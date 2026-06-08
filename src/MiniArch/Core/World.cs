using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Owns entity storage and queries.
/// </summary>
public sealed partial class World : IDisposable
{
    private const int DefaultChunkCapacity = 128;
    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    private readonly HierarchyTable _hierarchy = new();
    private EntityRecord[] _records;
    private int _entitySlotCount;
    private Dictionary<QueryDescription, QueryFilter> _queryFiltersByDescription = new();
    private Dictionary<QueryFilter, MiniArch.Core.Query> _queries = new();
    private Archetype[] _archetypeSnapshot = Array.Empty<Archetype>();
    private readonly int _chunkCapacity;
    private RecycledEntity[] _freeIds;
    private int _freeIdCount;

    private int _archetypeVersion;
    private int _createArchetypeCacheGeneration;
    private readonly List<Entity> _destroyOrderScratch;
    private int[] _destroyVisitedGen = [];
    private int _destroyCurrentGen;

    private bool _disposed;


    /// <summary>
    /// Creates a world.
    /// </summary>
    public World(int chunkCapacity = DefaultChunkCapacity, int entityCapacity = 64)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity), "Chunk capacity must be positive.");
        }

        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity), "Entity capacity must be non-negative.");
        }

        _chunkCapacity = chunkCapacity;
        _records = new EntityRecord[entityCapacity];
        _entitySlotCount = 0;
        _freeIds = entityCapacity == 0 ? Array.Empty<RecycledEntity>() : new RecycledEntity[entityCapacity];
        _destroyVisitedGen = entityCapacity == 0 ? [] : new int[entityCapacity];
        _destroyOrderScratch = new List<Entity>(entityCapacity);
    }

    /// <summary>
    /// Releases owned runtime state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archetypes.Clear();
        _queryFiltersByDescription.Clear();
        _queries.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _entitySlotCount = 0;
        _records = Array.Empty<EntityRecord>();
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if DEBUG
        if (_disposed) throw new ObjectDisposedException(nameof(World));
