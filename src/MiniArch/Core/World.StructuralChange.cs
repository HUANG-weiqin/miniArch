using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Ensures the entity has component <typeparamref name="T"/>. If the component
    /// already exists, its value is overwritten in place.
    /// </summary>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        AssertNotDisposed();
        ApplyTypedAdd(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Sets the value of an existing component. Throws if the entity does not have it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity does not have a component of type <typeparamref name="T"/>.</exception>
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        AssertNotDisposed();
        ApplyTypedSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        AssertNotDisposed();
        var componentType = GetComponentType<T>();
        RemoveBoxed(entity, componentType);
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

        // Pre-hook: capture Old values before entity moves out of source archetype
        if (_anyTrackingActive) DispatchBeforeTransition(entity, sourceArchetype, info.RowIndex);

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
        if (_anyTrackingActive) AppendTransition(entity, sourceArchetype, destination!);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyTypedSet<T>(Entity entity, ComponentType componentType, in T component) where T : unmanaged
    {
        var info = RequireLocation(entity);
        ApplyTypedSet(entity, info, componentType, in component);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyTypedSet<T>(Entity entity, EntityRecord info, ComponentType componentType, in T component) where T : unmanaged
    {
        var archetype = info.Archetype!;

        if (!archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");
        }

        var world = archetype._owner;
        if (world is not null)
        {
            if (world._singlePreviousQuery is { } q)
            {
                // Fast path: inline capture, no dispatch
                q.CaptureOld(entity, archetype, info.RowIndex);
                archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
                // Lazy New: read from live storage in Changes()
            }
            else if (world._anyPreviousTrackingActive)
            {
                // Slow path: dispatch for multiple queries
                world.DispatchBeforeWrite(entity, archetype, info.RowIndex);
                archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
                world.DispatchAfterWrite(entity, archetype, info.RowIndex);
            }
            else
            {
                archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            }
            return;
        }

        archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
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

        // Pre-hook: capture Old values before entity moves to new archetype
        if (_anyTrackingActive) DispatchBeforeTransition(entity, archetype, info.RowIndex);

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

        var world = archetype._owner;
        if (world is not null)
        {
            world.DispatchBeforeWrite(entity, archetype, info.RowIndex);
            archetype.WriteComponentRaw(componentIndex, info.RowIndex, source);
            world.DispatchAfterWrite(entity, archetype, info.RowIndex);
            return;
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
        if (_anyTrackingActive) AppendTransition(entity, sourceArchetype, destination);
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

        // Pre-hook: capture Old values before entity moves out
        if (_anyTrackingActive) DispatchBeforeTransition(entity, archetype, info.RowIndex);

        MoveEntity(entity, info, destination!);
        if (_anyTrackingActive) AppendTransition(entity, archetype, destination!);
    }

}
