using System.Net.Mail;

namespace Planner.Shared.Email;

/// <summary>
/// Controle op het afzenderadres vóór een automatisch antwoord (#1809). Een antwoord gaat alleen naar één
/// syntactisch geldig adres zonder lijstscheiders of control-tekens, en nooit naar een systeemadres
/// (no-reply, mailer-daemon, postmaster) waar een mens het niet leest en een antwoordlus kan ontstaan.
/// Ongeldig = geen automatisch antwoord; het voorstel gaat ter beoordeling (fail-safe). Pure logica.
/// </summary>
public static class AfzenderControle
{
    public const string Reden = "Afzenderadres niet geschikt voor een automatisch antwoord: ter beoordeling";

    private static readonly string[] SysteemLokaleDelen =
        ["no-reply", "noreply", "no_reply", "donotreply", "do-not-reply", "do_not_reply", "mailer-daemon", "postmaster"];

    public static bool IsGeschikt(string? afzender)
    {
        if (string.IsNullOrWhiteSpace(afzender) || afzender.Length > 254) return false;
        if (afzender != afzender.Trim()) return false;
        if (afzender.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is ',' or ';' or '<' or '>' or '"' or '(' or ')'))
            return false;

        if (!MailAddress.TryCreate(afzender, out var adres) || adres.Address != afzender || !string.IsNullOrEmpty(adres.DisplayName))
            return false;

        var lokaal = adres.User;
        if (lokaal.Length == 0 || adres.Host.IndexOf('.') < 0) return false;

        return !SysteemLokaleDelen.Any(s => lokaal.Equals(s, StringComparison.OrdinalIgnoreCase));
    }
}
