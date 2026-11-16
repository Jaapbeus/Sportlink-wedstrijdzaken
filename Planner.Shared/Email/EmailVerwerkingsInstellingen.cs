namespace Planner.Shared.Email;

/// <summary>Typed per-club switches for processing incoming email.</summary>
public sealed record EmailVerwerkingsInstellingen(
    bool VerwerkingIngeschakeld,
    bool ReviewModusActief,
    string? ReviewOntvanger,
    bool EenmaligeImportVoltooid,
    bool DoorBeheerderGewijzigd)
{
    public static EmailVerwerkingsInstellingen VeiligeStandaard { get; } =
        new(false, true, null, false, false);

    public bool IsGeinitialiseerd => EenmaligeImportVoltooid || DoorBeheerderGewijzigd;

    public EmailVerwerkingsInstellingen NaBeheerderWijziging() =>
        this with { DoorBeheerderGewijzigd = true };

    public override string ToString() =>
        $"EmailVerwerkingsInstellingen {{ VerwerkingIngeschakeld = {VerwerkingIngeschakeld}, " +
        $"ReviewModusActief = {ReviewModusActief}, ReviewOntvanger = " +
        $"{(string.IsNullOrWhiteSpace(ReviewOntvanger) ? EmailOntvangerAuditRedactie.NietIngesteld : EmailOntvangerAuditRedactie.Ingesteld)}, " +
        $"EenmaligeImportVoltooid = {EenmaligeImportVoltooid}, DoorBeheerderGewijzigd = {DoorBeheerderGewijzigd} }}";
}

public enum EmailVerwerkingsInstellingenLeesStatus
{
    Beschikbaar,
    RijOntbreekt,
    NietGeinitialiseerd,
    Onleesbaar,
    OntvangerOngeldig
}

public sealed record EmailVerwerkingsInstellingenLeesResultaat(
    EmailVerwerkingsInstellingen Instellingen,
    EmailVerwerkingsInstellingenLeesStatus Status);

/// <summary>Fail-closed interpretation of a database read; it never consults process settings.</summary>
public static class EmailVerwerkingsInstellingenLezer
{
    public static EmailVerwerkingsInstellingenLeesResultaat LeesVeilig(
        EmailVerwerkingsInstellingen? opgeslagen,
        bool databaseLezingGelukt)
    {
        if (!databaseLezingGelukt)
            return Onveilig(EmailVerwerkingsInstellingenLeesStatus.Onleesbaar);

        if (opgeslagen is null)
            return Onveilig(EmailVerwerkingsInstellingenLeesStatus.RijOntbreekt);

        if (!opgeslagen.IsGeinitialiseerd)
            return Onveilig(EmailVerwerkingsInstellingenLeesStatus.NietGeinitialiseerd);

        if (!EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(opgeslagen.ReviewOntvanger, out var ontvanger))
            return Onveilig(EmailVerwerkingsInstellingenLeesStatus.OntvangerOngeldig);

        return new(
            opgeslagen with { ReviewOntvanger = ontvanger },
            EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);
    }

    private static EmailVerwerkingsInstellingenLeesResultaat Onveilig(
        EmailVerwerkingsInstellingenLeesStatus status) =>
        new(EmailVerwerkingsInstellingen.VeiligeStandaard, status);
}

public enum EmailVerwerkingsInstellingenValidatieFout
{
    OntvangerOngeldig,
    BevestigingReviewUitzettenVereist
}

public static class EmailVerwerkingsInstellingenValidatie
{
    public static IReadOnlyList<EmailVerwerkingsInstellingenValidatieFout> ValideerWijziging(
        EmailVerwerkingsInstellingenLeesResultaat huidig,
        EmailVerwerkingsInstellingen nieuw,
        bool reviewUitzettenBevestigd)
    {
        var fouten = new List<EmailVerwerkingsInstellingenValidatieFout>();
        if (!TryNormaliseerOntvanger(nieuw.ReviewOntvanger, out _))
            fouten.Add(EmailVerwerkingsInstellingenValidatieFout.OntvangerOngeldig);

        var wasReviewActief = huidig.Status != EmailVerwerkingsInstellingenLeesStatus.Beschikbaar
            || huidig.Instellingen.ReviewModusActief;
        if (wasReviewActief && !nieuw.ReviewModusActief && !reviewUitzettenBevestigd)
            fouten.Add(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);

        return fouten;
    }

    public static bool TryNormaliseerOntvanger(string? waarde, out string? ontvanger)
    {
        ontvanger = null;
        if (string.IsNullOrWhiteSpace(waarde))
            return true;

        var opgeschoond = waarde.Trim();
        var resultaat = OntvangerParser.Parse(opgeschoond);
        // The shared parser also accepts display names and recipient lists. This setting accepts
        // exactly one bare address; never expose FoutMelding because it may repeat the input.
        if (!resultaat.IsValid
            || resultaat.Emailadressen.Count != 1
            || !string.Equals(resultaat.Emailadressen[0], opgeschoond, StringComparison.OrdinalIgnoreCase))
            return false;

        ontvanger = resultaat.Emailadressen[0];
        return true;
    }
}

public sealed record EmailOntvangerAuditWaardes(string OudeWaarde, string NieuweWaarde);

/// <summary>Audit projection that never returns or persists an email address.</summary>
public static class EmailOntvangerAuditRedactie
{
    public const string Ingesteld = "[ingesteld]";
    public const string NietIngesteld = "[niet ingesteld]";

    public static EmailOntvangerAuditWaardes Redigeer(string? oud, string? nieuw) =>
        new(IsIngesteld(oud) ? Ingesteld : NietIngesteld,
            IsIngesteld(nieuw) ? Ingesteld : NietIngesteld);

    private static bool IsIngesteld(string? waarde) => !string.IsNullOrWhiteSpace(waarde);
}

public enum EmailVerwerkingsInstellingenImportBeslissing
{
    Importeren,
    BronOntbreekt,
    AlGeimporteerd,
    BeheerderHeeftGewijzigd,
    HuidigeWaardenGewijzigd
}

/// <summary>Pure one-time import decision. The caller must apply it transactionally per club.</summary>
public static class EmailVerwerkingsInstellingenImport
{
    public static EmailVerwerkingsInstellingenImportBeslissing Bepaal(
        EmailVerwerkingsInstellingen? huidig,
        bool legacyBronBeschikbaar)
    {
        if (huidig?.EenmaligeImportVoltooid == true)
            return EmailVerwerkingsInstellingenImportBeslissing.AlGeimporteerd;

        if (huidig?.DoorBeheerderGewijzigd == true)
            return EmailVerwerkingsInstellingenImportBeslissing.BeheerderHeeftGewijzigd;

        if (huidig is not null && HeeftAfwijkendeWaarden(huidig))
            return EmailVerwerkingsInstellingenImportBeslissing.HuidigeWaardenGewijzigd;

        return legacyBronBeschikbaar
            ? EmailVerwerkingsInstellingenImportBeslissing.Importeren
            : EmailVerwerkingsInstellingenImportBeslissing.BronOntbreekt;
    }

    private static bool HeeftAfwijkendeWaarden(EmailVerwerkingsInstellingen instellingen) =>
        instellingen.VerwerkingIngeschakeld
        || !instellingen.ReviewModusActief
        || !string.IsNullOrWhiteSpace(instellingen.ReviewOntvanger);
}
