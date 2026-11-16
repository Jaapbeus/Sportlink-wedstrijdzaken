namespace Planner.Shared.Email;

/// <summary>Metadata voor de pollpoort en de bestaande Graph-verzending bij verbindingsuitval.</summary>
public interface IEmailPollGraph : IReplyGraph
{
    /// <summary>Leest uitsluitend het aantal ongelezen inboxitems, zonder berichtinhoud of bericht-ID.</summary>
    Task<int> TelOngelezenInboxBerichtenAsync();
}

/// <summary>Alleen een bewezen aantal inboxitems kan werk melden; ontbrekende metadata is een fout.</summary>
public static class EmailInboxMetadata
{
    public static int LeesOngelezenAantal(int? ongelezenAantal) => ongelezenAantal
        ?? throw new InvalidOperationException("Inbox unread count niet beschikbaar");

    public static bool HeeftWerk(int? ongelezenAantal)
    {
        return LeesOngelezenAantal(ongelezenAantal) > 0;
    }
}
