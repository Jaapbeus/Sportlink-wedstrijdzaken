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

    public static async Task<EmailVerwerkingsInstellingenLeesResultaat> ProbeerLezenAsync(
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> lezen, Action meldFout)
    {
        try { return await lezen(); }
        catch (Exception)
        {
            meldFout();
            return LeesVeilig(null, false);
        }
    }

    public static EmailVerwerkingsInstellingenLeesResultaat VanRij(System.Data.Common.DbDataReader reader) =>
        LeesVeilig(new EmailVerwerkingsInstellingen(
            reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3), reader.GetBoolean(4)), true);

    public static bool VerwerkingToegestaan(EmailVerwerkingsInstellingenLeesResultaat gelezen,
        Action<EmailVerwerkingsInstellingenLeesStatus> meldUitgeschakeld)
    {
        if (gelezen.Instellingen.VerwerkingIngeschakeld) return true;
        meldUitgeschakeld(gelezen.Status);
        return false;
    }

    private static EmailVerwerkingsInstellingenLeesResultaat Onveilig(
        EmailVerwerkingsInstellingenLeesStatus status) =>
        new(EmailVerwerkingsInstellingen.VeiligeStandaard, status);
}

public enum EmailVerwerkingsInstellingenValidatieFout
{
    OntvangerOngeldig,
    SchakelaarOngeldig,
    ClubRijOntbreekt,
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

public sealed record EmailOntvangerAuditWaardes(string? OudeWaarde, string? NieuweWaarde);

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

/// <summary>Typed, value-free error for the admin settings endpoint.</summary>
public sealed class EmailVerwerkingsInstellingenWijzigingException : Exception
{
    public EmailVerwerkingsInstellingenWijzigingException(EmailVerwerkingsInstellingenValidatieFout fout)
        : base(fout.ToString()) => Fout = fout;

    public EmailVerwerkingsInstellingenValidatieFout Fout { get; }

    public string VeiligeMelding => Fout switch
    {
        EmailVerwerkingsInstellingenValidatieFout.OntvangerOngeldig => "Vul hoogstens één kaal, geldig reviewadres in.",
        EmailVerwerkingsInstellingenValidatieFout.SchakelaarOngeldig => "Gebruik aan of uit voor de e-mailschakelaars.",
        EmailVerwerkingsInstellingenValidatieFout.ClubRijOntbreekt => "Instellingenrij voor deze club ontbreekt.",
        _ => "Bevestig expliciet dat reviewmodus wordt uitgeschakeld."
    };
}

/// <summary>Builds an admin update from an effective database snapshot.</summary>
public static class EmailVerwerkingsInstellingenWijziging
{
    public static bool BevatEmailVeld(IDictionary<string, string?> velden) =>
        velden.ContainsKey("EmailVerwerkingIngeschakeld")
        || velden.ContainsKey("EmailReviewModusActief")
        || velden.ContainsKey("EmailReviewOntvanger");

    public static EmailVerwerkingsInstellingen Bouw(
        EmailVerwerkingsInstellingenLeesResultaat huidig,
        IDictionary<string, string?> velden,
        bool reviewUitzettenBevestigd)
    {
        var verwerking = LeesSchakelaar(velden, "EmailVerwerkingIngeschakeld", huidig.Instellingen.VerwerkingIngeschakeld);
        var review = LeesSchakelaar(velden, "EmailReviewModusActief", huidig.Instellingen.ReviewModusActief);
        var ontvanger = velden.TryGetValue("EmailReviewOntvanger", out var ingevoerd)
            ? ingevoerd : huidig.Instellingen.ReviewOntvanger;
        var nieuw = huidig.Instellingen with
        {
            VerwerkingIngeschakeld = verwerking,
            ReviewModusActief = review,
            ReviewOntvanger = ontvanger,
            DoorBeheerderGewijzigd = true
        };
        var fouten = EmailVerwerkingsInstellingenValidatie.ValideerWijziging(
            huidig, nieuw, reviewUitzettenBevestigd);
        if (fouten.Count > 0)
            throw new EmailVerwerkingsInstellingenWijzigingException(fouten[0]);

        EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(ontvanger, out var schoon);
        if (velden.ContainsKey("EmailReviewOntvanger"))
            velden["EmailReviewOntvanger"] = schoon;
        return nieuw with { ReviewOntvanger = schoon };
    }

