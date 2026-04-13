namespace MiniArch.Core;

/// <summary>
/// Captures bidirectional public-state differences for a frame.
/// </summary>
public readonly struct WorldDelta
{
    private static readonly IReadOnlyList<WorldDeltaEntry> EmptyEntries = Array.Empty<WorldDeltaEntry>();
    private static readonly IReadOnlyList<Entity> EmptyEntities = Array.Empty<Entity>();
    private readonly WorldDeltaState? _state;

    internal WorldDelta(WorldDeltaState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets the per-entity delta entries.
    /// </summary>
    public IReadOnlyList<WorldDeltaEntry> Entries => _state?.Entries ?? EmptyEntries;

    internal IReadOnlyList<Entity> ReservedEntities => _state?.ReservedEntities ?? EmptyEntities;

    internal IReadOnlyList<Entity> ReleasedEntities => _state?.ReleasedEntities ?? EmptyEntities;

    internal WorldDeltaState State => _state ?? throw new InvalidOperationException("World delta is not initialized.");
}

/// <summary>
/// Captures the public state transition of an entity.
/// </summary>
public readonly record struct WorldDeltaEntry(Entity Entity, WorldEntityPublicState? Before, WorldEntityPublicState? After);

/// <summary>
/// Captures an entity's public state.
/// </summary>
public readonly record struct WorldEntityPublicState(Entity Parent, IReadOnlyList<FrameComponentValue> Components);

internal sealed class WorldDeltaState
{
    public WorldDeltaState(WorldDeltaEntry[] entries, Entity[] reservedEntities, Entity[] releasedEntities)
    {
        Entries = entries;
        ReservedEntities = reservedEntities;
        ReleasedEntities = releasedEntities;
    }

    public WorldDeltaEntry[] Entries { get; }

    public Entity[] ReservedEntities { get; }

    public Entity[] ReleasedEntities { get; }
}
