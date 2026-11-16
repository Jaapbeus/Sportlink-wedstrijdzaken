namespace Planner.Shared.Email;

/// <summary>Alleen een bewezen aantal inboxitems kan werk melden; ontbrekende metadata is een fout.</summary>
public static class EmailInboxMetadata
{
    public static bool HeeftWerk(int? ongelezenAantal)
    {
        if (ongelezenAantal is not int aantal)
            throw new InvalidOperationException("Inbox unread count niet beschikbaar");
        return aantal > 0;
    }
}
