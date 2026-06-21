using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Adds a component to an entity.
    /// </summary>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Sets a component on an entity.
    /// </summary>
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        ThrowIfDisposed();
        var componentType = GetComponentType<T>();
        RemoveBoxed(entity, componentType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveEntityCore(
        Entity entity,
        EntityRecord sourceInfo,
        Archetype destination,
        out int destinationRowIndex)
    {
        destinationRowIndex = destination.AddEntity(entity);
        destination.CopySharedComponentsFrom(sourceInfo.Archetype!, sourceInfo.RowIndex, destinationRowIndex);
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
        MoveEntityCore(entity, sourceInfo, destination, out var rowIdx);
        FinishMoveEntity(entity, sourceInfo, destination, rowIdx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyTypedAddOrSet<T>(Entity entity, ComponentType componentType, in T component) where T : unmanaged
    {
        var info = GetRequiredLocation(entity);
        ApplyTypedAddOrSet(entity, info, componentType, in component);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ApplyTypedAddOrSet<T>(Entity entity, EntityRecord info, ComponentType componentType, in T component) where T : unmanaged
    {
        var archetype = info.Archetype!;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        if (!archetype.TryGetAddDestination(componentType, out var destination))
        {
            var destinationSignature = archetype.Signature.Add(componentType);
            destination = GetOrCreateArchetype(destinationSignature);
            archetype.CacheAddDestination(componentType, destination);
            destination!.CacheRemoveDestination(componentType, archetype);
        }

        var rowIdx = destination!.AddEntity(entity);
        destination.CopySharedComponentsFrom(archetype, info.RowIndex, rowIdx);
        destination.SetComponentAtTyped(destination.GetComponentIndex(componentType), rowIdx, in component);
        FinishMoveEntity(entity, info, destination, rowIdx);
    }

    internal unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, byte[] data, int offset)
    {
        if (!TryGetLocation(entity, out var loc))
            return;
        var record = new EntityRecord { Archetype = loc.Archetype, RowIndex = loc.RowIndex, Version = loc.Version };
        fixed (byte* ptr = data)
        {
            ApplyRawAddOrSet(entity, record, componentType, ptr + offset);
        }
    }

    private unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, byte* source)
    {
        var info = GetRequiredLocation(entity);
        ApplyRawAddOrSet(entity, info, componentType, source);
    }

    internal unsafe void ApplyRawAddOrSet(Entity entity, EntityRecord info, ComponentType componentType, byte* source)
    {
        var archetype = info.Archetype!;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.WriteComponentRaw(componentIndex, info.RowIndex, source);
            return;
        }

        var destination = GetOrCreateAddDestinationArchetype(archetype, componentType);
        MoveEntityFromBytes(entity, info, destination, componentType, source);
    }

    private unsafe void MoveEntityFromBytes(
        Entity entity,
        EntityRecord sourceInfo,
        Archetype destination,
        ComponentType componentType,
        byte* source)
    {
        MoveEntityCore(entity, sourceInfo, destination, out var rowIdx);
        var columnIndex = destination.GetComponentIndex(componentType);
        destination.WriteComponentRaw(columnIndex, rowIdx, source);
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
        var info = GetRequiredLocation(entity);
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