namespace MiniArch.Core;

/// <summary>
/// Per-frame recording interface shared by <see cref="CommandBuffer"/>
/// and <see cref="CommandStream"/> for the test-layer abstraction.
/// Both implementations also expose <c>Unlink</c>, <c>Snapshot</c>,
/// <c>SubmitAndSnapshotAsync</c>, and <c>Clone</c>, but no current
/// consumer needs polymorphic access to those — they remain on the
/// concrete types until a use case appears (YAGNI).
/// </summary>
public interface ICommandRecorder
{
    /// <summary>Reserves a new entity.</summary>
    Entity Create();

    /// <summary>Adds a component to a created entity.</summary>
    void Add<T>(Entity entity, T component) where T : unmanaged;

    /// <summary>Sets a component value on an existing or created entity.</summary>
    void Set<T>(Entity entity, T component) where T : unmanaged;

    /// <summary>Removes a component from an entity.</summary>
    void Remove<T>(Entity entity) where T : unmanaged;

    /// <summary>Destroys an entity.</summary>
    void Destroy(Entity entity);

    /// <summary>Links a child entity to a parent.</summary>
    void Link(Entity parent, Entity child);

    /// <summary>Submits all recorded commands to the World.</summary>
    bool Submit();
}
