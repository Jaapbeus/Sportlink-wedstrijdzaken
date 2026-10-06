using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email.Trace;

/// <summary>
/// Wat er per verwerkt bericht in <c>planner.EmailTrace</c> wordt opgeslagen (#1568, deel B).
/// Bevat uitsluitend PII-arme velden: de trace-JSON komt uit <see cref="BeslissingsTrace.ToJson"/>.
/// </summary>
public sealed record EmailTraceRecord(
    int VerwerkingId,
    string ClubCode,
    string VerzoekType,
    string Zekerheid,
    string? SjabloonSleutel,
    string TraceJson,
    string AppVersie)
{
    public const int MaxAppVersieLengte = 20;
    public const int MaxSjabloonLengte = 60;
    public const int MaxVerzoekTypeLengte = 50;

    /// <summary>Bouwt het opslagrecord; de samenvattende kolommen worden uit de trace afgeleid.</summary>
    public static EmailTraceRecord Van(
        int verwerkingId, string clubCode, string verzoekType, BeslissingsTrace trace, string appVersie)
    {
        var sjabloon = trace.Stappen
            .Where(s => s.Code == TraceCodes.Sjabloon)
            .Select(s => s.Details.TryGetValue("sjabloon", out var v) ? v : null)
            .LastOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return new EmailTraceRecord(
            verwerkingId,
            clubCode,
            Kap(verzoekType, MaxVerzoekTypeLengte),
            ZekerheidVan(trace),
            sjabloon is null ? null : Kap(sjabloon, MaxSjabloonLengte),
            trace.ToJson(),
            Kap(appVersie, MaxAppVersieLengte));
    }

    /// <summary>Zeker als het eindoordeel zeker is; Mislukt zodra een stap mislukte; anders Onzeker.</summary>
    public static string ZekerheidVan(BeslissingsTrace trace)
        => trace.Oordeel.IsZeker ? nameof(ZekerheidsNiveau.Zeker)
            : trace.Stappen.Any(s => s.Zekerheid == ZekerheidsNiveau.Mislukt && s.Code != TraceCodes.Eindoordeel
                    && s.Code is not (TraceCodes.TeamHerkenning or TraceCodes.TegenstanderHerkenning or TraceCodes.OpponentTeamHerkenning))
                ? nameof(ZekerheidsNiveau.Mislukt)
                : nameof(ZekerheidsNiveau.Onzeker);

    /// <summary>Versie van de draaiende assembly, voor reproduceerbaarheid van de trace.</summary>
    public static string VersieVan(Assembly assembly)
        => assembly.GetName().Version?.ToString() ?? "onbekend";

    private static string Kap(string waarde, int max) => waarde.Length <= max ? waarde : waarde[..max];
}

/// <summary>Een opgeslagen trace zoals het admin-endpoint hem teruggeeft (zonder body, afzender of onderwerp).</summary>
/// <param name="Status">Status van de verwerking; <c>null</c> als de verwerking inmiddels is opgeruimd.</param>
public sealed record EmailTraceAntwoord(
    int VerwerkingId,
    string VerzoekType,
    string? Status,
    DateTime? OntvangstDatum,
    DateTime Aangemaakt,
    string Zekerheid,
    string? SjabloonSleutel,
    string AppVersie,
    JsonElement? Trace)
{
    /// <summary>
    /// Leest één rij met kolomvolgorde verzoektype, status, ontvangstdatum, aangemaakt, zekerheid, sjabloonsleutel,
    /// appversie, tracejson. Eén mapping voor beide tiers; datums worden als UTC gemarkeerd (DB schrijft UTC).
    /// </summary>
    public static EmailTraceAntwoord VanRij(int verwerkingId, IDataRecord r) => new(
        verwerkingId,
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
        DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6),
        ParseTrace(r.GetString(7)));

    /// <summary>Parset de opgeslagen JSON; <c>null</c> bij een beschadigde waarde in plaats van een fout.</summary>
    public static JsonElement? ParseTrace(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// De trace is een hulpmiddel, geen onderdeel van de verwerking: een mislukte opslag mag het bericht
/// nooit laten falen (#1568). Eén gedeelde plek voor die garantie, zodat beide tiers hem identiek hebben.
/// </summary>
public static class EmailTraceOpslag
{
    /// <summary>Voert <paramref name="opslaan"/> uit en meldt een fout via <paramref name="meldFout"/> in plaats van te gooien.</summary>
    /// <returns><c>true</c> als de opslag slaagde.</returns>
    public static async Task<bool> BewaarVeiligAsync(Func<Task> opslaan, Action<Exception> meldFout)
    {
        try
        {
            await opslaan();
            return true;
        }
        catch (Exception ex)
        {
            try { meldFout(ex); } catch { /* de melding zelf mag de verwerking evenmin breken */ }
            return false;
        }
    }

    /// <summary>
    /// Zoals <see cref="BewaarVeiligAsync(Func{Task}, Action{Exception})"/>, met de standaardmelding: alleen
    /// het fouttype en het verwerkings-id, nooit de foutmelding zelf (kan data bevatten).
    /// </summary>
    public static Task<bool> BewaarVeiligAsync(Func<Task> opslaan, ILogger log, int verwerkingId)
        => BewaarVeiligAsync(opslaan,
            ex => log.LogWarning("Trace opslaan mislukt voor verwerking {Id} ({Fouttype})", verwerkingId, ex.GetType().Name));

    /// <summary>
    /// Voert de verwerking uit en bewaart de trace daarna altijd, ook als de verwerking faalt (dan met een
    /// mislukte stap). De oorspronkelijke fout wordt doorgegeven; een mislukte opslag nooit (#1568).
    /// </summary>
    public static async Task<T> MetTraceAsync<T>(
        TraceBuilder trace, Func<Task<T>> verwerk, Func<BeslissingsTrace, Task> opslaan, ILogger log, int verwerkingId)
    {
        try
        {
            return await verwerk();
        }
        catch
        {
            trace.Voeg(TraceCodes.VerwerkingFout, "Verwerking", "Mislukt (zie e-mail-log)", ZekerheidsNiveau.Mislukt);
            throw;
        }
        finally
        {
            var klaar = trace.Bouw();
            await BewaarVeiligAsync(() => opslaan(klaar), log, verwerkingId);
        }
    }

    /// <summary>De ene tekst voor "buiten scope" in de korte trace, zodat beide tiers dezelfde reden tonen.</summary>
    public const string BuitenScopeReden = "Buiten scope: geen antwoord door de planner";

    /// <summary>Korte trace voor berichten die de planner niet bereiken (buiten scope): classificatie + reden.</summary>
    public static BeslissingsTrace BouwKorteTrace(
        string type, string? team, string? tegenstander, int aantalDatums, string? aanvangsTijd, string reden)
    {
        var builder = new TraceBuilder()
            .Classificatie(type, team, tegenstander, aantalDatums, aanvangsTijd)
            .Voeg(TraceCodes.Tak, "Gekozen verwerkingstak", "Niet verwerkt door de planner", ZekerheidsNiveau.Zeker,
                new[] { new KeyValuePair<string, string?>("reden", reden) });
        return builder.Bouw();
    }
}
