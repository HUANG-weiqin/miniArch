using System.Numerics;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature in column-major <see cref="byte"/> storage.
/// </summary>
/// <remarks>
/// <b>Two storage modes</b>, switched automatically by <see cref="EnsureCapacity"/>:
/// <list type="bullet">
/// <item><b>Non-chunked</b> (small archetype): a single flat
/// <c>_data</c> buffer + <c>_entities</c> array. Doubles in place until
/// <c>_capacity * 2</c> would exceed <see cref="_segmentCapacity"/>.</item>
/// <item><b>Chunked</b> (large archetype): a <see cref="Segment"/>[] where each
/// segment owns its own <c>Entities</c>/<c>Data</c> arrays of fixed size
/// <see cref="_segmentCapacity"/> (target ~2 MB per segment, rounded up to a
/// power of two so row resolution uses <c>SHR</c>/<c>AND</c> instead of
/// <c>DIV</c>). Promotion is one-way; <see cref="IsChunked"/> never reverts.
/// <see cref="ChunkView"/> exposes one segment per chunk to public consumers.</item>
/// </list>
/// </remarks>
internal sealed partial class Archetype
{
    // --- Storage (non-chunked mode) ---
    private Entity[] _entities;
    private byte[] _data;
    private int[] _columnByteOffsets;
    private int[] _elementSizes;
    private int _count;
    private int _capacity;

    // --- Storage (chunked mode) ---
    private bool _isChunked;
    private Segment[] _segments = null!;
    private int _segmentCount;

    // Cached flat entity view for chunked mode, rebuilt on layout change.
    private int _flatEntitiesGeneration;
    private Entity[]? _cachedFlatEntities;
    private int _cachedFlatEntitiesGeneration = -1;

    // Fixed entity capacity per segment (power of two), computed from component sizes once.
    // All segments share the same capacity so column byte offsets are identical.
    // _segmentBitShift and _segmentMask provide a SHR+AND hot path instead of DIV.
    private readonly int _segmentCapacity;
    private readonly int _segmentBitShift;
    private readonly int _segmentMask;

    // --- Archetype metadata ---
    private readonly Signature _signature;
    private readonly Type[] _componentTypes;
    private readonly int[] _componentIdToColumnIndex;
    private Archetype?[] _addDestinationCache = Array.Empty<Archetype?>();
    private Archetype?[] _removeDestinationCache = Array.Empty<Archetype?>();
    internal Archetype(Signature signature, Type[] componentTypes, int capacity = 4)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        if (componentTypes.Length != signature.Count)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        _signature = signature;
        _capacity = capacity;
        _componentTypes = componentTypes;
        _componentIdToColumnIndex = ComponentColumnMap.Build(signature);
        var segCap = ComputeSegmentEntityCapacity(componentTypes);
        _segmentCapacity = segCap;
        _segmentBitShift = BitOperations.TrailingZeroCount((uint)segCap);
        _segmentMask = segCap - 1;
        _entities = new Entity[capacity];
        (_data, _columnByteOffsets, _elementSizes) = CreateStorage(signature, componentTypes, capacity);
    }

    /// <summary>
    /// Gets the archetype signature.
    /// </summary>
    internal Signature Signature => _signature;

    /// <summary>
    /// Gets the entity count.
    /// </summary>
    internal int EntityCount => _count;

    /// <summary>
    /// Whether this archetype has switched to chunked (segmented) storage.
    /// </summary>
    internal bool IsChunked => _isChunked;

    /// <summary>
    /// Gets the current physical capacity (maximum entities before resize).
    /// </summary>
    internal int Capacity => _isChunked ? ComputeChunkedCapacity() : _capacity;

    private int ComputeChunkedCapacity()
    {
        var total = 0;
        for (var i = 0; i < _segmentCount; i++)
            total += _segments[i].Entities.Length;
        return total;
    }

    /// <summary>
    /// Gets the component types that define this archetype's signature.
    /// </summary>
    internal IReadOnlyList<Type> ComponentTypes => _componentTypes;

    internal bool TryGetAddDestination(ComponentType component, out Archetype? destination) =>
        TryGetCached(_addDestinationCache, component, out destination);
    internal bool TryGetRemoveDestination(ComponentType component, out Archetype? destination) =>
        TryGetCached(_removeDestinationCache, component, out destination);
    internal void CacheAddDestination(ComponentType component, Archetype destination) =>
        CacheWrite(ref _addDestinationCache, component, destination);
    internal void CacheRemoveDestination(ComponentType component, Archetype destination) =>
        CacheWrite(ref _removeDestinationCache, component, destination);

    private static bool TryGetCached(Archetype?[] cache, ComponentType component, out Archetype? destination)
    {
        var id = component.Value;
        if ((uint)id >= (uint)cache.Length) { destination = null; return false; }
        destination = cache[id];
        return destination is not null;
    }

    private static void CacheWrite(ref Archetype?[] cache, ComponentType component, Archetype destination)
    {
        var id = component.Value;
        if ((uint)id >= (uint)cache.Length) Array.Resize(ref cache, id + 1);
        cache[id] = destination;
    }

    // Target 2 MB per segment: large enough for L3 cache residency,
    // small enough that allocation and GC compaction are near-instant.
    private const int TargetSegmentBytes = 2 * 1024 * 1024;

    private static int ComputeSegmentEntityCapacity(Type[] componentTypes)
    {
        var perEntity = 0;
        for (var i = 0; i < componentTypes.Length; i++)
        {
            var elemSize = ComponentSizeCache.GetSize(componentTypes[i]);
            perEntity = AlignUp(perEntity, Math.Min(elemSize, 8));
            perEntity += elemSize;
        }
        var raw = perEntity > 0 ? Math.Max(16, TargetSegmentBytes / perEntity) : 65536;
        return (int)BitOperations.RoundUpToPowerOf2((uint)raw);
    }

    internal struct Segment
    {
        public Entity[] Entities;
        public byte[] Data;
        public int Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetComponentIndex(ComponentType component, out int columnIndex)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)_componentIdToColumnIndex.Length)
        {
            columnIndex = -1;
            return false;
        }

        columnIndex = _componentIdToColumnIndex[componentId];
        return columnIndex >= 0;
    }

    internal int GetComponentIndex(ComponentType component)
    {
        if (TryGetComponentIndex(component, out var columnIndex))
            return columnIndex;

        throw new ArgumentException($"Archetype does not contain component {component.Value}.", nameof(component));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndexFast(ComponentType component) =>
        _componentIdToColumnIndex[component.Value];
}
