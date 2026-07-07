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
}
