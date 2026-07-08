namespace MiniArch.Core;

/// <summary>
/// Non-generic dispatch interface for change queries. The World holds a list of
/// weak references to registered queries and calls OnTransition on each
/// structural op (Create/Destroy/Add/Remove).
/// </summary>
internal interface IChangeQuery
{
    /// <summary>
    /// Called by the World on each structural op (Create/Destroy/Add/Remove).
    /// The query filters old/new archetype against its configured filter and
    /// appends a Transition if membership changed.
    /// </summary>
    void OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype);

    /// <summary>
    /// Called when the world rolls back to a captured state. Implementations
    /// must drop prediction-era observer state while preserving registration so
    /// mutations immediately after restore are still observed.
    /// </summary>
    void OnWorldRestored(int trackingGeneration);
}
