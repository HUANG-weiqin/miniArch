using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// A pull-based watch that tracks value changes for component type <typeparamref name="TComponent"/>
/// by comparing the current world state against the last <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Snapshot"/> to record a baseline. Then call <see cref="Diff"/> to discover
/// entities whose component value has changed since the baseline. Multiple <see cref="Diff"/>
/// calls against the same baseline repeat the same callbacks.
/// </para>
/// <para>
/// Entities whose slot was never populated (stale slot) at <see cref="Snapshot"/> time
/// are reported with the old value as <c>default</c>. Entities removed/destroyed after
/// <see cref="Snapshot"/> are not reported because the current scan cannot find them.
/// </para>
/// </remarks>
public sealed class ChangeWatch<TComponent, THandler>
    where TComponent : unmanaged, IEquatable<TComponent>
    where THandler : struct, IChangeHandler<TComponent>
{
    private TComponent[] _oldValues = [];
    private int[] _touchedIds = [];
    private int _touchedCount;
    private readonly QueryDescription _query;
    private THandler _handler;
    private bool _hasSnapshot;

    // Reusable buffer for collecting entries before dispatching callbacks.
    // Grows as needed; cleared each Diff.
    private Entry[] _buffer = [];

    private struct Entry
    {
        public Entity Entity;
        public TComponent OldValue;
        public TComponent NewValue;
    }

    internal ChangeWatch(QueryDescription query, THandler handler)
    {
        _query = query;
        _handler = handler;
    }

    /// <summary>
    /// Gets or sets the handler. Setting replaces the handler for subsequent
    /// <see cref="Diff"/> calls.
    /// </summary>
    public ref THandler Handler => ref _handler;

    /// <summary>
    /// Records a baseline snapshot of all entities matching the watch's query.
    /// Must be called before <see cref="Diff"/>.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="Snapshot"/> again resets the baseline to the current world state.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Snapshot(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Clear previous touched slots to avoid stale values for recycled ids.
        for (var i = 0; i < _touchedCount; i++)
        {
            var id = _touchedIds[i];
            if ((uint)id < (uint)_oldValues.Length)
                _oldValues[id] = default;
        }
        _touchedCount = 0;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var values = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entityId = entities[i].Id;

                // Ensure _oldValues is large enough
                if ((uint)entityId >= (uint)_oldValues.Length)
                    Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));

                _oldValues[entityId] = values[i];

                // Record touched id
                if (_touchedCount >= _touchedIds.Length)
                    Array.Resize(ref _touchedIds, Math.Max(_touchedCount + 1, _touchedIds.Length * 2));
                _touchedIds[_touchedCount++] = entityId;
            }
        }

        _hasSnapshot = true;
    }

    /// <summary>
    /// Scans the current world and calls <see cref="IChangeHandler{TComponent}.OnChange"/>
    /// for each entity whose component value differs from the snapshot baseline.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="Snapshot"/> has not been called.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Diff(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!_hasSnapshot)
            throw new InvalidOperationException(
                "ChangeWatch.Diff requires a prior Snapshot call. Call Snapshot(World) before Diff.");

        // Phase 1: collect diffs into buffer (avoids corruption if handler mutates world).
        var bufferCount = 0;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var values = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                var entityId = entity.Id;

                var oldVal = (uint)entityId < (uint)_oldValues.Length
                    ? _oldValues[entityId]
                    : default;

                var newVal = values[i];

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

        // Phase 2: dispatch callbacks (buffer is stable even if handler mutates world).
        for (var i = 0; i < bufferCount; i++)
        {
            ref var entry = ref _buffer[i];
            _handler.OnChange(world, entry.Entity, in entry.OldValue, in entry.NewValue);
        }
    }
}
