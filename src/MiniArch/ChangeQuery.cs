using System.Collections.Generic;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Stateful cursor over changes to component <typeparamref name="T"/>. Hold one instance per
/// consuming system; each enumeration call auto-advances its cursor. Obtain via <see cref="World.Track{T}"/>.
/// </summary>
/// <remarks>
/// After a snapshot save/load, observer state resets. Call <see cref="World.Track{T}"/> again
/// to obtain a fresh cursor; discard cursors from before the restore.
/// </remarks>
public sealed class ChangeQuery<T> where T : unmanaged
{
    private readonly World _world;
    private readonly ComponentType _type;
    private long _valueCursor;
    private int _transitionCursor;

    internal ChangeQuery(World world)
    {
        _world = world;
        _type = Component<T>.ComponentType;
    }

    /// <summary>
    /// Returns chunks whose component <typeparamref name="T"/> was written since the last call.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
    public IEnumerable<ChunkView> ModifiedChunks()
    {
        var query = _world.Query(new QueryDescription().With<T>());
        var snapshotEpoch = _world.CurrentWriteEpoch;
        var result = new List<ChunkView>();
        var chunks = query.GetChunks().ToArray();
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var chunk = chunks[ci];
            var arch = chunk.Archetype;
            if (!arch.TryGetComponentIndex(_type, out var col))
                continue;

            var versions = arch._columnVersions;
            if (versions is not null && versions[col] > _valueCursor)
                result.Add(chunk);
        }

        _valueCursor = snapshotEpoch;
        return result;
    }

    /// <summary>
    /// Returns entities that entered or exited the set of entities having component
    /// <typeparamref name="T"/> since the last call.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
    public IEnumerable<Transition> Transitions()
    {
        var log = _world.GetTransitionLogInternal();
        var end = log.Count;
        var result = new List<Transition>();
        for (int i = _transitionCursor; i < end; i++)
        {
            var entry = log[i];
            var oldHas = entry.OldArchetype is { } o && o.ContainsComponent(_type);
            var newHas = entry.NewArchetype is { } n && n.ContainsComponent(_type);
            if (!oldHas && newHas)
                result.Add(new Transition(TransitionKind.Entered, entry.Entity));
            else if (oldHas && !newHas)
                result.Add(new Transition(TransitionKind.Exited, entry.Entity));
            // both true or both false: membership in {T} unchanged -> skip
        }

        _transitionCursor = end;
        return result;
    }
}
