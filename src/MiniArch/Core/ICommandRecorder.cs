namespace MiniArch.Core;

/// <summary>
/// Minimal recorder interface shared by <see cref="CommandBuffer"/>
/// and <see cref="CommandStream"/> for the test-layer abstraction.
/// Both classes also expose <c>Snapshot()</c> / <c>Clone()</c>
/// specific to each type; those are not part of this interface.
/// </summary>
public interface ICommandRecorder
{
    /// <summary>Reserves a new entity.</summary>
    Entity Create();

    /// <summary>Adds a component to a created entity.</summary>
    void Add<T>(Entity entity, T component);

    /// <summary>Sets a component value on an existing or created entity.</summary>
    void Set<T>(Entity entity, T component);

    /// <summary>Removes a component from an entity.</summary>
    void Remove<T>(Entity entity);

    /// <summary>Destroys an entity.</summary>
    void Destroy(Entity entity);

    /// <summary>Links a child entity to a parent.</summary>
    void Link(Entity parent, Entity child);

    /// <summary>Submits all recorded commands to the World.</summary>
    bool Submit();
}
