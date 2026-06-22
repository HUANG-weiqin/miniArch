namespace MiniArch.Core;

/// <summary>
/// Per-frame recording interface shared by <see cref="CommandBuffer"/>
/// and <see cref="CommandStream"/> for the test-layer abstraction.
/// Covers all user-facing input commands. Both implementations also
/// expose <c>Snapshot</c> and <c>SubmitAndSnapshotAsync</c> for output,
/// but those are not recording commands and stay on the concrete types.
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

    /// <summary>Unlinks a child entity from its parent.</summary>
    void Unlink(Entity child);

    /// <summary>Records a deep clone of an entity and its child subtree.</summary>
    Entity Clone(Entity source);

    /// <summary>Submits all recorded commands to the World.</summary>
    bool Submit();
}
