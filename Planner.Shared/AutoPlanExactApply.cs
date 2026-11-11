using System.Globalization;
using System.Threading.Tasks;

namespace Planner.Shared;

/// <summary>Client choice and source snapshot for applying a visible ALLSTARS plan.</summary>
public sealed record AutoPlanApplyChoice(
    long? WedstrijdCode, string? HuidigeVeld, string? HuidigeTijd,
    int? VeldNummer, string? VeldSubpositie, string? Tijd);

public sealed record AutoPlanApplySource(
    long? WedstrijdCode, string? Veld, string? AanvangsTijd, string TeamNaam,
    string? LeeftijdsCategorie, int? SportlinkSpeelduur);

public sealed record AutoPlanApplyWrite(long WedstrijdCode, string? VerwachtVeld,
    string? VerwachteTijd, string Veld, string Tijd);

/// <summary>Safe conflict reported to the caller; a transaction must roll back on this exception.</summary>
public sealed class AutoPlanApplyConflictException : Exception
{
    public const string SafeMessage =
        "De getoonde planning is gewijzigd of bevat een conflict. Controleer de planning en probeer opnieuw.";

    public AutoPlanApplyConflictException() : base(SafeMessage) { }

    public static bool IsTransactionConflict(string? postgresSqlState, int? sqlServerNumber) =>
        string.Equals(postgresSqlState, "40001", StringComparison.Ordinal) ||
        sqlServerNumber is 1205 or 3960;
}

public sealed class AutoPlanApplyForbiddenException : Exception
{
    public AutoPlanApplyForbiddenException()
        : base("Toepassen is alleen beschikbaar in testmodus (ALLSTARS).") { }
}

/// <summary>
/// Pure exact-slot validation shared by SQL Server and Postgres. Source and metadata must be read
/// inside the same serializable transaction that persists the returned writes.
/// </summary>
public static class AutoPlanExactApply
{
    private sealed record ApplyRow(AutoPlanApplySource Source, int Field, TimeOnly Start,
        string? Lane, decimal Fraction, int Duration, bool SportlinkLeading, bool Write);

    public static List<AutoPlanApplyWrite> Validate(
        IReadOnlyList<AutoPlanApplyChoice> choices,
        IReadOnlyList<AutoPlanApplySource> sources,
        List<VeldInfo> velden,
        IReadOnlyDictionary<string, Speeltijd> speeltijden,
        Dictionary<string, (int bufferVoor, int bufferNa)> teamBuffers,
        int bufferMinuten)
    {
        ValidateSourceSet(choices, sources);
        var choicesById = choices.ToDictionary(c => c.WedstrijdCode!.Value);
        var sourceFields = velden.Select(v => ((string?)v.VeldNaam, v.VeldNummer)).ToList();
        var rows = new List<ApplyRow>();

        foreach (var source in sources)
        {
            if (!choicesById.TryGetValue(source.WedstrijdCode!.Value, out var choice))
                throw new AutoPlanApplyConflictException();
            var row = BuildRow(source, choice, sourceFields, speeltijden);
            if (row != null) rows.Add(row);
        }

        var availability = velden.Select(v => new VeldBeschikbaarheidInfo
        {
            VeldNummer = v.VeldNummer, BeschikbaarVanaf = new TimeOnly(8, 0),
            BeschikbaarTot = new TimeOnly(22, 0), GebruikZonsondergang = false
        }).ToList();
        var scheduler = new FieldScheduler(availability, velden, bufferMinuten, teamBuffers);
        var writes = new List<AutoPlanApplyWrite>();
        foreach (var row in rows.OrderBy(r => r.Start).ThenBy(r => r.Field))
        {
            if (!scheduler.TryOccupyExactSlot(row.Field, row.Start, row.Duration, row.Fraction,
                    row.Lane, row.Source.TeamNaam, row.SportlinkLeading, out _))
                throw new AutoPlanApplyConflictException();
            if (!row.Write) continue;
            var veld = velden.Single(v => v.VeldNummer == row.Field);
            writes.Add(new AutoPlanApplyWrite(row.Source.WedstrijdCode!.Value,
                row.Source.Veld, row.Source.AanvangsTijd,
                AutoPlanRegels.BuildSportlinkVeldString(veld.VeldNaam, row.Lane ?? ""),
                FormatTime(row.Start)));
        }
        return writes;
    }

    public static async Task<int> PersistAsync(IReadOnlyCollection<AutoPlanApplyWrite> writes,
        Func<AutoPlanApplyWrite, Task<int>> persist,
        Func<Task> commit)
    {
        foreach (var write in writes)
            if (await persist(write) != 1) throw new AutoPlanApplyConflictException();

        await commit();
        return writes.Count;
    }

    private static void ValidateSourceSet(IReadOnlyList<AutoPlanApplyChoice> choices,
        IReadOnlyList<AutoPlanApplySource> sources)
    {
        if (choices.Count != sources.Count || choices.Any(c => c.WedstrijdCode is null) ||
            sources.Any(s => s.WedstrijdCode is null) ||
            choices.Select(c => c.WedstrijdCode).Distinct().Count() != choices.Count ||
            sources.Select(s => s.WedstrijdCode).Distinct().Count() != sources.Count)
            throw new AutoPlanApplyConflictException();
    }

