using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Owns entity storage and queries.
/// </summary>
public sealed class World : IDisposable
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
    private readonly Dictionary<Type, ComponentType> _replayComponentTypeScratch = new(16);

    private bool _disposed;

#if DEBUG
    private int _debugEntityCapacityGrowCount;
    private int _debugDestroyScratchGrowCount;
    private int _debugMaxEntityCapacity;
    private int _debugLastEntityCapacityBefore;
    private int _debugLastEntityCapacityAfter;
    private int _debugLastDestroyOrderScratchCapacityBefore;
    private int _debugLastDestroyOrderScratchCapacityAfter;
    private int _debugLastDestroyVisitedScratchCapacityBefore;
    private int _debugLastDestroyVisitedScratchCapacityAfter;
#endif

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
    /// Returns a snapshot of debug-only world metrics.
    /// </summary>
    public WorldDebugMetrics GetDebugMetrics()
    {
#if DEBUG
        return new WorldDebugMetrics(
            true,
            _debugEntityCapacityGrowCount,
            _debugDestroyScratchGrowCount,
            _records.Length,
            Math.Max(_debugMaxEntityCapacity, _records.Length),
            _debugLastEntityCapacityBefore,
            _debugLastEntityCapacityAfter,
            _entitySlotCount,
            _destroyOrderScratch.Capacity,
            _destroyVisitedGen.Length,
            _debugLastDestroyOrderScratchCapacityBefore,
            _debugLastDestroyOrderScratchCapacityAfter,
            _debugLastDestroyVisitedScratchCapacityBefore,
            _debugLastDestroyVisitedScratchCapacityAfter);
#else
        return default;
#endif
    }

    /// <summary>
    /// Returns a stable text report for debug-only world metrics.
    /// </summary>
    public string GetDebugReport()
    {
#if DEBUG
        var metrics = GetDebugMetrics();
        return $"MiniArch World Debug Metrics\n" +
            $"enabled: {metrics.IsEnabled}\n" +
            $"entity_capacity_grow_count: {metrics.EntityCapacityGrowCount}\n" +
            $"destroy_scratch_grow_count: {metrics.DestroyScratchGrowCount}\n" +
            $"entity_capacity: {metrics.EntityCapacity}\n" +
            $"max_entity_capacity: {metrics.MaxEntityCapacity}\n" +
            $"last_entity_capacity: {metrics.LastEntityCapacityBefore}->{metrics.LastEntityCapacityAfter}\n" +
            $"entity_slot_count: {metrics.EntitySlotCount}\n" +
            $"destroy_order_scratch_capacity: {metrics.DestroyOrderScratchCapacity}\n" +
            $"destroy_visited_scratch_capacity: {metrics.DestroyVisitedScratchCapacity}\n" +
            $"last_destroy_order_scratch_capacity: {metrics.LastDestroyOrderScratchCapacityBefore}->{metrics.LastDestroyOrderScratchCapacityAfter}\n" +
            $"last_destroy_visited_scratch_capacity: {metrics.LastDestroyVisitedScratchCapacityBefore}->{metrics.LastDestroyVisitedScratchCapacityAfter}";
#else
        return "MiniArch World Debug Metrics\ndisabled";
#endif
    }

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

    internal void SetSnapshotEntityVersion(int entityId, int version)
    {
        ValidateSnapshotEntitySlot(entityId);
        _records[entityId].Version = version;
    }

    internal int GetEntityVersion(int entityId)
    {
        ValidateSnapshotEntitySlot(entityId);
        return _records[entityId].Version;
    }

    internal void SetSnapshotLocation(Entity entity, Archetype archetype, int rowIndex)
    {
        ValidateSnapshotEntitySlot(entity.Id);
        _records[entity.Id].Archetype = archetype;
        _records[entity.Id].RowIndex = rowIndex;
    }

    internal void RebuildFreeIdStack()
    {
        if (_freeIds.Length < _entitySlotCount)
        {
            Array.Resize(ref _freeIds, _entitySlotCount);
        }

        _freeIdCount = 0;
        for (var id = _entitySlotCount - 1; id >= 0; id--)
        {
            if (!_records[id].IsOccupied)
            {
                _freeIds[_freeIdCount++] = new RecycledEntity(id, _records[id].Version);
            }
        }
    }

    /// <summary>
    /// Creates an empty entity.
    /// </summary>
    public Entity Create()
    {
        ThrowIfDisposed();
        var archetype = GetOrCreateArchetype(Signature.Empty);
        return CreateInArchetype(archetype, out _);
    }

    /// <summary>
    /// Creates an entity with one component.
    /// </summary>
    public Entity Create<T1>(T1 component1)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var archetype = GetOrCreateCreateArchetype<T1>(componentType1);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        archetype.SetComponentAtTyped(0, rowIndex, in component1);
        return entity;
    }

    /// <summary>
    /// Creates an entity with two components.
    /// </summary>
    public Entity Create<T1, T2>(T1 component1, T2 component2)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var archetype = GetOrCreateCreateArchetype<T1, T2>(componentType1, componentType2);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType1), rowIndex, in component1);
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType2), rowIndex, in component2);
        return entity;
    }

    /// <summary>
    /// Creates an entity with three components.
    /// </summary>
    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3>(componentType1, componentType2, componentType3);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        return entity;
    }

    /// <summary>
    /// Creates an entity with four components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4>(T1 component1, T2 component2, T3 component3, T4 component4)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4>(componentType1, componentType2, componentType3, componentType4);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        return entity;
    }

    /// <summary>
    /// Creates an entity with five components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5>(componentType1, componentType2, componentType3, componentType4, componentType5);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        return entity;
    }

    /// <summary>
    /// Creates an entity with six components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        return entity;
    }

    /// <summary>
    /// Creates an entity with seven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        return entity;
    }

    /// <summary>
    /// Creates an entity with eight components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        return entity;
    }

    /// <summary>
    /// Creates an entity with nine components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        return entity;
    }

    /// <summary>
    /// Creates an entity with ten components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        return entity;
    }

    /// <summary>
    /// Creates an entity with eleven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        return entity;
    }

    /// <summary>
    /// Creates an entity with twelve components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        return entity;
    }

    /// <summary>
    /// Creates an entity with thirteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        return entity;
    }

    /// <summary>
    /// Creates an entity with fourteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        return entity;
    }

    /// <summary>
    /// Creates an entity with fifteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, rowIndex, componentType15, in component15);
        return entity;
    }

    /// <summary>
    /// Creates an entity with sixteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var componentType16 = GetComponentType<T16>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15, componentType16);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, rowIndex, componentType15, in component15);
        SetCreatedComponent(archetype, rowIndex, componentType16, in component16);
        return entity;
    }

    /// <summary>
    /// Creates many empty entities.
    /// </summary>
    public void CreateMany(Span<Entity> entities)
    {
        ThrowIfDisposed();
        if (entities.Length == 0)
        {
            return;
        }

        if (_freeIdCount == 0)
        {
            CreateManyFresh(entities);
            return;
        }

        var reusedCount = Math.Min(entities.Length, _freeIdCount);
        var startId = AppendEntitySlots(entities.Length - reusedCount);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var startRow = archetype.ReserveRows(entities.Length);
        Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[1];
        ranges[0] = new EntityBatchRange(startRow, entities.Length);
        WriteCreatedEntitiesAndLocations(archetype, entities, ranges, reusedCount, startId);
    }

    /// <summary>
    /// Ensures entity metadata capacity.
    /// </summary>
    public void EnsureCapacity(int entityCapacity)
    {
        ThrowIfDisposed();
        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

#if DEBUG
        var beforeCapacity = _records.Length;
#endif
        if (_records.Length < entityCapacity)
        {
            Array.Resize(ref _records, entityCapacity);
        }

#if DEBUG
        var afterCapacity = _records.Length;
        if (afterCapacity > beforeCapacity)
        {
            _debugEntityCapacityGrowCount++;
            _debugLastEntityCapacityBefore = beforeCapacity;
            _debugLastEntityCapacityAfter = afterCapacity;
            _debugMaxEntityCapacity = Math.Max(_debugMaxEntityCapacity, afterCapacity);
        }
#endif

        EnsureDestroyScratchCapacity(entityCapacity);
    }

    private void EnsureEntityCapacity(int requiredCount)
    {
        if (requiredCount <= _records.Length) return;
#if DEBUG
        var beforeCapacity = _records.Length;
#endif
        var newLength = Math.Max(requiredCount, _records.Length * 2);
        Array.Resize(ref _records, newLength);
#if DEBUG
        _debugEntityCapacityGrowCount++;
        _debugLastEntityCapacityBefore = beforeCapacity;
        _debugLastEntityCapacityAfter = newLength;
        _debugMaxEntityCapacity = Math.Max(_debugMaxEntityCapacity, newLength);
#endif
    }

    /// <summary>
    /// Destroys an entity.
    /// </summary>
    public void Destroy(Entity entity)
    {
        ThrowIfDisposed();

        if (!_hierarchy.HasChildren(entity))
        {
            DestroySingle(entity);
            return;
        }

        _destroyOrderScratch.Clear();
        if (++_destroyCurrentGen == 0)
        {
            Array.Clear(_destroyVisitedGen);
            _destroyCurrentGen = 1;
        }

        try
        {
            _hierarchy.CollectDestroySubtree(this, entity, _destroyVisitedGen, _destroyCurrentGen, _destroyOrderScratch);
            if (_destroyOrderScratch.Count == 0)
            {
                throw new InvalidOperationException($"Cannot destroy entity {entity}: it is no longer alive. The entity may have already been destroyed.");
            }

            for (var index = 0; index < _destroyOrderScratch.Count; index++)
            {
                DestroySingle(_destroyOrderScratch[index]);
            }
        }
        finally
        {
            _destroyOrderScratch.Clear();
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
    /// Gets an entity-only query from a description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        ThrowIfDisposed();
        return new Query(GetAdvancedQuery(in description));
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
    /// Tries to get an entity location.
    /// </summary>
    public bool TryGetLocation(Entity entity, out EntityInfo info)
    {
        ThrowIfDisposed();
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
        {
            info = default;
            return false;
        }

        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied || record.Version != entity.Version)
        {
            info = default;
            return false;
        }

        info = new EntityInfo(record.Version, record.Archetype, record.RowIndex);
        return true;
    }

    /// <summary>
    /// Returns whether an entity is alive.
    /// </summary>
    public bool IsAlive(Entity entity)
    {
        ThrowIfDisposed();
        return TryGetLocation(entity, out _);
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

    internal unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, Type runtimeType, byte[] data, int offset)
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

    private Archetype GetOrCreateCreateArchetype<T1>(ComponentType componentType1)
    {
        var entry = CreateArchetypeCache<T1>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1));
        CreateArchetypeCache<T1>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2>(ComponentType componentType1, ComponentType componentType2)
    {
        var entry = CreateArchetypeCache<T1, T2>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2));
        CreateArchetypeCache<T1, T2>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3>(ComponentType ct1, ComponentType ct2, ComponentType ct3)
    {
        var entry = CreateArchetypeCache<T1, T2, T3>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3));
        CreateArchetypeCache<T1, T2, T3>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4));
        CreateArchetypeCache<T1, T2, T3, T4>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5));
        CreateArchetypeCache<T1, T2, T3, T4, T5>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15, ComponentType ct16)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15, ct16));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
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

    internal MiniArch.Core.Query GetAdvancedQuery(in QueryDescription description)
    {
        return GetOrCreateQuery(GetOrCreateQueryFilter(description));
    }

    internal MiniArch.Core.Query GetOrCreateQuery(QueryFilter filter)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queries);
            if (snapshot.TryGetValue(filter, out var query))
            {
                return query;
            }

            var candidate = new MiniArch.Core.Query(this, filter);
            var updated = new Dictionary<QueryFilter, MiniArch.Core.Query>(snapshot)
            {
                [filter] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queries, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private QueryFilter GetOrCreateQueryFilter(in QueryDescription description)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queryFiltersByDescription);
            if (snapshot.TryGetValue(description, out var filter))
            {
                return filter;
            }

            var candidate = new QueryFilter(
                CreateQueryComponentSet(description.GetRequiredTypes()),
                CreateQueryComponentSet(description.GetExcludedTypes()),
                CreateQueryComponentSet(description.GetAnyTypes()));
            var updated = new Dictionary<QueryDescription, QueryFilter>(snapshot)
            {
                [description] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queryFiltersByDescription, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private QueryComponentSet CreateQueryComponentSet(ReadOnlySpan<Type> types)
    {
        if (types.Length == 0)
        {
            return QueryComponentSet.Empty;
        }

        var componentTypes = new ComponentType[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            componentTypes[i] = ComponentRegistry.Shared.GetOrCreate(types[i]);
        }

        return QueryComponentSet.CreateFrom(componentTypes);
    }

    private void PublishArchetypeSnapshot(Archetype archetype)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _archetypeSnapshot);
            var updated = new Archetype[snapshot.Length + 1];
            Array.Copy(snapshot, updated, snapshot.Length);
            updated[^1] = archetype;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _archetypeSnapshot, updated, snapshot), snapshot))
            {
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityRecord GetRequiredLocation(Entity entity)
    {
        var id = entity.Id;
#if DEBUG
        if ((uint)id >= (uint)_entitySlotCount)
        {
            ThrowInvalidEntity(entity);
        }
#endif

        ref var record = ref _records[id];
        if (!record.IsOccupied || record.Version != entity.Version)
        {
            ThrowStaleEntity(entity);
        }

        return record;
    }

    [DoesNotReturn]
    private void ThrowInvalidEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} does not exist. The entity may have never been created, or its id is invalid.");
    }

    [DoesNotReturn]
    private void ThrowStaleEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} is no longer alive. It may have been destroyed in a previous frame or the handle is stale.");
    }

    private void DestroySingle(Entity entity)
    {
        var info = GetRequiredLocation(entity);
        var arch = info.Archetype!;
        arch.RemoveAt(info.RowIndex, out var movedEntity);
        if (_hierarchy.HasAnyLinks(entity))
        {
            _hierarchy.RemoveDestroyed(entity);
        }

        ref var record = ref _records[entity.Id];
        record = default;
        record.Version = entity.Version + 1;
        PushFreeIdUnsafe(entity.Id, entity.Version + 1);

        if (movedEntity.IsValid)
        {
            ref var movedRecord = ref _records[movedEntity.Id];
            movedRecord.Archetype = info.Archetype;
            movedRecord.RowIndex = info.RowIndex;
        }
    }

    private Entity CreateInArchetype(Archetype archetype, out int rowIndex)
    {
        var id = AcquireEntityIdUnsafe(out var version);
        var entity = new Entity(id, version);
        rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
        return entity;
    }

    private static void SetCreatedComponent<T>(Archetype archetype, int rowIndex, ComponentType componentType, in T component)
    {
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType), rowIndex, in component);
    }

    private int AppendEntitySlots(int newEntityCount)
    {
        var startId = _entitySlotCount;
        if (newEntityCount == 0)
        {
            return startId;
        }

        var requiredCount = startId + newEntityCount;
        EnsureBatchCapacity(requiredCount, newEntityCount);
        EnsureEntityCapacity(requiredCount);
        _entitySlotCount = requiredCount;
        // Initialize new slots: version=1, location=empty
        var newSlots = _records.AsSpan(startId, newEntityCount);
        for (var i = 0; i < newSlots.Length; i++)
        {
            newSlots[i] = new EntityRecord { Version = 1 };
        }
        return startId;
    }

    private void EnsureBatchCapacity(int requiredCount, int batchCount)
    {
        if (_records.Length >= requiredCount)
        {
            return;
        }

        var targetCapacity = requiredCount + (batchCount / 2);
        if (targetCapacity < requiredCount)
        {
            targetCapacity = requiredCount;
        }

        EnsureCapacity(targetCapacity);
    }

    private void CreateManyFresh(Span<Entity> entities)
    {
        var startId = AppendEntitySlots(entities.Length);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var startRow = archetype.ReserveRows(entities.Length);
        Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[1];
        ranges[0] = new EntityBatchRange(startRow, entities.Length);
        WriteCreatedEntitiesAndLocations(archetype, entities, ranges, 0, startId);
    }

    private void WriteCreatedEntitiesAndLocations(Archetype archetype, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges, int reusedCount, int startId)
    {
        var freeIds = _freeIds;
        var freeIndex = _freeIdCount;
        var entityIndex = 0;
        var nextId = startId;

        foreach (var range in ranges)
        {
            var chunkEntities = archetype.GetReservedEntities(range.StartRow, range.Count);
            var rowOffset = 0;

            for (; rowOffset < range.Count && entityIndex < reusedCount; rowOffset++)
            {
                var recycled = freeIds[--freeIndex];
                var entity = new Entity(recycled.Id, recycled.Version);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }

            for (; rowOffset < range.Count; rowOffset++)
            {
                var entity = new Entity(nextId++, 1);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }
        }

        _freeIdCount = freeIndex;
    }

    private readonly object _entityIdLock = new();

    private int AcquireEntityIdUnsafe(out int version)
    {
        if (_freeIdCount > 0)
        {
            var recycled = PopFreeIdUnsafe();
            version = recycled.Version;
            return recycled.Id;
        }

        var id = _entitySlotCount;
        EnsureEntityCapacity(_entitySlotCount + 1);
        _records[_entitySlotCount] = new EntityRecord { Version = 1 };
        _entitySlotCount++;
        EnsureDestroyScratchCapacity(_entitySlotCount);
        version = 1;
        return id;
    }

    private void PushFreeIdUnsafe(int id, int version)
    {
        if (_freeIdCount == _freeIds.Length)
        {
            var newCapacity = _freeIds.Length == 0 ? 4 : _freeIds.Length * 2;
            Array.Resize(ref _freeIds, newCapacity);
        }

        _freeIds[_freeIdCount++] = new RecycledEntity(id, version);
    }

    private RecycledEntity PopFreeIdUnsafe()
    {
        return _freeIds[--_freeIdCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentType GetComponentType<T>()
    {
        return Component<T>.ComponentType;
    }

    private void ValidateSnapshotEntitySlot(int entityId)
    {
        if (entityId < 0 || entityId >= _entitySlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId));
        }
    }

    internal void LinkSnapshot(Entity parent, Entity child)
    {
        _hierarchy.LinkRestored(parent, child);
    }

    internal Entity ReserveDeferredEntity()
    {
        lock (_entityIdLock)
        {
            var id = AcquireEntityIdUnsafe(out var version);
            return new Entity(id, version);
        }
    }

    internal void ReleaseReservedEntity(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
        {
            throw new InvalidOperationException($"Entity {entity} is not a valid deferred entity. The entity handle may be invalid or already materialized.");
        }

        ref var record = ref _records[entity.Id];
        if (record.IsOccupied || record.Version != entity.Version)
        {
            throw new InvalidOperationException($"Entity {entity} is not a deferred reserved entity. It may have already been materialized or was never reserved via CommandBuffer.");
        }

        var nextVersion = entity.Version + 1;
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
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

        Dictionary<Type, ComponentType>? componentTypeCache = null;
        if (!trusted)
        {
            componentTypeCache = _replayComponentTypeScratch;
            componentTypeCache.Clear();
        }

        if (!trusted)
        {
            foreach (var reserved in delta.ReservedEntities)
                EnsureReplayReservation(reserved);
        }

        foreach (var released in delta.ReleasedEntities)
            ReleaseReservedEntity(released);

        foreach (var created in delta.CreatedEntities)
        {
            if (trusted)
                MaterializeReservedEntityTrusted(created.Entity, created.Signature, created.Components);
            else
                MaterializeReservedEntity(created.Entity, created.Components, componentTypeCache, reservationChecked: true);
        }

        foreach (var link in delta.LinkCommands)
            Link(link.Parent, link.Child);

        foreach (var unlink in delta.UnlinkCommands)
            Unlink(unlink.Child);

        unsafe
        {
            foreach (var add in delta.AddCommands)
            {
                var componentType = trusted
                    ? add.ComponentType
                    : ResolveCompiledComponentType(add.RuntimeType, add.ComponentType, componentTypeCache);
                ApplyRawAddOrSet(add.Entity, componentType, add.RuntimeType, add.Data, add.DataOffset);
            }

            foreach (var set in delta.SetCommands)
            {
                var componentType = trusted
                    ? set.ComponentType
                    : ResolveCompiledComponentType(set.RuntimeType, set.ComponentType, componentTypeCache);
                ApplyRawAddOrSet(set.Entity, componentType, set.RuntimeType, set.Data, set.DataOffset);
            }
        }

        foreach (var remove in delta.RemoveCommands)
        {
            var componentType = trusted
                ? remove.ComponentType
                : ResolveCompiledComponentType(remove.RuntimeType, remove.ComponentType, componentTypeCache);
            RemoveBoxed(remove.Entity, componentType);
        }

        foreach (var entity in delta.DestroyedEntities)
            if (IsAlive(entity)) Destroy(entity);

        componentTypeCache?.Clear();
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        IReadOnlyList<RawComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        var signature = BuildReplaySignature(components, componentTypeCache);
        MaterializeReservedEntityCore(entity, signature, components, componentTypeCache, trustCompiledComponentTypes: false);
    }

    internal void MaterializeReservedEntityTrusted(Entity entity, Signature signature, IReadOnlyList<RawComponentValue> components)
    {
        MaterializeReservedEntityCore(entity, signature, components, componentTypeCache: null, trustCompiledComponentTypes: true);
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
        IReadOnlyList<RawComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache,
        bool trustCompiledComponentTypes)
    {
        var archetype = GetOrCreateArchetype(signature);
        var rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[entity.Id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var resolvedType = trustCompiledComponentTypes
                ? component.ComponentType
                : ResolveCompiledComponentType(component.RuntimeType, component.ComponentType, componentTypeCache);
            var columnIndex = archetype.GetComponentIndex(resolvedType);
            unsafe
            {
                fixed (byte* ptr = component.Data)
                {
                    archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + component.DataOffset);
                }
            }
        }
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

    private void EnsureDestroyScratchCapacity(int entityCount)
    {
        if (entityCount <= 0)
        {
            return;
        }

#if DEBUG
        var beforeOrderCapacity = _destroyOrderScratch.Capacity;
        var beforeVisitedCapacity = _destroyVisitedGen.Length;
#endif
        _destroyOrderScratch.EnsureCapacity(entityCount);
        if (_destroyVisitedGen.Length < entityCount)
        {
            Array.Resize(ref _destroyVisitedGen, Math.Max(entityCount, _destroyVisitedGen.Length * 2));
        }
#if DEBUG
        if (_destroyOrderScratch.Capacity > beforeOrderCapacity || _destroyVisitedGen.Length > beforeVisitedCapacity)
        {
            _debugDestroyScratchGrowCount++;
            _debugLastDestroyOrderScratchCapacityBefore = beforeOrderCapacity;
            _debugLastDestroyOrderScratchCapacityAfter = _destroyOrderScratch.Capacity;
            _debugLastDestroyVisitedScratchCapacityBefore = beforeVisitedCapacity;
            _debugLastDestroyVisitedScratchCapacityAfter = _destroyVisitedGen.Length;
        }
#endif
    }

    private void EnsureReplayReservation(Entity entity)
    {
        if (entity.Id < _entitySlotCount &&
            !_records[entity.Id].IsOccupied &&
            _records[entity.Id].Version == entity.Version &&
            !IsEntityAvailableInFreeList(entity))
        {
            return;
        }

        var reserved = ReserveDeferredEntity();
        if (reserved != entity)
        {
            throw new InvalidOperationException($"Replay failed: expected to reserve entity {entity} but got {reserved} instead. The source and target worlds may be out of sync.");
        }
    }

    private bool IsEntityAvailableInFreeList(Entity entity)
    {
        for (var index = 0; index < _freeIdCount; index++)
        {
            var recycled = _freeIds[index];
            if (recycled.Id == entity.Id && recycled.Version == entity.Version)
            {
                return true;
            }
        }

        return false;
    }

    private Signature BuildReplaySignature(
        IReadOnlyList<RawComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null)
    {
        if (components.Count == 0)
        {
            return Signature.Empty;
        }

        var componentTypes = new ComponentType[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            componentTypes[index] = ResolveCompiledComponentType(component.RuntimeType, component.ComponentType, componentTypeCache);
        }

        return new Signature(componentTypes);
    }

    private ComponentType ResolveCompiledComponentType(
        Type runtimeType,
        ComponentType componentType,
        Dictionary<Type, ComponentType>? cache)
    {
        if (componentType.IsValid && ComponentRegistry.Shared.TryGetType(componentType, out var existing) && existing == runtimeType)
        {
            return componentType;
        }

        if (cache is not null && cache.TryGetValue(runtimeType, out var resolved))
        {
            return resolved;
        }

        resolved = GetComponentType(runtimeType);
        cache?.Add(runtimeType, resolved);
        return resolved;
    }

    private ComponentType GetComponentType(Type componentType)
    {
        return ComponentRegistry.Shared.GetOrCreate(componentType);
    }

    private static class CreateArchetypeCache<T1>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>
    {
        public static CachedCreateArchetype? Entry;
    }

    private sealed class CachedCreateArchetype
    {
        private readonly WeakReference<World> _world;
        private readonly WeakReference<Archetype> _archetype;

        public CachedCreateArchetype(World world, int generation, Archetype archetype)
        {
            _world = new WeakReference<World>(world);
            _archetype = new WeakReference<Archetype>(archetype);
            Generation = generation;
        }

        public int Generation { get; }

        public bool TryGetArchetype(World world, int generation, [NotNullWhen(true)] out Archetype? archetype)
        {
            archetype = null;
            if (generation != Generation ||
                !_world.TryGetTarget(out var cachedWorld) ||
                !ReferenceEquals(cachedWorld, world))
            {
                return false;
            }

            return _archetype.TryGetTarget(out archetype);
        }
    }

    private readonly record struct CloneWork(Entity Source, Entity CloneEntity);

    private readonly record struct RecycledEntity(int Id, int Version);
}

