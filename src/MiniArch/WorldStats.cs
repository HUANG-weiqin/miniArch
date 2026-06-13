namespace MiniArch;

/// <summary>
/// Snapshot of world-level statistics.
/// All data is computed on demand; no background state is maintained.
/// </summary>
public readonly struct WorldStats
{
    /// <summary>Number of alive entities.</summary>
    public int EntityCount { get; }

    /// <summary>Entity metadata slot capacity.</summary>
    public int EntityCapacity { get; }

    /// <summary>Number of recycled entity IDs in the free list.</summary>
    public int RecycledEntityCount { get; }

    /// <summary>Number of archetypes.</summary>
    public int ArchetypeCount { get; }

    internal WorldStats(int entityCount, int entityCapacity, int recycledCount, int archetypeCount)
    {
        EntityCount = entityCount;
        EntityCapacity = entityCapacity;
        RecycledEntityCount = recycledCount;
        ArchetypeCount = archetypeCount;
    }
}

/// <summary>
/// Snapshot of a single archetype's state.
/// </summary>
public readonly struct ArchetypeStats
{
    /// <summary>Number of entities stored in this archetype.</summary>
    public int EntityCount { get; }

    /// <summary>Current allocated capacity (entities).</summary>
    public int Capacity { get; }

    /// <summary>Component types that define this archetype's signature.</summary>
    public IReadOnlyList<Type> ComponentTypes { get; }

    internal ArchetypeStats(int entityCount, int capacity, IReadOnlyList<Type> componentTypes)
    {
        EntityCount = entityCount;
        Capacity = capacity;
        ComponentTypes = componentTypes;
    }
}
