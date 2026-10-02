namespace Planner.Shared;

/// <summary>Eén rij uit de teambegeleiding-CSV, al genormaliseerd naar de canonieke kolommen.</summary>
public sealed record TeambegeleidingCsvRij(
    string? Team, string? LeeftijdscategorieTeam, string? Teamrol, string? Functie,
    string? Naam, string? Emailadres, string? Telefoonnummer);

public sealed class TeambegeleidingCsvResultaat
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public List<string> Ontbreekt { get; set; } = [];
    public List<string> Herkend { get; set; } = [];
    public List<string> Waarschuwingen { get; set; } = [];
    public List<TeambegeleidingCsvRij> Rows { get; set; } = [];
}

/// <summary>
/// Enige plek voor de tier-onafhankelijke CSV-verwerking van de teambegeleiding-import (#1360):
/// kolomaliassen, naam-/telefoonsamenstelling, deduplicatie en kolomlengte-validatie. Stond tot
/// #1360 woordelijk in beide <c>AdminTeambegeleidingFunction</c>-bestanden (codekwaliteitsregel 1).
/// De CSV-inhoud wordt in-memory verwerkt en nooit opgeslagen (AVG).
/// </summary>
public static class TeambegeleidingCsv
{
    private static readonly Dictionary<string, string[]> KolomAliassen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Team"]                   = ["Team", "Teamnaam", "Team naam"],
        ["Teamrol"]                = ["Teamrol", "Rol", "Rol in team", "Rol team"],
        ["Functie"]                = ["Functie", "Functie in team"],
        ["Roepnaam"]               = ["Roepnaam", "Voornaam", "First name"],
        ["Achternaam"]             = ["Achternaam", "Familienaam", "Last name"],
        ["Emailadres"]             = ["E-mailadres", "Email", "E-mail", "Emailadres", "Mailadres"],
        ["LeeftijdscategorieTeam"] = ["Leeftijdscategorie team", "Leeftijdscategorie", "Age category"],
        ["Tussenvoegsel"]          = ["Tussenvoegsel(s)", "Tussenvoegsel", "Infix", "Tussenv."],
        ["MobielNummer"]           = ["Mobiel nummer", "Mobiel", "Mobiele telefoon", "Mobile"],
        ["TelefoonnummerKolom"]    = ["Telefoonnummer", "Telefoon", "Vaste telefoon", "Phone"],
    };

    // Functie is bewust niet vereist: exports van vóór #1360 hebben de kolom niet.
    private static readonly string[] VereistKolommen = ["Team", "Teamrol", "Roepnaam", "Achternaam", "Emailadres"];

    // #1131: kolomgrenzen zoals in Database/avg/Tables/Teambegeleiding.sql (en Postgres-migraties
    // 002 + 033). Bij een schemawijziging aan die tabel dit synchroon houden.
    private const int TeamMaxLength = 100;
    private const int LeeftijdscategorieTeamMaxLength = 50;
    private const int TeamrolMaxLength = 100;
    private const int FunctieMaxLength = 150;
    private const int NaamMaxLength = 300;
    private const int EmailadresMaxLength = 200;
    private const int TelefoonnummerMaxLength = 50;

    public static TeambegeleidingCsvResultaat Parse(string csvContent)
    {
        var result = new TeambegeleidingCsvResultaat();
        var lines = csvContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count < 2)
        {
            result.Error = "CSV bevat geen gegevensrijen.";
            return result;
        }

        var mapping = BouwKolomMapping(SplitCsvLine(lines[0]));
        var ontbreekt = VereistKolommen.Where(v => !mapping.ContainsKey(v)).ToList();
        if (ontbreekt.Count > 0)
        {
            result.Ontbreekt = ontbreekt;
            result.Error = $"Vereiste kolommen niet gevonden: {string.Join(", ", ontbreekt)}";
            return result;
        }

        result.Herkend = [.. mapping.Keys];
        VoegKolomWaarschuwingenToe(mapping, result.Waarschuwingen);

        var rows = BouwRijen(lines, mapping);
        var (deduped, duplicaten) = DedupliceerRijen(rows);
        result.Rows = deduped;
        if (duplicaten > 0)
            result.Waarschuwingen.Add(
                $"{duplicaten} exacte duplicaat-rij{(duplicaten == 1 ? "" : "en")} overgeslagen (zelfde team, rol, functie, naam en e-mailadres).");

        result.IsValid = true;
        return result;
    }

    /// <summary>
    /// Valideert elke rij tegen de kolomgrenzen van <c>avg.Teambegeleiding</c> vóórdat er iets
    /// destructiefs (DELETE/INSERT) gebeurt (#1131). Rijnummers zijn 1-based en tellen de header
    /// mee (rij 1 = header, rij 2 = eerste datarij).
    /// </summary>
    public static List<string> ValideerKolomLengtes(IReadOnlyList<TeambegeleidingCsvRij> rows)
    {
        var fouten = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            var rijNummer = i + 2;
            var row = rows[i];
            VoegLengteFoutToe(fouten, rijNummer, "Team", row.Team, TeamMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Leeftijdscategorie team", row.LeeftijdscategorieTeam, LeeftijdscategorieTeamMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Teamrol", row.Teamrol, TeamrolMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Functie", row.Functie, FunctieMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Naam", row.Naam, NaamMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Emailadres", row.Emailadres, EmailadresMaxLength);
            VoegLengteFoutToe(fouten, rijNummer, "Telefoonnummer", row.Telefoonnummer, TelefoonnummerMaxLength);
        }
        return fouten;
    }

    private static void VoegLengteFoutToe(List<string> fouten, int rijNummer, string kolomNaam, string? waarde, int maxLength)
    {
        if (waarde != null && waarde.Length > maxLength)
            fouten.Add($"Rij {rijNummer}, kolom '{kolomNaam}': {waarde.Length} tekens (maximaal {maxLength}).");
    }

    private static void VoegKolomWaarschuwingenToe(Dictionary<string, int> mapping, List<string> waarschuwingen)
    {
        if (!mapping.ContainsKey("MobielNummer") && !mapping.ContainsKey("TelefoonnummerKolom"))
            waarschuwingen.Add("Geen telefoonnummer-kolom gevonden — Telefoonnummer wordt leeg.");
        if (!mapping.ContainsKey("LeeftijdscategorieTeam"))
            waarschuwingen.Add("Kolom 'Leeftijdscategorie team' niet gevonden — wordt leeg.");
        if (!mapping.ContainsKey("Functie"))
            waarschuwingen.Add("Kolom 'Functie' niet gevonden — badge toont alleen Teamrol.");
    }

    private static Dictionary<string, int> BouwKolomMapping(string[] headers)
    {
        var mapping = new Dictionary<string, int>();
        foreach (var (canonical, aliases) in KolomAliassen)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                if (aliases.Any(a => string.Equals(a, headers[i], StringComparison.OrdinalIgnoreCase)))
                {
                    mapping[canonical] = i;
                    break;
                }
            }
        }
        return mapping;
    }

    private static List<TeambegeleidingCsvRij> BouwRijen(List<string> lines, Dictionary<string, int> mapping)
    {
        var rows = new List<TeambegeleidingCsvRij>();
        for (int i = 1; i < lines.Count; i++)
        {
            var fields = SplitCsvLine(lines[i]);

            string? GetVeld(string key)
            {
                if (!mapping.TryGetValue(key, out var idx) || idx >= fields.Length) return null;
                var v = fields[idx];
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }

            var naamDelen = new[] { GetVeld("Roepnaam"), GetVeld("Tussenvoegsel"), GetVeld("Achternaam") }
                .Where(p => p != null).ToArray();
            var naam = naamDelen.Length > 0 ? string.Join(" ", naamDelen) : null;

            rows.Add(new TeambegeleidingCsvRij(
                GetVeld("Team"),
                GetVeld("LeeftijdscategorieTeam"),
                GetVeld("Teamrol"),
                GetVeld("Functie"),
                naam,
                GetVeld("Emailadres"),
                GetVeld("MobielNummer") ?? GetVeld("TelefoonnummerKolom")));
        }
        return rows;
    }

    // Functie zit in de sleutel: twee overigens identieke rijen met een andere functie zijn twee
    // verschillende rollen, geen duplicaat (#1360).
    private static (List<TeambegeleidingCsvRij> Rows, int Duplicaten) DedupliceerRijen(List<TeambegeleidingCsvRij> rows)
    {
        List<TeambegeleidingCsvRij> gededupliceerd = [.. rows
            .GroupBy(r => (
                Team: r.Team?.Trim().ToUpperInvariant(),
                Teamrol: r.Teamrol?.Trim().ToUpperInvariant(),
                Functie: r.Functie?.Trim().ToUpperInvariant(),
                Naam: r.Naam?.Trim().ToUpperInvariant(),
                Email: r.Emailadres?.Trim().ToUpperInvariant(),
                Telefoon: r.Telefoonnummer?.Trim().ToUpperInvariant()))
            .Select(g => g.First())];
        return (gededupliceerd, rows.Count - gededupliceerd.Count);
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuote && i + 1 < line.Length && line[i + 1] == '"')
                { current.Append('"'); i++; }
                else
                { inQuote = !inQuote; }
            }
            else if (c == ';' && !inQuote)
            { fields.Add(current.ToString().Trim()); current.Clear(); }
            else
            { current.Append(c); }
        }
        fields.Add(current.ToString().Trim());
        return [.. fields];
    }
}