    private static ApplyRow? BuildRow(AutoPlanApplySource source, AutoPlanApplyChoice choice,
        List<(string? VeldNaam, int VeldNummer)> sourceFields,
        IReadOnlyDictionary<string, Speeltijd> speeltijden)
    {
        if (!SameField(source.Veld, choice.HuidigeVeld) ||
            !SameTime(source.AanvangsTijd, choice.HuidigeTijd) ||
            choice.VeldNummer.HasValue != (choice.Tijd != null) ||
            (!choice.VeldNummer.HasValue && choice.VeldSubpositie != null))
            throw new AutoPlanApplyConflictException();

        var (sourceField, sourceLane) = VeldResolver.Resolve(source.Veld, sourceFields);
        var targetField = choice.VeldNummer ?? sourceField;
        var targetTime = choice.Tijd ?? source.AanvangsTijd;
        var targetLane = choice.VeldNummer.HasValue ? choice.VeldSubpositie : sourceLane;
        var age = !string.IsNullOrWhiteSpace(source.LeeftijdsCategorie)
            ? source.LeeftijdsCategorie : AutoPlanRegels.ExtractLeeftijdFromTeamNaam(source.TeamNaam);
        speeltijden.TryGetValue(age ?? "", out var speeltijd);
        bool sportlinkLeading = VeldbezettingDuur.IsSportlinkLeidend(source.SportlinkSpeelduur);

        // Een onbekend team zonder Sportlink-speelduur heeft geen betrouwbare duur of veldfractie.
        // De preview laat deze rij ongewijzigd; hij telt daarom niet mee als bezetting en kan andere
        // geldige toewijzingen niet blokkeren.
        if (IsUnknownUnassigned(choice, speeltijd, sportlinkLeading)) return null;

        if (MissingSlot(targetField, targetTime, choice.VeldNummer.HasValue)) return null;
        var start = ParseTargetStart(targetTime, choice.VeldNummer.HasValue);

        // The UI sends minute precision, while the source can contain seconds. When the chosen
        // displayed minute is unchanged, retain the source time even if field/lane changed.
        start = RetainSourceSeconds(source.AanvangsTijd, choice, start);

        var fraction = DetermineFraction(targetLane, speeltijd);
        var duration = VeldbezettingDuur.Bepaal(source.SportlinkSpeelduur, speeltijd?.WedstrijdTotaal);
        if (duration <= 0) throw new AutoPlanApplyConflictException();

        var write = choice.VeldNummer.HasValue &&
            (targetField != sourceField || !SameMinute(source.AanvangsTijd, choice.Tijd) ||
             !string.Equals(sourceLane, targetLane, StringComparison.OrdinalIgnoreCase));
        return new ApplyRow(source, targetField, start, targetLane, fraction, duration,
            sportlinkLeading, write);
    }

    private static bool IsUnknownUnassigned(AutoPlanApplyChoice choice, Speeltijd? speeltijd,
        bool sportlinkLeading) =>
        !choice.VeldNummer.HasValue && choice.Tijd == null && speeltijd == null && !sportlinkLeading;

    private static TimeOnly ParseTargetStart(string? targetTime, bool assigned)
    {
        var parsed = assigned
            ? TimeOnly.TryParseExact(targetTime, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            : TimeOnly.TryParse(targetTime, CultureInfo.InvariantCulture, out start);
        return parsed ? start : throw new AutoPlanApplyConflictException();
    }

    private static decimal DetermineFraction(string? targetLane, Speeltijd? speeltijd)
    {
        var fraction = VeldResolver.SubpositieFractie(targetLane) ?? speeltijd?.Veldafmeting ?? 1.00m;
        return fraction is > 0 and <= 1 ? fraction : throw new AutoPlanApplyConflictException();
    }

    private static bool SameField(string? a, string? b) =>
        VeldNormalisatie.Normaliseer(a) == VeldNormalisatie.Normaliseer(b);

    private static bool MissingSlot(int field, string? time, bool assigned)
    {
        if (field != 0 && !string.IsNullOrWhiteSpace(time)) return false;
        if (assigned) throw new AutoPlanApplyConflictException();
        return true;
    }

    private static bool SameTime(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        return TimeOnly.TryParse(a, CultureInfo.InvariantCulture, out var first) &&
               TimeOnly.TryParse(b, CultureInfo.InvariantCulture, out var second) && first == second;
    }

    private static bool SameMinute(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return TimeOnly.TryParse(a, CultureInfo.InvariantCulture, out var first) &&
               TimeOnly.TryParse(b, CultureInfo.InvariantCulture, out var second) &&
               first.Hour == second.Hour && first.Minute == second.Minute;
    }

    private static TimeOnly RetainSourceSeconds(string? sourceTime, AutoPlanApplyChoice choice, TimeOnly target)
    {
        if (choice.VeldNummer.HasValue && SameMinute(sourceTime, choice.Tijd) &&
            TimeOnly.TryParse(sourceTime, CultureInfo.InvariantCulture, out var source))
            return source;
        return target;
    }

    private static string FormatTime(TimeOnly time)
    {
        if (time.Ticks % TimeSpan.TicksPerMinute == 0)
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }
}
