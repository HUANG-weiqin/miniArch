namespace MiniArch.Core;

public sealed class Chunk
{
    private readonly Signature _signature;
    private readonly Entity[] _entities;
    private readonly Dictionary<ComponentType, object?[]> _columns;

    public Chunk(Signature signature, int capacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _signature = signature;
        _entities = new Entity[capacity];
        _columns = new Dictionary<ComponentType, object?[]>();
        foreach (var component in signature)
        {
            _columns[component] = new object?[capacity];
        }
    }

    public Signature Signature => _signature;

    public int Capacity => _entities.Length;

    public int Count { get; private set; }

    public Entity GetEntity(int row)
    {
        ValidateRow(row);
        return _entities[row];
    }

    public int Add(Entity entity, IReadOnlyDictionary<ComponentType, object?> components)
    {
        if (Count == Capacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

        var row = Count;
        _entities[row] = entity;
        foreach (var component in _signature)
        {
            if (!components.TryGetValue(component, out var value))
            {
                throw new InvalidOperationException($"Missing component {component.Value}.");
            }

            _columns[component][row] = value;
        }

        Count++;
        return row;
    }

    public object? GetComponent(ComponentType component, int row)
    {
        ValidateRow(row);
        return _columns[component][row];
    }

    public T GetComponent<T>(ComponentType component, int row)
    {
        return (T)GetComponent(component, row)!;
    }

    public void SetComponent(ComponentType component, int row, object? value)
    {
        ValidateRow(row);
        if (!_columns.ContainsKey(component))
        {
            throw new ArgumentException($"Chunk does not contain component {component.Value}.", nameof(component));
        }

        _columns[component][row] = value;
    }

    public IReadOnlyDictionary<ComponentType, object?> CaptureRow(int row)
    {
        ValidateRow(row);
        var values = new Dictionary<ComponentType, object?>(_signature.Count);
        foreach (var component in _signature)
        {
            values[component] = _columns[component][row];
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

            foreach (var component in _signature)
            {
                _columns[component][row] = _columns[component][last];
                _columns[component][last] = null;
            }
        }
        else
        {
            movedEntity = default;
            foreach (var component in _signature)
            {
                _columns[component][last] = null;
            }
        }

        _entities[last] = default;
        Count--;
        return row != last;
    }

    private void ValidateRow(int row)
    {
        if (row < 0 || row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }
}
