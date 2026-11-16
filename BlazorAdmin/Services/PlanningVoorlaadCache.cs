using BlazorAdmin.Models;
using Planner.Shared.Planning;

namespace BlazorAdmin.Services;

/// <summary>
/// Alleen geheugen van de geopende Planning-pagina: maximaal acht dagen, twee minuten oud.
/// Een datumwissel toont de kopie meteen en controleert daarna opnieuw. Geen browseropslag.
/// Lopende aanvragen voor dezelfde dag worden gedeeld; vooruitladen is serieel en onderbreekbaar.
/// </summary>
public sealed class PlanningVoorlaadCache : IDisposable
{
    private readonly Func<string, CancellationToken, Task<ApiResult<VeldbezettingAntwoordDto>>> _haal;
    private readonly Func<DateTime> _nu;
    private readonly Func<string, CancellationToken, Task<ApiResult<VeldbezettingAntwoordDto>>> _haalVooraf;
    private readonly Dictionary<string, (VeldbezettingAntwoordDto Data, DateTime Tot)> _dagen = new();
    private readonly Dictionary<string, Task<ApiResult<VeldbezettingAntwoordDto>>> _lopend = new();
    private readonly SemaphoreSlim _achtergrond = new(1);
    private CancellationTokenSource _clubLeven = new();
    private string? _club;
    private int _generatie;
    private int _voorlaadGeneratie;
    private bool _disposed;
    public static readonly TimeSpan Geldigheid = TimeSpan.FromMinutes(2);

    public PlanningVoorlaadCache(Func<string, CancellationToken, Task<ApiResult<VeldbezettingAntwoordDto>>> haal,
        Func<DateTime>? nu = null,
        Func<string, CancellationToken, Task<ApiResult<VeldbezettingAntwoordDto>>>? haalVooraf = null)
    {
        _haal = haal;
        _haalVooraf = haalVooraf ?? haal;
        _nu = nu ?? (() => DateTime.UtcNow);
    }

    public void KiesClub(string? club)
    {
        if (_club == club) return;
        Wis();
        _club = club;
    }

    public void StopVoorladen() => _voorlaadGeneratie++;

    /// <summary>Ook bij een eigen mutatie: oude data en lopende antwoorden mogen de wijziging niet terugdraaien.</summary>
    public void Wis()
    {
        if (_disposed) return;
        _generatie++;
        StopVoorladen();
        _clubLeven.Cancel();
        _clubLeven.Dispose();
        _clubLeven = new();
        _dagen.Clear();
        _lopend.Clear();
    }

    public VeldbezettingAntwoordDto? Lees(string datum)
        => _dagen.TryGetValue(datum, out var dag) && dag.Tot > _nu() ? dag.Data : null;

    public Task<ApiResult<VeldbezettingAntwoordDto>> LaadAsync(string datum, bool vooraf = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lopend.TryGetValue(datum, out var taak)) return taak;
        var generatie = _generatie;
        var klaar = new TaskCompletionSource<ApiResult<VeldbezettingAntwoordDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lopend[datum] = klaar.Task;
        _ = HaalAsync(datum, generatie, _clubLeven.Token, vooraf, klaar);
        return klaar.Task;
    }

    /// <summary>Een gedeelde stille tokenfout krijgt uitsluitend op de zichtbare pagina één interactieve herkansing.</summary>
    public async Task<ApiResult<VeldbezettingAntwoordDto>> LaadZichtbaarAsync(string datum)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ct = _clubLeven.Token;
        var antwoord = await LaadAsync(datum);
        if (antwoord.Code != AdminApiTransport.VoorlaadSessieVerlopen || ct.IsCancellationRequested || _disposed)
            return antwoord;
        return await _haal(datum, ct);
    }

    private async Task HaalAsync(string datum, int generatie, CancellationToken ct, bool vooraf,
        TaskCompletionSource<ApiResult<VeldbezettingAntwoordDto>> klaar)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var antwoord = await (vooraf ? _haalVooraf : _haal)(datum, ct);
            if (generatie == _generatie && !ct.IsCancellationRequested)
            {
                _dagen.Remove(datum);
                if (antwoord.Success && antwoord.Data?.Sportlink is { Geraadpleegd: true, GeraadpleegdOpUtc: not null })
                {
                    if (_dagen.Count >= 8) _dagen.Remove(_dagen.MinBy(x => x.Value.Tot).Key);
                    _dagen[datum] = (antwoord.Data, _nu() + Geldigheid);
                }
            }
            klaar.TrySetResult(antwoord);
        }
        catch (OperationCanceledException) { klaar.TrySetCanceled(ct); }
        catch (Exception ex) { klaar.TrySetException(ex); }
        finally
        {
            if (generatie == _generatie) _lopend.Remove(datum);
        }
    }

    public async Task LaadVolgendeZaterdagenAsync(DateOnly vanaf)
    {
        if (_disposed) return;
        var generatie = ++_voorlaadGeneratie;
        var ct = _clubLeven.Token;
        try
        {
            await _achtergrond.WaitAsync(ct);
            try
            {
                foreach (var datum in PlanningVooruitblik.ZaterdagenVanaf(vanaf))
                {
                    if (_disposed || ct.IsCancellationRequested || generatie != _voorlaadGeneratie) break;
                    var sleutel = datum.ToString("yyyy-MM-dd");
                    if (Lees(sleutel) != null) continue;
                    var antwoord = await LaadAsync(sleutel, vooraf: true);
                    if (!antwoord.Success || antwoord.Data?.Sportlink?.Geraadpleegd != true) break;
                }
            }
            finally { _achtergrond.Release(); }
        }
        catch (Exception) { /* Vooruitladen mag de zichtbare pagina nooit laten mislukken. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Wis();
        _disposed = true;
        _clubLeven.Dispose();
        // Een al lopende achtergrondaanvraag moet zijn semaphore nog kunnen vrijgeven.
    }
}
