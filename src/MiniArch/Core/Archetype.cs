using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature.
/// Archetype is the sole storage unit — there is exactly one data block
/// per archetype (no multi-chunk splitting).
/// </summary>
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

    // Fixed entity capacity per segment, computed from component sizes once.
    // All segments share the same capacity so column byte offsets are identical.
    private readonly int _segmentEntityCapacity;

    // --- Archetype metadata ---
    private readonly Signature _signature;
    private readonly Type[] _componentTypes;
    private readonly int[] _componentIdToColumnIndex;
    private Archetype?[] _addDestinationCache = Array.Empty<Archetype?>();
    private Archetype?[] _removeDestinationCache = Array.Empty<Archetype?>();
    internal Archetype(Signature signature, Type[] componentTypes, int capacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        if (componentTypes.Length != signature.Count)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        _signature = signature;
        _capacity = capacity;
        _componentTypes = componentTypes;
        _componentIdToColumnIndex = ComponentColumnMap.Build(signature);
        _segmentEntityCapacity = ComputeSegmentEntityCapacity(componentTypes);
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

    internal void ForceChunkedForTesting()
    {
        if (!_isChunked)
        {
            NormalizeForChunked();
            ConvertToChunked();
        }
    }

    internal void AddSegmentForTesting()
    {
        GrowChunked(1);
    }

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

    // .NET maximum array element count; avoids overflow in _capacity * 2.
    private const int ArrayMaxLength = 0x7FFFFFC7; // Array.MaxLength

    // Target 2 MB per segment: large enough for L3 cache residency,
    // small enough that allocation and GC compaction are near-instant.
    private const int TargetSegmentBytes = 2 * 1024 * 1024;

    private int SegmentEntityCapacity => _segmentEntityCapacity;

    private static int ComputeSegmentEntityCapacity(Type[] componentTypes)
    {
        var perEntity = 0;
        for (var i = 0; i < componentTypes.Length; i++)
        {
            var elemSize = ComponentSizeCache.GetSize(componentTypes[i]);
            perEntity = AlignUp(perEntity, Math.Min(elemSize, 8));
            perEntity += elemSize;
        }
        return perEntity > 0 ? Math.Max(16, TargetSegmentBytes / perEntity) : 65536;
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
