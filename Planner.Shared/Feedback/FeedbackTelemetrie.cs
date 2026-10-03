namespace Planner.Shared.Feedback;

/// <summary>
/// Technische context bij een feedbackmelding (#764): laatste console-fouten, mislukte
/// API-aanroepen, navigatiespoor en browser. Standaard aan, vóór verzending zichtbaar in een paneel
/// en per melding uit te zetten.
///
/// <para>
/// Alleen BCL: dit bestand wordt als gelinkt bronbestand ook in BlazorAdmin gecompileerd (#1461),
/// zodat de browser en de server exact hetzelfde contract en dezelfde redactie gebruiken.
/// </para>
/// </summary>
public sealed class FeedbackTelemetrie
{
    public List<string> ConsoleFouten { get; set; } = [];
    public List<FeedbackApiFout> MislukteAanroepen { get; set; } = [];
    public List<string> Navigatiespoor { get; set; } = [];
    public string? Browser { get; set; }
    public int? Schermbreedte { get; set; }

    public bool IsLeeg =>
        ConsoleFouten.Count == 0 && MislukteAanroepen.Count == 0 && Navigatiespoor.Count == 0
        && string.IsNullOrWhiteSpace(Browser) && Schermbreedte is null;

    /// <summary>
    /// De letterlijke regels zoals ze worden verstuurd en bewaard, per bron. Het contextpaneel toont
    /// precies deze tekst — één bron, dus geen verschil tussen wat de gebruiker ziet en wat er gebeurt.
    /// </summary>
    public List<(string Bron, string Tekst)> NaarBronnen()
    {
        var bronnen = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(Browser) || Schermbreedte is not null)
        {
            var delen = new List<string>();
            if (!string.IsNullOrWhiteSpace(Browser)) delen.Add("Browser: " + Browser);
            if (Schermbreedte is { } w) delen.Add($"Scherm: {w} breed");
            bronnen.Add(("omgeving", string.Join("\n", delen)));
        }
        if (ConsoleFouten.Count > 0)
            bronnen.Add(("console", string.Join("\n", ConsoleFouten.Select(f => "Console-fout: " + f))));
        if (MislukteAanroepen.Count > 0)
            bronnen.Add(("netwerk", string.Join("\n", MislukteAanroepen.Select(call =>
                $"Mislukte aanroep: {call.Methode} {call.Pad} → {call.Status}"
                + (call.TijdUtc is { } t ? $" ({t:HH:mm:ss} UTC)" : "")
                + (string.IsNullOrWhiteSpace(call.CorrelatieId) ? "" : $", volgnr {call.CorrelatieId}")))));
        if (Navigatiespoor.Count > 0)
            bronnen.Add(("navigatie", "Route ervoor: " + string.Join(" → ", Navigatiespoor)));
        return bronnen;
    }

    public string NaarTekst() => string.Join("\n", NaarBronnen().Select(b => b.Tekst));
}

public sealed class FeedbackApiFout
{
    public string Methode { get; set; } = "";
    public string Pad { get; set; } = "";
    public int Status { get; set; }
    public DateTime? TijdUtc { get; set; }
    public string? CorrelatieId { get; set; }
}

/// <summary>
/// Redactie en begrenzing van een <see cref="FeedbackTelemetrie"/>. Draait client-side vóór
/// verzending (wat het paneel toont) en server-side bij ontvangst (de client wordt niet vertrouwd).
/// Idempotent: nogmaals toepassen verandert het resultaat niet.
/// </summary>
public static class FeedbackTelemetrieSaneerder
{
    public const int MaxItemsPerSoort = 5;
    public const int MaxTekenPerItem = 300;

    /// <param name="invoer">De ontvangen telemetrie; <c>null</c> geeft <c>null</c> terug.</param>
    /// <param name="melderNaam">Weergavenaam van de melder (server-side bekend); wordt uit vrije tekst gehaald.</param>
    public static FeedbackTelemetrie? Saneer(FeedbackTelemetrie? invoer, string? melderNaam = null)
    {
        if (invoer is null) return null;

        string Schoon(string? tekst) =>
            FeedbackRedactie.RedigeerNaam(FeedbackRedactie.Redigeer(tekst, MaxTekenPerItem), melderNaam);

        var uit = new FeedbackTelemetrie
        {
            ConsoleFouten = [.. (invoer.ConsoleFouten ?? []).Where(f => !string.IsNullOrWhiteSpace(f))
                .TakeLast(MaxItemsPerSoort).Select(Schoon)],
            MislukteAanroepen = [.. (invoer.MislukteAanroepen ?? []).Where(c => c is not null)
                .TakeLast(MaxItemsPerSoort).Select(c => new FeedbackApiFout
                {
                    Methode = Schoon(c.Methode).ToUpperInvariant() is { Length: <= 10 } m ? m : "?",
                    Pad = FeedbackRedactie.RedigeerNaam(FeedbackRedactie.RedigeerRoute(c.Pad), melderNaam),
                    Status = c.Status is >= 0 and <= 999 ? c.Status : 0,
                    TijdUtc = c.TijdUtc,
                    CorrelatieId = string.IsNullOrWhiteSpace(c.CorrelatieId) ? null
                        : new string([.. c.CorrelatieId.Where(char.IsLetterOrDigit).Take(40)])
                })],
            Navigatiespoor = [.. (invoer.Navigatiespoor ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
                .TakeLast(MaxItemsPerSoort).Select(p => FeedbackRedactie.RedigeerNaam(FeedbackRedactie.RedigeerRoute(p), melderNaam))],
            Browser = string.IsNullOrWhiteSpace(invoer.Browser) ? null : Schoon(invoer.Browser),
            Schermbreedte = invoer.Schermbreedte is > 0 and < 20000 ? invoer.Schermbreedte : null
        };
        return uit.IsLeeg ? null : uit;
    }
}
