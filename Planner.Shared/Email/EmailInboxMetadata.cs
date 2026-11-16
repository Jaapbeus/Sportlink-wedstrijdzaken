namespace Planner.Shared.Email;

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
