using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// A pull-based watch that tracks projected value changes for component type
/// <typeparamref name="TComponent"/>, projecting to <typeparamref name="TValue"/>
/// for comparison via <typeparamref name="THandler"/>.
/// </summary>
/// <remarks>
/// Call <see cref="Snapshot"/> to record a baseline. Then call <see cref="Diff"/> to discover
/// entities whose projected value has changed since the baseline.
/// </remarks>
public sealed class ChangeWatch<TComponent, TValue, THandler>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where THandler : struct, IChangeHandler<TComponent, TValue>
{
    private TValue[] _oldValues = [];
    private int[] _touchedIds = [];
    private int _touchedCount;
    private readonly QueryDescription _query;
    private THandler _handler;
    private bool _hasSnapshot;
    private bool _operationInProgress;

    private struct Entry
    {
        public Entity Entity;
        public TValue OldValue;
        public TValue NewValue;
    }

    private Entry[] _buffer = [];

    internal ChangeWatch(QueryDescription query, THandler handler)
    {
        _query = query;
        _handler = handler;
    }

    /// <summary>
    /// Gets or sets the handler.
    /// </summary>
    public ref THandler Handler => ref _handler;

    /// <summary>
    /// Records a baseline snapshot of projected values for all matching entities.
    /// </summary>
    /// <remarks>
    /// If snapshot collection fails, the partial baseline is discarded and a successful
    /// Snapshot is required before the next <see cref="Diff"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A Snapshot or Diff call is already in progress on this watch.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Snapshot(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        BeginOperation();
        _hasSnapshot = false;

        try
        {
            // Clear previous touched slots
            for (var i = 0; i < _touchedCount; i++)
            {
                var id = _touchedIds[i];
                if ((uint)id < (uint)_oldValues.Length)
                    _oldValues[id] = default;
            }
            _touchedCount = 0;

            foreach (var chunk in world.Query(in _query).GetChunks())
            {
                var components = chunk.GetSpan<TComponent>();
                var entities = chunk.GetEntities();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var entityId = entities[i].Id;

                    if ((uint)entityId >= (uint)_oldValues.Length)
                        Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));

                    _oldValues[entityId] = _handler.Project(components[i]);

                    if (_touchedCount >= _touchedIds.Length)
                        Array.Resize(ref _touchedIds, Math.Max(_touchedCount + 1, _touchedIds.Length * 2));
                    _touchedIds[_touchedCount++] = entityId;
                }
            }

            _hasSnapshot = true;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    /// <summary>
    /// Scans the current world and calls <see cref="IChangeHandler{TComponent, TValue}.OnChange"/>
    /// for each entity whose projected value differs from the snapshot baseline.
    /// </summary>
    /// <exception cref="InvalidOperationException">No snapshot exists, or a Snapshot or Diff call is already in progress on this watch.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Diff(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        BeginOperation();

        try
        {
            if (!_hasSnapshot)
                throw new InvalidOperationException(
                    "ChangeWatch.Diff requires a prior Snapshot call. Call Snapshot(World) before Diff.");

            var bufferCount = 0;

            foreach (var chunk in world.Query(in _query).GetChunks())
            {
                var components = chunk.GetSpan<TComponent>();
                var entities = chunk.GetEntities();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var entity = entities[i];
                    var entityId = entity.Id;

                    var oldVal = (uint)entityId < (uint)_oldValues.Length
                        ? _oldValues[entityId]
                        : default;

                    var newVal = _handler.Project(components[i]);

                    if (oldVal.Equals(newVal))
                        continue;

                    if (bufferCount >= _buffer.Length)
                        Array.Resize(ref _buffer, Math.Max(bufferCount + 1, _buffer.Length * 2));

                    _buffer[bufferCount] = new Entry
                    {
                        Entity = entity,
                        OldValue = oldVal,
                        NewValue = newVal
                    };
                    bufferCount++;
                }
            }

            for (var i = 0; i < bufferCount; i++)
            {
                ref var entry = ref _buffer[i];
                _handler.OnChange(world, entry.Entity, entry.OldValue, entry.NewValue);
            }
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginOperation()
    {
        if (_operationInProgress)
            throw new InvalidOperationException(
                "ChangeWatch does not support nested Snapshot or Diff calls on the same watch.");

        _operationInProgress = true;
    }
}
