using MiniArch.Core;

#pragma warning disable IDE0051, IDE0052, CS0169 // Fields used by later tasks

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
    /// Enumerates chunks whose component <typeparamref name="T"/> was written since the last call.
    /// </summary>
    public IEnumerable<ChunkView> ModifiedChunks() => throw new NotImplementedException();

    /// <summary>
    /// Enumerates entities that entered or exited the set of entities having component
    /// <typeparamref name="T"/> since the last call.
    /// </summary>
    public IEnumerable<Transition> Transitions() => throw new NotImplementedException();
}

#pragma warning restore IDE0051, IDE0052, CS0169
