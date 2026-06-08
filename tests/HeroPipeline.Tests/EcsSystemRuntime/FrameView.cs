using System;
using MiniArch.Core;
using MiniArchQuery = MiniArch.Query;
using MiniArchQueryDescription = MiniArch.QueryDescription;
using CoreWorld = MiniArch.World;

namespace Hero.Ecs;

public sealed class FrameView
{
    private readonly CoreWorld _world;

    internal FrameView(CoreWorld world)
    {
        _world = world;
    }

    public bool Exists(MiniArch.Entity entity) => _world.TryGetLocation(entity, out _);

    public bool TryGetParent(MiniArch.Entity entity, out MiniArch.Entity parent) =>
        _world.TryGetParent(entity, out parent);

    public MiniArchQuery Each(MiniArchQueryDescription description) =>
        _world.Query(in description);

    public MiniArchQuery ChunkQuery(MiniArchQueryDescription description) =>
        _world.Query(in description);

    public EntityAccessor Access(MiniArch.Entity entity) =>
        _world.Access(entity);

    public T Get<T>(MiniArch.Entity entity)
    {
        if (!TryGet(entity, out T component))
        {
            throw new InvalidOperationException(
                $"Entity '{entity}' is missing component '{typeof(T).Name}'.");
        }

        return component;
    }

    public bool TryGet<T>(MiniArch.Entity entity, out T component) =>
        _world.TryGet(entity, out component);

    public void Set<T>(MiniArch.Entity entity, T value) =>
        _world.Set(entity, value);

    /// <summary>
    /// Gets a snapshot of world-level statistics.
    /// </summary>
    public MiniArch.WorldStats GetStats() => _world.GetStats();

    /// <summary>
    /// Gets archetype-level statistics for all archetypes.
    /// </summary>
    public MiniArch.ArchetypeStats[] GetArchetypeStats() => _world.GetArchetypeStats();
}


