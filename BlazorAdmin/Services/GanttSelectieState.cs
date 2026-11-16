namespace BlazorAdmin.Services;

/// <summary>Persistent selection shared by a Gantt block and its matching table row.</summary>
public sealed class GanttSelectieState
{
    public long? WedstrijdCode { get; private set; }

    public void Selecteer(long? wedstrijdCode) => WedstrijdCode = wedstrijdCode;

    public bool IsGeselecteerd(long? wedstrijdCode) =>
        wedstrijdCode.HasValue && wedstrijdCode == WedstrijdCode;

    /// <summary>
    /// Keeps the selected item visible when the active list filter excludes it, without changing
    /// the filter. The caller can mark the returned extra item as outside the filter.
    /// </summary>
    public IEnumerable<T> VoegGeselecteerdeToe<T>(
        IEnumerable<T> gefilterd,
        IEnumerable<T> alle,
        Func<T, long?> wedstrijdCode)
    {
        var zichtbaar = gefilterd.ToList();
        if (!WedstrijdCode.HasValue || zichtbaar.Any(item => wedstrijdCode(item) == WedstrijdCode))
            return zichtbaar;

        var geselecteerd = alle.FirstOrDefault(item => wedstrijdCode(item) == WedstrijdCode);
        return geselecteerd is null ? zichtbaar : zichtbaar.Append(geselecteerd);
    }
}
