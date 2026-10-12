namespace Planner.Shared.Email;

public enum AntwoordLimietUitkomst
{
    Doorgaan,
    ConversatieLimietBereikt,
    BevestigingOpOnsAntwoord
}

/// <summary>
/// Beveiliging tegen antwoordlussen: een conversatie krijgt hoogstens
/// <see cref="MaxAutomatischeAntwoordenPerConversatie"/> automatische antwoorden, en een bevestiging
/// op een bericht dat zelf een reactie op ons antwoord is, wordt niet beantwoord. Pure logica; de
/// tier levert alleen het aantal eerder verzonden antwoorden in de conversatie.
/// </summary>
public static class AntwoordLimiet
{
    public const int MaxAutomatischeAntwoordenPerConversatie = 3;

    public const string RedenConversatieLimiet = "Antwoordlimiet per conversatie bereikt: geen automatisch antwoord";
    public const string RedenBevestigingOpAntwoord = "Bevestiging op een reactie op ons antwoord: geen automatisch antwoord";

    public static AntwoordLimietUitkomst Beoordeel(int eerderVerzondenInConversatie, bool isBevestiging)
    {
        if (eerderVerzondenInConversatie >= MaxAutomatischeAntwoordenPerConversatie)
            return AntwoordLimietUitkomst.ConversatieLimietBereikt;

        return isBevestiging && eerderVerzondenInConversatie >= 1
            ? AntwoordLimietUitkomst.BevestigingOpOnsAntwoord
            : AntwoordLimietUitkomst.Doorgaan;
    }

    public static string? Reden(AntwoordLimietUitkomst uitkomst) => uitkomst switch
    {
        AntwoordLimietUitkomst.ConversatieLimietBereikt => RedenConversatieLimiet,
        AntwoordLimietUitkomst.BevestigingOpOnsAntwoord => RedenBevestigingOpAntwoord,
        _ => null
    };
}
