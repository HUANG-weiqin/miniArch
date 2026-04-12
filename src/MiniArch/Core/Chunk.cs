using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace MiniArch.Core;

public sealed class Chunk
{
    private static readonly ConcurrentDictionary<Type, bool> ColumnClearRequirementCache = new();

    private readonly Signature _signature;
    private readonly Entity[] _entities;
    private readonly Array[] _columns;
    private readonly bool[] _columnRequiresClear;
    private readonly ColumnClearMode _columnClearMode;
    private readonly int[] _componentIdToColumnIndex;
    private readonly bool _typedColumns;

    public Chunk(Signature signature, int capacity = 4)
        : this(signature, null, BuildComponentIdToColumnIndex(signature), capacity, false)
    {
    }

    internal Chunk(Signature signature, int[] componentIdToColumnIndex, int capacity = 4)
        : this(signature, null, componentIdToColumnIndex, capacity, false)
    {
    }

    internal Chunk(Signature signature, Type[] componentTypes, int[] componentIdToColumnIndex, int capacity = 4)
        : this(signature, componentTypes, componentIdToColumnIndex, capacity, true)
    {
    }

    private Chunk(Signature signature, Type[]? componentTypes, int[] componentIdToColumnIndex, int capacity, bool hasTypedColumns)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(componentIdToColumnIndex);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _signature = signature;
        _typedColumns = hasTypedColumns;
        _componentIdToColumnIndex = componentIdToColumnIndex;
        _entities = new Entity[capacity];
        _columns = CreateColumns(signature, componentTypes, capacity, hasTypedColumns);
        _columnRequiresClear = CreateColumnClearMap(_columns, componentTypes, hasTypedColumns);
        _columnClearMode = GetColumnClearMode(_columnRequiresClear);
    }

    public Signature Signature => _signature;

    public int Capacity => _entities.Length;

    public int Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        return _entities.AsSpan(0, Count);
    }

    internal Array[] Columns => _columns;

    internal Entity[] GetEntityStorage() => _entities;

    public Entity GetEntity(int row)
    {
        ValidateRow(row);
        return _entities[row];
    }

    public int Add(Entity entity, IReadOnlyDictionary<ComponentType, object?> components)
    {
        var row = Add(entity);
        var signature = _signature.AsSpan();
        for (var index = 0; index < signature.Length; index++)
        {
            var component = signature[index];
            if (!components.TryGetValue(component, out var value))
            {
                throw new InvalidOperationException($"Missing component {component.Value}.");
            }

            var columnIndex = GetComponentIndex(component);
            _columns[columnIndex].SetValue(value, row);
        }

        return row;
    }

    public int Add(Entity entity)
    {
        if (Count == Capacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

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

        if (Count + count > Capacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

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

    public object? GetComponent(ComponentType component, int row)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        return _columns[columnIndex].GetValue(row);
    }

    public T GetComponent<T>(ComponentType component, int row)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        return GetComponentAt<T>(columnIndex, row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetComponentSpan<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentSpanAt<T>(columnIndex);
    }

    public void SetComponent(ComponentType component, int row, object? value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        _columns[columnIndex].SetValue(value, row);
    }

    public void SetComponent<T>(ComponentType component, int row, in T value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        SetComponentAt(columnIndex, row, in value);
    }

    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
    {
        ((T[])_columns[columnIndex])[row] = value;
    }

    internal void SetComponentAt<T>(int columnIndex, int row, in T value)
    {
        if (!_typedColumns)
        {
            _columns[columnIndex].SetValue(value, row);
            return;
        }

        SetComponentAtTyped(columnIndex, row, in value);
    }

    internal T GetComponentAt<T>(int columnIndex, int row)
    {
        if (!_typedColumns)
        {
            return (T)_columns[columnIndex].GetValue(row)!;
        }

        return ((T[])_columns[columnIndex])[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex)
    {
        if (!_typedColumns)
        {
            throw new InvalidOperationException("Component spans require typed columns.");
        }

        if (_columns[columnIndex] is not T[] typedColumn)
        {
            throw new InvalidOperationException($"Component column {columnIndex} cannot be read as {typeof(T).Name}.");
        }

        return typedColumn.AsSpan(0, Count);
    }

    internal T[] GetTypedColumnStorageAt<T>(int columnIndex)
    {
        if (!_typedColumns)
        {
            throw new InvalidOperationException("Typed column storage requires typed columns.");
        }

        if (_columns[columnIndex] is not T[] typedColumn)
        {
            throw new InvalidOperationException($"Component column {columnIndex} cannot be read as {typeof(T).Name}.");
        }

        return typedColumn;
    }

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
        {
            return columnIndex;
        }

        throw new ArgumentException($"Chunk does not contain component {component.Value}.", nameof(component));
    }

    internal void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.ValidateRow(sourceRow);
        ValidateRow(destinationRow);

        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
            {
                continue;
            }

            Array.Copy(source._columns[sourceColumnIndex], sourceRow, _columns[index], destinationRow, 1);
        }
    }

    public IReadOnlyDictionary<ComponentType, object?> CaptureRow(int row)
    {
        ValidateRow(row);

        var values = new Dictionary<ComponentType, object?>(_signature.Count);
        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            values[components[index]] = _columns[index].GetValue(row);
        }

        return values;
    }

    public bool RemoveAt(int row, out Entity movedEntity)
    {
        ValidateRow(row);

        var last = Count - 1;
        if (row != last)
        {
            movedEntity = _entities[last];
            _entities[row] = movedEntity;
            CopyRemovedRow(row, last);
        }
        else
        {
            movedEntity = new Entity(-1, -1);
            ClearRemovedTail(last);
        }

        _entities[last] = default;
        Count--;
        return row != last;
    }

    private static Array[] CreateColumns(Signature signature, Type[]? componentTypes, int capacity, bool typedColumns)
    {
        var componentCount = signature.Count;
        var columns = new Array[componentCount];

        if (componentCount == 0)
        {
            return columns;
        }

        if (!typedColumns)
        {
            for (var index = 0; index < componentCount; index++)
            {
                columns[index] = new object?[capacity];
            }

            return columns;
        }

        ArgumentNullException.ThrowIfNull(componentTypes);
        if (componentTypes.Length != componentCount)
        {
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));
        }

        for (var index = 0; index < componentCount; index++)
        {
            columns[index] = Array.CreateInstance(componentTypes[index], capacity);
        }

        return columns;
    }

    private static bool[] CreateColumnClearMap(Array[] columns, Type[]? componentTypes, bool typedColumns)
    {
        var clearMap = new bool[columns.Length];
        if (columns.Length == 0)
        {
            return clearMap;
        }

        if (!typedColumns)
        {
            Array.Fill(clearMap, true);
            return clearMap;
        }

        ArgumentNullException.ThrowIfNull(componentTypes);
        for (var index = 0; index < componentTypes.Length; index++)
        {
            clearMap[index] = RequiresClear(componentTypes[index]);
        }

        return clearMap;
    }

    private static bool RequiresClear(Type type)
    {
        return ColumnClearRequirementCache.GetOrAdd(type, static componentType =>
        {
            return (bool)typeof(Chunk)
                .GetMethod(nameof(RequiresClearGeneric), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(componentType)
                .Invoke(null, null)!;
        });
    }

    private static bool RequiresClearGeneric<T>()
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    }

    private static ColumnClearMode GetColumnClearMode(bool[] clearMap)
    {
        if (clearMap.Length == 0)
        {
            return ColumnClearMode.None;
        }

        var requiresClear = 0;
        for (var index = 0; index < clearMap.Length; index++)
        {
            if (clearMap[index])
            {
                requiresClear++;
            }
        }

        if (requiresClear == 0)
        {
            return ColumnClearMode.None;
        }

        if (requiresClear == clearMap.Length)
        {
            return ColumnClearMode.All;
        }

        return ColumnClearMode.Mixed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
    {
        switch (_columnClearMode)
        {
            case ColumnClearMode.None:
                CopyColumnsWithoutClearing(row, last);
                break;
            case ColumnClearMode.All:
                CopyColumnsAndClearAll(row, last);
                break;
            default:
                CopyColumnsAndClearMixed(row, last);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRemovedTail(int last)
    {
        switch (_columnClearMode)
        {
            case ColumnClearMode.All:
                ClearAllColumns(last);
                break;
            case ColumnClearMode.Mixed:
                ClearMixedColumns(last);
                break;
        }
    }

    private void CopyColumnsWithoutClearing(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
        }
    }

    private void CopyColumnsAndClearAll(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
            Array.Clear(column, last, 1);
        }
    }

    private void CopyColumnsAndClearMixed(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
            if (_columnRequiresClear[index])
            {
                Array.Clear(column, last, 1);
            }
        }
    }

    private void ClearAllColumns(int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            Array.Clear(_columns[index], last, 1);
        }
    }

    private void ClearMixedColumns(int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            if (_columnRequiresClear[index])
            {
                Array.Clear(_columns[index], last, 1);
            }
        }
    }

    private static int[] BuildComponentIdToColumnIndex(Signature signature)
    {
        var maxComponentId = -1;
        var components = signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var componentId = components[index].Value;
            if (componentId > maxComponentId)
            {
                maxComponentId = componentId;
            }
        }

        if (maxComponentId < 0)
        {
            return Array.Empty<int>();
        }

        var lookup = new int[maxComponentId + 1];
        Array.Fill(lookup, -1);

        for (var index = 0; index < components.Length; index++)
        {
            lookup[components[index].Value] = index;
        }

        return lookup;
    }

    private void ValidateRow(int row)
    {
        if (row < 0 || row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    private enum ColumnClearMode
    {
        None,
        All,
        Mixed,
    }
}
