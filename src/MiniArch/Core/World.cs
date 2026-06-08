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
    /// Gets an entity-only query from a description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        ThrowIfDisposed();
        return new Query(GetAdvancedQuery(in description));
    }

    /// <summary>
    /// Gets the first entity in the archetype that stores component
    /// <typeparamref name="T" />.
    /// Uses the same generic archetype cache as <see cref="Create{T}" />,
    /// so the hot path is O(1) with no allocation and no dictionary lookup.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No entity with component <typeparamref name="T" /> exists.
    /// </exception>
    public Entity GetFirst<T>() where T : struct
    {
        ThrowIfDisposed();
        if (!TryGetCreateArchetype<T>(out var archetype) || archetype.EntityCount == 0)
        {
            throw new InvalidOperationException(
                $"No entity with component '{typeof(T).Name}' exists.");
        }
        return archetype.GetEntity(0);
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

    private bool TryGetCreateArchetype<T>([NotNullWhen(true)] out Archetype? archetype) where T : struct
    {
        var entry = CreateArchetypeCache<T>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out archetype))
        {
            return true;
        }

        var ct = Component<T>.ComponentType;
        if (!_archetypes.TryGetValue(new Signature(ct), out archetype))
        {
            return false;
        }

        CreateArchetypeCache<T>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return true;
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






    private static void SetCreatedComponent<T>(Archetype archetype, int rowIndex, ComponentType componentType, in T component)
    {
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType), rowIndex, in component);
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

}

