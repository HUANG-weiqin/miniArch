namespace MiniArch.Core;

/// <summary>
/// Per-frame recording interface implemented by <see cref="CommandStream"/>
/// for the test-layer abstraction. Covers all user-facing input commands.
/// <see cref="CommandStream"/> also exposes <c>Snapshot</c> and
/// <c>SubmitAndSnapshotAsync</c> for output, but those are not recording
/// commands and stay on the concrete type.
/// </summary>
public interface ICommandRecorder
{
    /// <summary>
    /// Records a create. When <see cref="CommandStream.DeferredEntities"/> is
    /// <c>true</c>, returns a placeholder entity (Id &lt; 0) that is resolved
    /// to a real id at <see cref="Submit"/> time, deterministic across hosts
    /// that share the same record sequence. When <c>false</c> (default),
    /// reserves a real entity id immediately.
    /// </summary>
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

    /// <summary>
    /// Records a deep clone of an entity and its child subtree.
    /// When <see cref="CommandStream.DeferredEntities"/> is <c>true</c>,
    /// returns a placeholder; otherwise reserves a real id immediately.
    /// </summary>
    Entity Clone(Entity source);

    /// <summary>
    /// Applies all recorded commands to the World, then clears the recorder.
    /// Clearing runs in a finally block, so the recorder is reset to its initial
    /// state even if applying throws. Reserved entity ids from deferred Create
    /// are released back to the World's free list during clearing for any entity
    /// that did not get materialized (e.g. on the exception path).
    /// </summary>
    /// <returns>True if any commands were applied; false if the recorder was empty.</returns>
    bool Submit();

    /// <summary>
    /// Resets the recorder to its initial state, discarding all recorded commands
    /// without applying them. Reserved entity ids are released back to the World.
    /// Use for relay-only flow: <see cref="CommandStream.Snapshot"/> then Clear.
    /// </summary>
    void Clear();
}
