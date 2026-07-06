using System.Collections.Generic;
using MiniArch.Core;

#pragma warning disable IDE0051, IDE0052, CS0169 // _transitionCursor used by Task 6

namespace MiniArch;

/// <summary>
/// Stateful cursor over changes to component <typeparamref name="T"/>. Hold one instance per
/// consuming system; each enumeration call auto-advances its cursor. Obtain via <see cref="World.Track{T}"/>.
/// </summary>
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
    public IEnumerable<Transition> Transitions() => throw new NotImplementedException();
}
