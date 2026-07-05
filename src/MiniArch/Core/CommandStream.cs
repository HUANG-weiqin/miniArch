using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Single-threaded <see cref="CommandStreamCore"/>. All mutators run without
/// synchronization; callers must ensure no concurrent access from multiple threads.
/// <para/>
/// For multi-threaded recording, use <see cref="ParallelCommandStream"/>.
/// </summary>
/// <remarks>
/// <b>Why a dedicated single-threaded type:</b> removing the per-call mode check
/// that a combined type would need (<c>if (parallel) lock(...); ...</c>) eliminates
/// a branch the CPU predictor must track on every recording API. On the
/// single-threaded hot path (record+submit per frame) this is the difference
/// between a direct call and a guarded dispatch.
/// </remarks>
public sealed class CommandStream : CommandStreamCore
{
    /// <summary>
    /// Creates a new single-threaded command stream bound to <paramref name="world"/>.
    /// </summary>
    public CommandStream(World world) : base(world) { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Entity Create() => CreateCore();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override EntitySlot Track(Entity entity) => TrackCore(entity);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Add<T>(Entity entity, T component)
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            WritePendingComponent(batchIdx, component);
        else if (_world.IsAlive(entity))
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Set<T>(Entity entity, T component)
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            WritePendingComponent(batchIdx, component);
        else if (_world.IsAlive(entity))
            GetOrCreateStore<T>().Append(entity, component, KindSet);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Remove<T>(Entity entity)
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        else if (_world.IsAlive(entity))
            GetOrCreateStore<T>().AppendRemove(entity);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Destroy(Entity entity) => DestroyCore(entity);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void AddChild(Entity parent, Entity child) => AddChildCore(parent, child);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void RemoveChild(Entity child) => RemoveChildCore(child);

    /// <inheritdoc/>
    public override Entity Clone(Entity source) => CloneCore(source);
}
