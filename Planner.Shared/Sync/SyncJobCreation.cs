namespace Planner.Shared.Sync;

/// <summary>Atomaire jobkeuze: alleen de maker zet een bericht op de queue.</summary>
public sealed record SyncJobCreation(SyncJobStatus Job, bool Created);