    public static void VulVeldenAan(IDictionary<string, string?> velden, EmailVerwerkingsInstellingen nieuw)
    {
        velden["EmailVerwerkingIngeschakeld"] = nieuw.VerwerkingIngeschakeld ? "1" : "0";
        velden["EmailReviewModusActief"] = nieuw.ReviewModusActief ? "1" : "0";
        velden["EmailReviewOntvanger"] = nieuw.ReviewOntvanger;
    }

    public static EmailOntvangerAuditWaardes AuditWaarden(string veld, string? oud, string? nieuw) =>
        string.Equals(veld, "EmailReviewOntvanger", StringComparison.OrdinalIgnoreCase)
            ? EmailOntvangerAuditRedactie.Redigeer(oud, nieuw)
            : new(oud, nieuw);

    private static bool LeesSchakelaar(IDictionary<string, string?> velden, string naam, bool huidigeWaarde)
    {
        if (!velden.TryGetValue(naam, out var invoer)) return huidigeWaarde;
        return invoer?.ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new EmailVerwerkingsInstellingenWijzigingException(
                EmailVerwerkingsInstellingenValidatieFout.SchakelaarOngeldig)
        };
    }
}

/// <summary>Tier-independent projection and review confirmation for the admin endpoint.</summary>
public static class EmailInstellingenAdminCore
{
    public static void VulGetResult(IDictionary<string, object?> result,
        EmailVerwerkingsInstellingenLeesResultaat gelezen)
    {
        result["EmailVerwerkingIngeschakeld"] = gelezen.Instellingen.VerwerkingIngeschakeld;
        result["EmailReviewModusActief"] = gelezen.Instellingen.ReviewModusActief;
        result["EmailReviewOntvanger"] = gelezen.Instellingen.ReviewOntvanger;
        result["EmailInstellingenStatus"] = gelezen.Status.ToString();
    }

    public static EmailInstellingenAdminWijziging? BereidVoor(
        EmailVerwerkingsInstellingenLeesResultaat oud,
        IDictionary<string, string?> changes, bool bevestigd)
    {
        if (oud.Status == EmailVerwerkingsInstellingenLeesStatus.RijOntbreekt)
            throw new EmailVerwerkingsInstellingenWijzigingException(
                EmailVerwerkingsInstellingenValidatieFout.ClubRijOntbreekt);
        var nieuw = EmailVerwerkingsInstellingenWijziging.Bouw(oud, changes, bevestigd);
        EmailVerwerkingsInstellingenWijziging.VulVeldenAan(changes, nieuw);
        return new(oud, nieuw);
    }

    public static async Task<EmailInstellingenAdminWijziging?> BereidVoorAsync(
        IDictionary<string, string?> changes, bool bevestigd,
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesVergrendeld)
    {
        if (!EmailVerwerkingsInstellingenWijziging.BevatEmailVeld(changes)) return null;
        return BereidVoor(await leesVergrendeld(), changes, bevestigd);
    }

    public static Dictionary<string, string?> LeesWaarden(System.Data.Common.DbDataReader reader)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            result[reader.GetName(i)] = AppSettingsNormalisatie.FormatteerAuditWaarde(reader.GetName(i), reader.GetValue(i));
        return result;
    }

    public static bool BevestigingAuditen(EmailInstellingenAdminWijziging wijziging) =>
        wijziging.Oud.Instellingen.ReviewModusActief && !wijziging.Nieuw.ReviewModusActief;
}

public sealed record EmailInstellingenAdminWijziging(
    EmailVerwerkingsInstellingenLeesResultaat Oud,
    EmailVerwerkingsInstellingen Nieuw);

/// <summary>Fail-safe timer gate before any Graph or mailbox operation.</summary>
public static class EmailTimerInstellingenGate
{
    public static async Task<EmailVerwerkingsInstellingenLeesResultaat?> LeesVoorVerwerkingAsync(
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesSnapshot,
        Action meldLeesfout, Action<EmailVerwerkingsInstellingenLeesStatus> meldUitgeschakeld)
    {
        EmailVerwerkingsInstellingenLeesResultaat gelezen;
        try { gelezen = await leesSnapshot(); }
        catch (Exception)
        {
            meldLeesfout();
            return null;
        }
        return EmailVerwerkingsInstellingenLezer.VerwerkingToegestaan(gelezen, meldUitgeschakeld)
            ? gelezen : null;
    }
}
