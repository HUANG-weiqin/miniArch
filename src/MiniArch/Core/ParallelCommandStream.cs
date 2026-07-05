using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Multi-threaded <see cref="CommandStreamCore"/>. All record methods
/// (<see cref="Create"/>, <see cref="Track"/>, <see cref="Add{T}"/>,
/// <see cref="Set{T}"/>, <see cref="Remove{T}"/>, <see cref="Destroy"/>,
/// <see cref="AddChild"/>, <see cref="RemoveChild"/>, <see cref="Clone"/>)
/// can be invoked concurrently from multiple threads.
/// <see cref="CommandStreamCore.Submit"/>, <see cref="CommandStreamCore.Snapshot"/>,
/// <see cref="CommandStreamCore.SubmitAndSnapshotAsync"/> and
/// <see cref="CommandStreamCore.Replay"/> must be called from a single thread
/// after all parallel recording work has completed.
/// </summary>
/// <remarks>
/// <para>
/// For single-threaded use, prefer <see cref="CommandStream"/> —it avoids the
/// per-mutator lock acquire.
/// </para>
/// <para>
/// <b>Concurrency model:</b> all mutators serialize on
/// <c>_storeCreateLock</c>. Per-entity record order across threads is
/// non-deterministic, but Submit/Snapshot deterministically sorts and dedupes
/// before emission, so cross-thread reordering does not affect the resulting
/// <see cref="FrameDelta"/> or world state.
/// </para>
/// <para>
/// Do <b>not</b> record concurrently into multiple <see cref="ParallelCommandStream"/>
/// instances that target the same <see cref="World"/>; concurrent recording is
/// only supported within one stream.
/// </para>
/// </remarks>
public sealed class ParallelCommandStream : CommandStreamCore
{
    /// <summary>
    /// Creates a new parallel command stream bound to <paramref name="world"/>.
    /// </summary>
    public ParallelCommandStream(World world) : base(world) { }

    /// <inheritdoc/>
    public override Entity Create()
    {
        lock (_storeCreateLock)
            return CreateCore();
    }

    /// <inheritdoc/>
    public override EntitySlot Track(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        // Placeholder slots register into _trackedBySeq — serialize with other mutators.
        lock (_storeCreateLock)
            return TrackCore(entity);
    }

    /// <inheritdoc/>
    public override void Add<T>(Entity entity, T component)
    {
        if (CanRecordParallelComponentCommand(entity))
            GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindAdd);
    }

    /// <inheritdoc/>
    public override void Set<T>(Entity entity, T component)
    {
        if (CanRecordParallelComponentCommand(entity))
            GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindSet);
    }

    /// <inheritdoc/>
    public override void Remove<T>(Entity entity)
    {
        if (CanRecordParallelComponentCommand(entity))
            GetOrCreateStoreParallel<T>().AppendConcurrent(entity, default!, KindRemove);
    }

    /// <inheritdoc/>
    public override void Destroy(Entity entity)
    {
        // Same pending-check + cancel logic as single-threaded, under the lock.
        // Without this, parallel Destroy on a pending entity would append to
        // DestroyEntities without cancelling the batch, causing the entity (and
        // its pending descendants) to be materialized then destroyed —diverging
        // from single-threaded semantics where they are never materialized.
        lock (_storeCreateLock)
            DestroyCore(entity);
    }

    /// <inheritdoc/>
    public override void AddChild(Entity parent, Entity child)
    {
        lock (_storeCreateLock)
            AddChildCore(parent, child);
    }

    /// <inheritdoc/>
    public override void RemoveChild(Entity child)
    {
        lock (_storeCreateLock)
            RemoveChildCore(child);
    }

    /// <inheritdoc/>
    public override Entity Clone(Entity source)
    {
        // Validate outside the lock (read-only world access); materialize under it.
        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        lock (_storeCreateLock)
            return CloneImpl(source, location);
    }
}
