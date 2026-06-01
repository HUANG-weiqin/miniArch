using System;
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

    public MiniArch.Core.Query ChunkQuery(MiniArchQueryDescription description) =>
        MiniArch.Core.Query.Create(_world, in description);

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
}


