namespace MiniArch.Core;

/// <summary>
/// Non-generic dispatch interface for change queries. The World holds a list of
/// weak references to registered queries and calls lifecycle methods on each
/// structural or write op when tracking is active.
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
    /// Called before a component value is written (Set). Default no-op for
    /// backward compat. Entity is in <paramref name="archetype"/> at <paramref name="row"/>.
    /// </summary>
    void OnBeforeWrite(Entity entity, Archetype archetype, int row) { }

    /// <summary>
    /// Called after a component value has been written (Set). Default no-op.
    /// Entity is in <paramref name="archetype"/> at <paramref name="row"/>.
    /// </summary>
    void OnAfterWrite(Entity entity, Archetype archetype, int row) { }

    /// <summary>
    /// Called before a structural change moves an entity out of
    /// <paramref name="archetype"/> at <paramref name="row"/>.
    /// Entity has NOT been moved yet — safe to read component values from
    /// (archetype, row).
    /// </summary>
    void OnBeforeTransition(Entity entity, Archetype archetype, int row) { }
}
