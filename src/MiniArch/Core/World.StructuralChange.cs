using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Adds component <typeparamref name="T"/> to the entity.
    /// Throws if the entity already has a component of this type.
    /// </summary>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        AssertNotDisposed();
        BeginStructChange();
#if DEBUG
        try
        {
#endif
        ApplyTypedAdd(entity, GetComponentType<T>(), in component);
#if DEBUG
        }
        finally
        {
            EndStructChange();
        }
#else
        EndStructChange();
#endif
    }

    /// <summary>
    /// Sets the value of an existing component. Throws if the entity does not have it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity does not have a component of type <typeparamref name="T"/>.</exception>
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        AssertNotDisposed();
        ApplyTypedSetFast(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Direct hot path for World.Set&lt;T&gt;. Value tracking is boundary-diff based,
    /// so this path only validates and writes the component cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyTypedSetFast<T>(Entity entity, ComponentType componentType, in T component) where T : unmanaged
    {
        var id = entity.Id;
        AssertValidEntityId(id, entity);
        ref var record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_records), id);
        if (!record.IsOccupied || record.Version != entity.Version)
            ThrowStaleEntity(entity);

        var archetype = record.Archetype!;
        if (!archetype.TryGetComponentIndex(componentType, out var componentIndex))
            ThrowComponentNotFound<T>(entity);

        ref var cell = ref archetype.GetComponentRefAt<T>(componentIndex, record.RowIndex);
        cell = component;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowComponentNotFound<T>(Entity entity)
    {
        throw new InvalidOperationException(
            $"Entity {entity} does not have component {typeof(T).Name}.");
    }

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    /// <remarks>
    /// If the entity does not have the component, this method silently returns
    /// without throwing. Use <see cref="World.Has{T}"/> to check existence first.
    /// </remarks>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        AssertNotDisposed();
        BeginStructChange();
#if DEBUG
        try
        {
#endif
        var componentType = GetComponentType<T>();
        RemoveBoxed(entity, componentType);
#if DEBUG
        }
        finally
        {
            EndStructChange();
        }
