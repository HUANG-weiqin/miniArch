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
    public void Add<T>(Entity entity, T component)
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Sets a component on an entity.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    public void Remove<T>(Entity entity)
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
    private void ApplyTypedAddOrSet<T>(Entity entity, ComponentType componentType, in T component)
    {
        var info = GetRequiredLocation(entity);
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
        fixed (byte* ptr = data)
        {
            ApplyRawAddOrSet(entity, componentType, ptr + offset);
        }
    }

    private unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, byte* source)
    {
        var info = GetRequiredLocation(entity);
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