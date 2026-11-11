namespace BlazorAdmin.Services;

/// <summary>Pure state for an inline two-step destructive action; no action runs until confirmed.</summary>
public sealed class InlineBevestiging<T> where T : notnull
{
    private T? _doel;
    public bool IsOpen { get; private set; }

    public bool IsOpenFor(T doel) => IsOpen && EqualityComparer<T>.Default.Equals(_doel!, doel);

    public void Vraag(T doel)
    {
        _doel = doel;
        IsOpen = true;
    }

    public bool Bevestig(T doel)
    {
        if (!IsOpenFor(doel)) return false;
        Annuleer();
        return true;
    }

    public void Annuleer()
    {
        _doel = default;
        IsOpen = false;
    }
}