#else
        EndStructChange();
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MoveEntityCore(
        Entity entity,
        EntityRecord sourceInfo,
        Archetype destination)
    {
        var destinationRowIndex = destination.AddEntity(entity);
        try
        {
            destination.CopySharedComponentsFrom(sourceInfo.Archetype!, sourceInfo.RowIndex, destinationRowIndex);
        }
        catch
        {
            // Roll back the AddEntity so the destination archetype does not
            // retain a phantom row with uninitialized component data. Without
            // this rollback, an exception mid-migration would leave the entity
            // present in both source and destination archetypes while the
            // record still points at the source — an unrecoverable corruption.
            destination.RemoveAt(destinationRowIndex, out _);
            throw;
        }
        return destinationRowIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinishMoveEntity(
        Entity entity,
        EntityRecord sourceInfo,
        Archetype destination,
        int destinationRowIndex)
    {
        var sourceArchetype = sourceInfo.Archetype!;
        sourceArchetype.RemoveAt(sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            ref var movedRecord = ref _records[movedEntity.Id];
            movedRecord.Archetype = sourceArchetype;
            movedRecord.RowIndex = sourceInfo.RowIndex;
        }

        ref var record = ref _records[entity.Id];
        record.Archetype = destination;
        record.RowIndex = destinationRowIndex;
    }

    private void MoveEntity(Entity entity, EntityRecord sourceInfo, Archetype destination)
    {
        var rowIdx = MoveEntityCore(entity, sourceInfo, destination);
        FinishMoveEntity(entity, sourceInfo, destination, rowIdx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyTypedAdd<T>(Entity entity, ComponentType componentType, in T component) where T : unmanaged
    {
        var info = RequireLocation(entity);
        ApplyTypedAdd(entity, info, componentType, in component);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ApplyTypedAdd<T>(Entity entity, EntityRecord info, ComponentType componentType, in T component) where T : unmanaged
    {
        var archetype = info.Archetype!;

        if (archetype.TryGetComponentIndex(componentType, out var existingIdx))
        {
            throw new InvalidOperationException(
                $"Entity {entity} already has component type {componentType.Value}. " +
                "Use Set<T> to overwrite, or remove the component first.");
        }

        if (!archetype.TryGetAddDestination(componentType, out var destination))
        {
            var destinationSignature = archetype.Signature.Add(componentType);
            destination = GetOrCreateArchetype(destinationSignature);
            archetype.CacheAddDestination(componentType, destination);
            destination!.CacheRemoveDestination(componentType, archetype);
        }

        var sourceArchetype = info.Archetype!;

        var rowIdx = MoveEntityCore(entity, info, destination!);
        try
        {
            destination!.SetComponentAtTyped(destination.GetComponentIndex(componentType), rowIdx, in component);
        }
        catch
        {
            destination!.RemoveAt(rowIdx, out _);
            throw;
        }
        FinishMoveEntity(entity, info, destination!, rowIdx);
    }

    // Raw-byte paths: ReplayCore dispatches Add/Set ops here.
    internal unsafe void ApplyRawAdd(Entity entity, EntityRecord info, ComponentType componentType, byte* source)
    {
        var archetype = info.Archetype!;
        if (archetype.TryGetComponentIndex(componentType, out var existingColIdx))
        {
            throw new InvalidOperationException(
                $"Replay Add: entity {entity} already has component type {componentType.Value}. " +
                "The delta is invalid — use Set for overwrites, not Add.");
        }

        var destination = GetOrCreateAddDestinationArchetype(archetype, componentType);
        MoveEntityFromBytes(entity, info, destination, componentType, source);
    }

    internal static unsafe void ApplyRawSet(Entity entity, EntityRecord info, ComponentType componentType, byte* source)
    {
        var archetype = info.Archetype!;

        if (!archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have component type {componentType.Value}.");
        }

        archetype.WriteComponentRaw(componentIndex, info.RowIndex, source);
    }

    private unsafe void MoveEntityFromBytes(
        Entity entity,
        EntityRecord sourceInfo,
        Archetype destination,
        ComponentType componentType,
        byte* source)
    {
        var sourceArchetype = sourceInfo.Archetype!;
        var rowIdx = MoveEntityCore(entity, sourceInfo, destination);
        try
        {
            var columnIndex = destination.GetComponentIndex(componentType);
            destination.WriteComponentRaw(columnIndex, rowIdx, source);
        }
        catch
        {
            destination.RemoveAt(rowIdx, out _);
            throw;
        }
        FinishMoveEntity(entity, sourceInfo, destination, rowIdx);
    }

    private Archetype GetOrCreateAddDestinationArchetype(Archetype source, ComponentType componentType)
    {
        if (source.TryGetAddDestination(componentType, out var destination))
            return destination!;

        var destinationSignature = source.Signature.Add(componentType);
        destination = GetOrCreateArchetype(destinationSignature);
        source.CacheAddDestination(componentType, destination);
        destination.CacheRemoveDestination(componentType, source);
        return destination;
    }

    internal void RemoveBoxed(Entity entity, ComponentType componentType)
    {
        var info = RequireLocation(entity);
        RemoveBoxed(entity, info, componentType);
    }

    internal void RemoveBoxed(Entity entity, EntityRecord info, ComponentType componentType)
    {
        var archetype = info.Archetype!;

        if (!archetype.TryGetComponentIndex(componentType, out _))
        {
            return;
        }

        if (!archetype.TryGetRemoveDestination(componentType, out var destination))
        {
            var destinationSignature = archetype.Signature.Remove(componentType);
            destination = GetOrCreateArchetype(destinationSignature);
            archetype.CacheRemoveDestination(componentType, destination);
            destination!.CacheAddDestination(componentType, archetype);
        }

        MoveEntity(entity, info, destination!);
    }

}
