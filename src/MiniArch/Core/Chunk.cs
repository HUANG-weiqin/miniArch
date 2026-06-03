using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Dense component storage for one signature.
/// </summary>
public sealed class Chunk
{
    private static readonly ConcurrentDictionary<Type, bool> ManagedReferenceCache = new();
    private static readonly MethodInfo ContainsManagedMethod = typeof(Chunk)
        .GetMethod(nameof(ContainsManagedReferences), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly Signature _signature;
    private Entity[] _entities;
    private byte[] _data;
    private readonly Type[] _componentTypes;
    private int[] _columnByteOffsets;
    private readonly int[] _elementSizes;
    internal byte[] GetDataArray() => _data;
    internal int[] GetColumnByteOffsets() => _columnByteOffsets;
    private readonly int[] _componentIdToColumnIndex;
    private readonly int _maxCapacity;

    internal Chunk(Signature signature, Type[] componentTypes, int[] componentIdToColumnIndex,
        int capacity = 4, int maxCapacity = -1)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(componentTypes);
        ArgumentNullException.ThrowIfNull(componentIdToColumnIndex);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _signature = signature;
        _componentIdToColumnIndex = componentIdToColumnIndex;
        _entities = new Entity[capacity];
        _componentTypes = componentTypes;
        (_data, _columnByteOffsets, _elementSizes) = CreateStorage(signature, componentTypes, capacity);
        _maxCapacity = maxCapacity < 0 ? capacity : Math.Max(capacity, maxCapacity);
    }

    /// <summary>
    /// Gets the chunk max capacity (logical entity limit before full).
    /// </summary>
    public int Capacity => _maxCapacity;

    /// <summary>
    /// Gets the live row count.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets live entities as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        return _entities.AsSpan(0, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity[] GetEntityStorage() => _entities;

    /// <summary>
    /// Gets the entity at a row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity GetEntity(int row)
    {
#if DEBUG
        ValidateRow(row);
#endif
        return _entities[row];
    }