#endif
    }

    /// <summary>
    /// Gets the component registry.
    /// </summary>
    public ComponentRegistry Components
    {
        get
        {
            ThrowIfDisposed();
            return ComponentRegistry.Shared;
        }
    }

    /// <summary>
    /// Gets the entity metadata capacity.
    /// </summary>
    public int EntityCapacity => _records.Length;


    /// <summary>
    /// Gets the number of currently alive entities.
    /// </summary>
    public int EntityCount => _entitySlotCount - _freeIdCount;

    internal int ChunkCapacity => _chunkCapacity;

    internal int EntitySlotCount => _entitySlotCount;

    internal ReadOnlySpan<EntityRecord> EntityRecords => _records.AsSpan(0, _entitySlotCount);

    internal Archetype[] Archetypes => Volatile.Read(ref _archetypeSnapshot);

    internal HierarchyTable Hierarchy => _hierarchy;

    internal int ArchetypeVersion => _archetypeVersion;

    internal int CreateArchetypeCacheGeneration => _createArchetypeCacheGeneration;

    internal void Reset(int entitySlotCount)
    {
        if (entitySlotCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entitySlotCount));
        }

        _archetypes.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _queryFiltersByDescription = new Dictionary<QueryDescription, QueryFilter>();
        _queries = new Dictionary<QueryFilter, MiniArch.Core.Query>();
        _createArchetypeCacheGeneration++;
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();

        _entitySlotCount = 0;
        EnsureCapacity(entitySlotCount);
        _entitySlotCount = entitySlotCount;
        _records.AsSpan(0, entitySlotCount).Clear();

        if (_freeIds.Length < entitySlotCount)
        {
            Array.Resize(ref _freeIds, entitySlotCount);
        }
    }



























    /// <summary>
    /// Links a child to a parent.
    /// </summary>
    public void Link(Entity parent, Entity child)
    {
        ThrowIfDisposed();
        _hierarchy.Link(this, parent, child);
    }

    /// <summary>
    /// Unlinks a child.
    /// </summary>
    public void Unlink(Entity child)
    {
        ThrowIfDisposed();
        GetRequiredLocation(child);
        _hierarchy.Unlink(child);
    }

    /// <summary>
    /// Tries to get a parent entity.
    /// </summary>
    public bool TryGetParent(Entity child, out Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.TryGetParent(this, child, out parent);
    }

    /// <summary>
    /// Gets the direct children of an entity.
    /// </summary>
    public List<Entity> GetChildren(Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.GetChildren(this, parent);
    }




    /// <summary>
    /// Tries to read a component directly from an entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component)
    {
        ThrowIfDisposed();
        if (!TryGetLocation(entity, out var info))
        {
            component = default!;
            return false;
        }

        var componentType = GetComponentType<T>();
        if (!info.Archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            component = default!;
            return false;
        }

        component = info.Archetype
            .GetComponentAt<T>(componentIndex, info.RowIndex);
        return true;
    }

    /// <summary>
    /// Checks whether an entity has a specific component.
    /// Inlined hot path: no EntityInfo allocation, no Signature.Contains overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>(Entity entity) where T : struct
    {
        ThrowIfDisposed();
        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied || record.Version != entity.Version)
            return false;

        return record.Archetype!.TryGetComponentIndex(Component<T>.ComponentType, out _);
    }

    /// <summary>
    /// Gets a component directly without version or bounds checks.
    /// Use only when the entity is known to be alive and the component is known to exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get<T>(Entity entity)
    {
        ref var record = ref _records[entity.Id];
        var arch = record.Archetype!;
        return arch.GetComponentAt<T>(arch.GetComponentIndexFast(GetComponentType<T>()), record.RowIndex);
    }

    /// <summary>
    /// Gets a component by reference directly without version or bounds checks.
    /// Use only when the entity is known to be alive and the component is known to exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef<T>(Entity entity)
    {
        ref var record = ref _records[entity.Id];
        var arch = record.Archetype!;
        return ref arch.GetComponentRefAt<T>(arch.GetComponentIndexFast(GetComponentType<T>()), record.RowIndex);
    }



    /// <summary>
    /// Creates a cached <see cref="EntityAccessor"/> for multiple component
    /// reads/writes on the same entity. The entity→(archetype,chunk,row) lookup
    /// is performed once; subsequent <see cref="EntityAccessor.Get{T}"/>,
    /// <see cref="EntityAccessor.Set{T}"/>, and <see cref="EntityAccessor.Has{T}"/>
    /// calls on the returned accessor skip the entity lookup entirely.
    /// </summary>
    /// <remarks>
    /// Discard the accessor before any structural change (Add/Remove) that may
    /// move the entity to a different archetype.
    /// </remarks>
    public EntityAccessor Access(Entity entity)
    {
        ThrowIfDisposed();
        if (!TryGetLocation(entity, out var info))
        {
            throw new InvalidOperationException(
                $"Entity '{entity}' is not alive.");
        }

        return new EntityAccessor(info.Archetype, info.RowIndex);
    }



    /// <summary>
    /// Deep-clones an entity and its entire child subtree.
    /// All component data is copied into new entities in the same archetypes.
    /// Parent-child links within the subtree are preserved; the clone root has no parent.
    /// </summary>
    /// <param name="source">The entity to clone. Must be alive.</param>
    /// <returns>A new entity that is the root of the cloned subtree.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="source"/> is no longer alive.</exception>
    public Entity Clone(Entity source)
    {
        ThrowIfDisposed();
        var root = CloneSingle(source);
        DeepCloneChildren(source, root);
        return root;
    }

    private Entity CloneSingle(Entity source)
    {
        var sourceInfo = GetRequiredLocation(source);
        var archetype = sourceInfo.Archetype!;
        var entity = CreateInArchetype(archetype, out var destRow);
        archetype.CopySharedComponentsFrom(sourceInfo.Archetype!, sourceInfo.RowIndex, destRow);
        return entity;
    }

    private void DeepCloneChildren(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_hierarchy.HasChildren(sourceRoot)) return;

        var stack = ArrayPool<CloneWork>.Shared.Rent(16);
        var stackCount = 0;
        try
        {
            PushPooled(ref stack, ref stackCount, new CloneWork(sourceRoot, cloneRoot));
            while (stackCount > 0)
            {
                var work = stack[--stackCount];
                foreach (var child in _hierarchy.EnumerateChildren(this, work.Source))
                {
                    var cloneChild = CloneSingle(child);
                    _hierarchy.Link(this, work.CloneEntity, cloneChild);
                    PushPooled(ref stack, ref stackCount, new CloneWork(child, cloneChild));
                }
            }
        }
        finally
        {
            ArrayPool<CloneWork>.Shared.Return(stack);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushPooled<T>(ref T[] array, ref int count, T value)
    {
        if ((uint)count >= (uint)array.Length)
        {
            GrowPooled(ref array);
        }

        array[count++] = value;
    }

    private static void GrowPooled<T>(ref T[] array)
    {
        var next = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, next, array.Length);
        ArrayPool<T>.Shared.Return(array);
        array = next;
    }

    /// <summary>
    /// Creates a snapshot-equivalent clone of this world.
    /// </summary>
    public World Clone()
    {
        return WorldClone.Clone(this);
    }









    internal Archetype GetOrCreateArchetype(Signature signature)
    {
        if (_archetypes.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(signature, ResolveComponentTypes(signature), _chunkCapacity);
        _archetypes.Add(signature, archetype);
        PublishArchetypeSnapshot(archetype);
        _archetypeVersion++;
        return archetype;
    }


















    private Type[] ResolveComponentTypes(Signature signature)
    {
        var componentCount = signature.Count;
        if (componentCount == 0)
        {
            return Array.Empty<Type>();
        }

        var types = new Type[componentCount];
        var components = signature.AsSpan();
        for (var index = 0; index < componentCount; index++)
        {
            types[index] = ComponentRegistry.Shared.GetType(components[index]);
        }

        return types;
    }




















    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentType GetComponentType<T>()
    {
        return Component<T>.ComponentType;
    }


    internal void LinkSnapshot(Entity parent, Entity child)
    {
        _hierarchy.LinkRestored(parent, child);
    }



    /// <summary>
    /// Replays a frame delta into this world: reserves entities, materializes created entities,
    /// applies hierarchy link/unlink, add/set/remove components, and destroys entities in standard order.
    /// </summary>
    public void Replay(FrameDelta delta) => ReplayCore(delta, trusted: false);

    private void ReplayCore(FrameDelta delta, bool trusted)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(delta);

        if (!trusted)
        {
            foreach (var reserved in delta.ReservedEntities)
                EnsureReplayReservation(reserved);
        }

        foreach (var released in delta.ReleasedEntities)
            ReleaseReservedEntity(released);

        foreach (var created in delta.CreatedEntities)
        {
            var signature = BuildReplaySignature(created.Components);
            MaterializeReservedEntityCore(created.Entity, signature, created.Components);
        }

        foreach (var link in delta.LinkCommands)
            Link(link.Parent, link.Child);

        foreach (var unlink in delta.UnlinkCommands)
            Unlink(unlink.Child);

        unsafe
        {
            foreach (var add in delta.AddCommands)
                ApplyRawAddOrSet(add.Entity, add.ComponentType, add.Data, add.DataOffset);

            foreach (var set in delta.SetCommands)
                ApplyRawAddOrSet(set.Entity, set.ComponentType, set.Data, set.DataOffset);
        }

        foreach (var remove in delta.RemoveCommands)
            RemoveBoxed(remove.Entity, remove.ComponentType);

        foreach (var entity in delta.DestroyedEntities)
            if (IsAlive(entity)) Destroy(entity);
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        IReadOnlyList<RawComponentValue> components,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        var signature = BuildReplaySignature(components);
        MaterializeReservedEntityCore(entity, signature, components);
    }

    internal unsafe void MaterializeReservedEntityDirect(Entity entity, Archetype archetype, ReadOnlySpan<RawComponentValue> components)
    {
        var rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[entity.Id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;

        for (var index = 0; index < components.Length; index++)
        {
            ref readonly var component = ref components[index];
            var columnIndex = archetype.GetComponentIndex(component.ComponentType);
            fixed (byte* ptr = component.Data)
            {
                archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + component.DataOffset);
            }
        }
    }

    internal unsafe void MaterializeReservedEntityFast(
        Entity entity,
        Archetype archetype,
        ReadOnlySpan<CommandBuffer.CreatedComponent> components,
        List<byte[]> slabs)
    {
        var rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[entity.Id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;

        for (var index = 0; index < components.Length; index++)
        {
            ref readonly var cc = ref components[index];
            var columnIndex = archetype.GetComponentIndex(cc.ComponentType);
            var data = slabs[cc.SlabIndex];
            fixed (byte* ptr = data)
            {
                archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + cc.DataOffset);
            }
        }
    }

    private void MaterializeReservedEntityCore(
        Entity entity,
        Signature signature,
        IReadOnlyList<RawComponentValue> components)
    {
        var archetype = GetOrCreateArchetype(signature);
        var rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[entity.Id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var columnIndex = archetype.GetComponentIndex(component.ComponentType);
            unsafe
            {
                fixed (byte* ptr = component.Data)
                {
                    archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + component.DataOffset);
                }
            }
        }
    }





    private static Signature BuildReplaySignature(IReadOnlyList<RawComponentValue> components)
    {
        if (components.Count == 0)
        {
            return Signature.Empty;
        }

        var componentTypes = new ComponentType[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            componentTypes[index] = components[index].ComponentType;
        }

        return new Signature(componentTypes);
    }

    private ComponentType GetComponentType(Type componentType)
    {
        return ComponentRegistry.Shared.GetOrCreate(componentType);
    }


















    private readonly record struct CloneWork(Entity Source, Entity CloneEntity);

}

