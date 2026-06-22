using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Adds a component to an entity, or overwrites it if already present.
    /// </summary>
    /// <remarks>
    /// This is an <b>upsert</b>: if the entity already has a component of type
    /// <typeparamref name="T"/>, its value is overwritten in place without an
    /// archetype migration. If you require strict add semantics (throw on
    /// duplicate), check <see cref="Has{T}"/> first.
    /// <para>
    /// <b>Alias of <see cref="Set{T}"/>.</b> Both route to the same implementation.
    /// The two names exist for readability at call sites ("Add a new buff" vs
    /// "Set health to 50"), not for behavioural difference.
    /// </para>
    /// </remarks>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Sets a component on an entity, adding it if absent or overwriting if present.
    /// </summary>
    /// <remarks>
    /// This is an <b>upsert</b> — see <see cref="Add{T}"/> for the full contract.
    /// </remarks>
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
    private int MoveEntityCore(
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