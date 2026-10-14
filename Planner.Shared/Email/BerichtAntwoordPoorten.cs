namespace Planner.Shared.Email;

// Poorten van BerichtAntwoordGenerator (#1758, slice 6 van epic #1716). De generator is tier-onafhankelijk; de
// gegevens die hij leest staan per databasetier in eigen DTO's (plannerresponse, inkomend bericht, sjabloon) en
// de instellingen in een eigen cache. Die DTO's implementeren deze interfaces rechtstreeks, met dezelfde
// propertynamen; zo verandert er niets aan hun JSON-vorm en hoeft geen DTO naar Planner.Shared te verhuizen.
// De sjabloonopslag zelf (EmailTemplateService, database) blijft per tier: de generator krijgt alleen de inhoud
// van een al opgehaald sjabloon (IAntwoordSjabloon).

/// <summary>Afzender en onderwerp van het bericht waarop wordt geantwoord.</summary>
public interface IAntwoordAfzender
{
    string Afzender { get; }
    string AfzenderNaam { get; }
    string Onderwerp { get; }
}

/// <summary>
/// Een inkomend bericht zoals de fase-orkestratie van de e-mailverwerker het ziet (#1716 slice 9): de
/// Graph-sleutel plus afzender en onderwerp. Het per tier eigen <c>InkomendBericht</c> implementeert dit;
/// de rest van het bericht (body, headers) leest alleen de tier.
/// </summary>
public interface IInkomendBericht : IAntwoordAfzender
{
    string MessageId { get; }
}

/// <summary>Een vrij tijdvenster op één veld uit het beschikbaarheidsresponse.</summary>
public interface IAntwoordVenster
{
    string VeldNaam { get; }
    string Van { get; }
    string Tot { get; }
    string? Opmerking { get; }
    string? VeldType { get; }
}

/// <summary>Het beschikbaarheidsresponse van de planner, voor zover het antwoord het leest.</summary>
public interface IAntwoordBeschikbaarheid
{
    bool Beschikbaar { get; }
    SlotToewijzing? Toewijzing { get; }
    /// <summary>Waar als het team op de datum al een wedstrijd heeft (dan is het veld niet gecontroleerd).</summary>
    bool HeeftTeamConflict { get; }
    string? Reden { get; }
    string? GecontroleerdDagdeel { get; }
    List<SlotToewijzing> Alternatieven { get; }
    IReadOnlyList<IAntwoordVenster>? BeschikbareVensters { get; }
    List<string> Waarschuwingen { get; }
}

/// <summary>Een gevonden wedstrijd uit het programma.</summary>
public interface IAntwoordWedstrijd
{
    string Wedstrijd { get; }
    string Datum { get; }
    string AanvangsTijd { get; }
    string? VeldNaam { get; }
}

/// <summary>De herplanopties van de planner, voor zover het antwoord ze leest.</summary>
public interface IAntwoordHerplanOpties
{
    List<SlotToewijzing> Alternatieven { get; }
}

/// <summary>Een al opgehaald antwoordsjabloon (sleutel, onderwerp en tekst met placeholders).</summary>
public interface IAntwoordSjabloon
{
    string Key { get; }
    string Onderwerp { get; }
    string Body { get; }
}

/// <summary>
/// De clubinstellingen voor de ondertekening en de afsluitzin. Per tier geleverd: uit de meegegeven
/// club-snapshot van de e-mailtester, of anders uit de procesbrede instellingencache (#677). Een getter wordt pas
/// gelezen op het moment dat de generator de waarde nodig heeft.
/// </summary>
public interface IAntwoordInstellingen
{
    string? EmailVoetnoot { get; }
    string? PlannerAfzenderNaam { get; }
    string? CoordinatorNaam { get; }
    string? CoordinatorFunctie { get; }
    /// <summary>Foutmelding als <see cref="PlannerAfzenderNaam"/> nodig is maar ontbreekt (geen fallbacknaam in code).</summary>
    string OntbrekendeAfzenderNaamMelding { get; }
}