    /// <summary>
    /// Grows storage to guarantee capacity for at least <paramref name="requiredCapacity"/> rows.
    /// Only grows up to <see cref="Capacity"/> (max logical capacity).
    /// </summary>
    internal void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= _entities.Length) return;

        var newCapacity = Math.Min(Math.Max(requiredCapacity, _entities.Length * 2), _maxCapacity);
        if (newCapacity <= _entities.Length) return;

        var newEntities = new Entity[newCapacity];
        Array.Copy(_entities, newEntities, Count);

        var (newData, newOffsets, _) = CreateStorage(_signature, _componentTypes, newCapacity);

        for (var col = 0; col < _elementSizes.Length; col++)
        {
            var elemSize = _elementSizes[col];
            var columnBytes = Count * elemSize;
            if (columnBytes <= 0) continue;

            ref var srcRef = ref _data[_columnByteOffsets[col]];
            ref var dstRef = ref newData[newOffsets[col]];
            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
        }

        _entities = newEntities;
        _data = newData;
        _columnByteOffsets = newOffsets;
    }

    /// <summary>
    /// Adds an entity.
    /// </summary>
    internal int Add(Entity entity)
    {
        EnsureCapacity(Count + 1);
        var row = Count;
        _entities[row] = entity;
        Count++;
        return row;
    }

    internal int ReserveRows(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (Count + count > _maxCapacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

        EnsureCapacity(Count + count);
        var row = Count;
        Count += count;
        return row;
    }

    internal Span<Entity> GetReservedEntities(int startRow, int count)
    {
        if (startRow < 0 || count < 0 || startRow + count > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startRow));
        }

        return _entities.AsSpan(startRow, count);
    }

    /// <summary>
    /// Gets a component value by type.
    /// </summary>
    public T GetComponent<T>(ComponentType component, int row)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        return GetComponentAt<T>(columnIndex, row);
    }

    /// <summary>
    /// Gets a typed component span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpan<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentSpanAt<T>(columnIndex);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return ref GetComponentRefAt<T>(columnIndex, 0);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex)
    {
#if DEBUG
        ValidateElementSize<T>(columnIndex);
#endif
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), _columnByteOffsets[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
    {
#if DEBUG
        ValidateElementSize<T>(columnIndex);
#endif
        ref var target = ref GetComponentRefAt<T>(columnIndex, row);
        target = value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row)
    {
#if DEBUG
        ValidateElementSize<T>(columnIndex);
#endif
        return GetComponentRefAt<T>(columnIndex, row);
    }

    /// <summary>Gets a span of component <typeparamref name="T"/> at the given column index.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpanAt<T>(int columnIndex)
    {
#if DEBUG
        ValidateElementSize<T>(columnIndex);
#endif
        return MemoryMarshal.CreateSpan(ref GetComponentRefAt<T>(columnIndex, 0), Count);
    }

    /// <summary>Tries to get the column index for a component type. Returns false if not present in this chunk.</summary>
    public bool TryGetComponentIndex(ComponentType component, out int columnIndex)
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

    /// <summary>
    /// Resolves column indices for a batch of component types in a single call.
    /// Hoists per-row column lookups out of the row loop.
    /// </summary>
    internal bool TryGetColumnIndices(ReadOnlySpan<ComponentType> components, Span<int> outIndices)
    {
        if (components.Length != outIndices.Length)
        {
            throw new ArgumentException("Output span length must match component count.", nameof(outIndices));
        }

        for (var i = 0; i < components.Length; i++)
        {
            if (!TryGetComponentIndex(components[i], out var columnIndex))
            {
                return false;
            }

            outIndices[i] = columnIndex;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndex(ComponentType component)
    {
        if (TryGetComponentIndex(component, out var columnIndex))
        {
            return columnIndex;
        }

        throw new ArgumentException($"Chunk does not contain component {component.Value}.", nameof(component));
    }

    internal void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow)
    {
#if DEBUG
        ArgumentNullException.ThrowIfNull(source);
        source.ValidateRow(sourceRow);
        ValidateRow(destinationRow);
#endif

        if (_signature == source._signature)
        {
            CopyAllColumnsFrom(source, sourceRow, destinationRow);
            return;
        }

        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
            {
                continue;
            }

            CopyComponent(source, sourceColumnIndex, sourceRow, index, destinationRow);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyAllColumnsFrom(Chunk source, int sourceRow, int destinationRow)
    {
        var columnCount = _elementSizes.Length;
        for (var index = 0; index < columnCount; index++)
        {
            CopyComponent(source, index, sourceRow, index, destinationRow);
        }
    }

    internal unsafe void ReadComponentRaw(int columnIndex, int row, byte* destination)
    {
#if DEBUG
        ValidateRow(row);
#endif
        ref var source = ref _data[GetByteOffset(columnIndex, row)];
        Unsafe.CopyBlockUnaligned(ref *destination, ref source, (uint)_elementSizes[columnIndex]);
    }

    internal unsafe void WriteComponentRaw(int columnIndex, int row, byte* source)
    {
#if DEBUG
        ValidateRow(row);
#endif
        ref var target = ref _data[GetByteOffset(columnIndex, row)];
        Unsafe.CopyBlockUnaligned(ref target, ref *source, (uint)_elementSizes[columnIndex]);
    }

    internal void WriteColumnTo<T>(BinaryWriter writer, int columnIndex, int count)
        where T : unmanaged
    {
        ValidateColumnCount(columnIndex, count);
        writer.Write(GetColumnBytes(columnIndex, count));
    }

    internal void ReadColumnFrom<T>(BinaryReader reader, int columnIndex, int count)
        where T : unmanaged
    {
        ValidateColumnCount(columnIndex, count);
        reader.BaseStream.ReadExactly(GetColumnBytes(columnIndex, count));
    }

    internal void CopyColumnsFrom(Chunk source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_signature.Equals(source._signature))
        {
            throw new ArgumentException("Source chunk signature must match.", nameof(source));
        }

        if (count < 0 || count > Count || count > source.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var componentCount = _signature.Count;
        if (componentCount == 0)
        {
            return;
        }

        for (var columnIndex = 0; columnIndex < componentCount; columnIndex++)
        {
            CopyColumnFrom(source, columnIndex, count);
        }
    }

    /// <summary>
    /// Removes a row with swap-remove.
    /// </summary>
    internal bool RemoveAt(int row, out Entity movedEntity)
    {
#if DEBUG
        ValidateRow(row);
#endif

        var last = Count - 1;
        if (row != last)
        {
            movedEntity = _entities[last];
            _entities[row] = movedEntity;
            CopyRemovedRow(row, last);
        }
        else
        {
            movedEntity = default;
        }

        _entities[last] = default;
        Count--;
        return row != last;
    }

    private static (byte[] Data, int[] ColumnByteOffsets, int[] ElementSizes) CreateStorage(Signature signature, Type[] componentTypes, int capacity)
    {
        var componentCount = signature.Count;
        var columnByteOffsets = new int[componentCount];
        var elementSizes = new int[componentCount];

        if (componentCount == 0)
        {
            return (Array.Empty<byte>(), columnByteOffsets, elementSizes);
        }

        if (componentTypes.Length != componentCount)
        {
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));
        }

        var totalBytes = 0;
        for (var index = 0; index < componentCount; index++)
        {
            ThrowIfManagedComponent(componentTypes[index]);
            var elementSize = ComponentSizeCache.GetSize(componentTypes[index]);
            totalBytes = AlignUp(totalBytes, Math.Min(elementSize, 8));
            columnByteOffsets[index] = totalBytes;
            elementSizes[index] = elementSize;
            totalBytes += elementSize * capacity;
        }

        return (new byte[totalBytes], columnByteOffsets, elementSizes);
    }

    private static void ThrowIfManagedComponent(Type type)
    {
        if (!GenericMethodCache.GetOrInvoke(ManagedReferenceCache, type, ContainsManagedMethod))
        {
            return;
        }

        throw new NotSupportedException($"Component {type.FullName ?? type.Name} contains managed references and cannot be stored in flat byte chunks.");
    }

    private static bool ContainsManagedReferences<T>()
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
    {
        ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
        var offsets = _columnByteOffsets;
        var sizes = _elementSizes;
        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            ref var sourceRef = ref Unsafe.Add(ref dataRef, offsets[index] + last * size);
            ref var destRef = ref Unsafe.Add(ref dataRef, offsets[index] + row * size);
            CopySmall(ref destRef, ref sourceRef, size);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetColumnBytes(int columnIndex, int count)
    {
        return _data.AsSpan(_columnByteOffsets[columnIndex], count * _elementSizes[columnIndex]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetByteOffset(int columnIndex, int row)
    {
        return _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex];
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRefAt<T>(int columnIndex, int row)
    {
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex]));
    }

    [SkipLocalsInit]
    private unsafe void CopyComponent(Chunk source, int sourceColumnIndex, int sourceRow, int destinationColumnIndex, int destinationRow)
    {
        var size = _elementSizes[destinationColumnIndex];
#if DEBUG
        if (source._elementSizes[sourceColumnIndex] != size)
        {
            throw new InvalidOperationException("Component size mismatch.");
        }
#endif

        ref var sourceData = ref MemoryMarshal.GetArrayDataReference(source._data);
        ref var destinationData = ref MemoryMarshal.GetArrayDataReference(_data);
        ref var sourceRef = ref Unsafe.Add(ref sourceData, source._columnByteOffsets[sourceColumnIndex] + sourceRow * size);
        ref var destinationRef = ref Unsafe.Add(ref destinationData, _columnByteOffsets[destinationColumnIndex] + destinationRow * size);
        CopySmall(ref destinationRef, ref sourceRef, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopySmall(ref byte destination, ref byte source, int size)
    {
        switch (size)
        {
            case 1:
                destination = source;
                return;
            case 4:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<int>(ref source));
                return;
            case 8:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                return;
            case 12:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, 8)));
                return;
            case 16:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref source, 8)));
                return;
            default:
                Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)size);
                return;
        }
    }

    private unsafe void CopyColumnFrom(Chunk source, int columnIndex, int count)
    {
        var byteCount = checked((uint)(count * _elementSizes[columnIndex]));
        ref var sourceRef = ref source._data[source._columnByteOffsets[columnIndex]];
        ref var destinationRef = ref _data[_columnByteOffsets[columnIndex]];
        Unsafe.CopyBlockUnaligned(ref destinationRef, ref sourceRef, byteCount);
    }

    private void ValidateColumnCount(int columnIndex, int count)
    {
        if ((uint)columnIndex >= (uint)_elementSizes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(columnIndex));
        }

        if (count < 0 || count > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
    }

#if DEBUG
    private void ValidateElementSize<T>(int columnIndex)
    {
        if (_componentTypes[columnIndex] != typeof(T) || _elementSizes[columnIndex] != Unsafe.SizeOf<T>())
        {
            throw new InvalidCastException("Component type does not match column element type.");
        }
    }
#endif

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private void ValidateRow(int row)
    {
        if (row < 0 || row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }
}
