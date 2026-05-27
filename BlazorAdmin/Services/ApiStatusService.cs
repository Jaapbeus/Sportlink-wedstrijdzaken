namespace BlazorAdmin.Services;

public enum BackendStatus { Ok, StartingUp, Fout }

/// <summary>
/// Houdt globale API-bereikbaarheidsstatus bij zodat MainLayout een banner kan tonen
/// wanneer de backend niet beschikbaar is of 5xx-fouten geeft.
/// </summary>
public class ApiStatusService
{
    public BackendStatus Status { get; private set; } = BackendStatus.Ok;
    public string? Foutmelding { get; private set; }
    public event Action? OnChanged;

    public void MeldStartend()
    {
        if (Status == BackendStatus.StartingUp) return;
        Status = BackendStatus.StartingUp;
        Foutmelding = null;
        OnChanged?.Invoke();
    }

    public void MeldFout(string melding)
    {
        if (Status == BackendStatus.Fout && Foutmelding == melding) return;
        Status = BackendStatus.Fout;
        Foutmelding = melding;
        OnChanged?.Invoke();
    }

    public void Herstel()
    {
        if (Status == BackendStatus.Ok) return;
        Status = BackendStatus.Ok;
        Foutmelding = null;
        OnChanged?.Invoke();
    }
}
