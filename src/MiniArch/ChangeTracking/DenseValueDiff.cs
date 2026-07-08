using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Explicit dense shadow-diff tracker for <typeparamref name="TComponent"/>
/// projected through <typeparamref name="TProjector"/>.
///
/// <para>
/// Call <see cref="Capture"/> to record a baseline of all matching entities'
/// projected values. Then call <see cref="Drain"/> to discover entities whose
/// projected value has changed since the last <see cref="Capture"/>.
/// </para>
/// </summary>
public sealed class DenseValueDiff<TComponent, TValue, TProjector>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where TProjector : struct, IValueProjector<TComponent, TValue>
{
    private TValue[] _oldValues = [];
    private int[] _touchedEntities = [];
    private int _touchedCount;
    private readonly QueryDescription _query;
    private readonly TProjector _projector;
    private bool _hasCaptured;

    internal DenseValueDiff(QueryDescription query, TProjector projector)
    {
        _query = query;
        _projector = projector;
    }

    /// <summary>
    /// Records a baseline of matching entities' projected values.
    /// Must be called at least once before <see cref="Drain"/>.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="Capture"/> again without an intervening <see cref="Clear"/>
    /// is allowed, but it first clears slots touched by the previous capture.
    /// For hot loops, prefer the explicit <c>Capture → Drain → Clear</c> cadence.
    /// <para/>
    /// <paramref name="world"/> must remain alive for the lifetime of this diff;
    /// no version check is performed — destroyed/reused entity IDs may report
    /// stale slot diffs (the old slot value from a previous occupant is
    /// reported against the new entity when the ID is recycled).
    /// Use <see cref="World.TrackTransitions"/> for structure membership semantics.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Capture(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_hasCaptured)
        {
            for (int i = 0; i < _touchedCount; i++)
                _oldValues[_touchedEntities[i]] = default;
        }

        _touchedCount = 0;
        _hasCaptured = true;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var span = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;

                // Resize _oldValues if needed
                if ((uint)entityId >= (uint)_oldValues.Length)
                    Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));

                _oldValues[entityId] = _projector.Project(span[i]);

                // Track touched entities
                if (_touchedCount >= _touchedEntities.Length)
                    Array.Resize(ref _touchedEntities, Math.Max(_touchedCount + 1, _touchedEntities.Length * 2));
                _touchedEntities[_touchedCount++] = entityId;
            }
        }
    }

    /// <summary>
    /// Scans matching entities and calls <paramref name="sink"/> for each
    /// whose projected value differs from the last <see cref="Capture"/> baseline.
    ///
    /// <para>
    /// If <see cref="Capture"/> has never been called, Drain yields nothing.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <paramref name="world"/> must remain alive; no version check — see
    /// <see cref="Capture"/> for stale slot semantics.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drain<TDrain>(World world, ref TDrain sink)
        where TDrain : struct, IValueChangeSink<TValue>
    {
        ArgumentNullException.ThrowIfNull(world);
        // If never captured, drain yields nothing (no baseline)
        if (!_hasCaptured) return;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var span = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;
                TValue oldVal = (uint)entityId < (uint)_oldValues.Length
                    ? _oldValues[entityId]
                    : default;
                TValue newVal = _projector.Project(span[i]);

                if (!oldVal.Equals(newVal))
                {
                    sink.OnChanged(entities[i], oldVal, newVal);
                }
            }
        }
    }

    /// <summary>
    /// Resets the baseline so that the next <see cref="Capture"/> establishes
    /// a fresh one. After <see cref="Clear"/>, the next <see cref="Drain"/>
    /// without an intervening <see cref="Capture"/> yields nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (!_hasCaptured) return;
        for (int i = 0; i < _touchedCount; i++)
            _oldValues[_touchedEntities[i]] = default;
        _touchedCount = 0;
        _hasCaptured = false;
    }
}
