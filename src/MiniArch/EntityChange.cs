using System;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Records a snapshot pair (Old + New) for one entity across all captured components
/// at the time of a write or structural transition. Produced by <see cref="ChangeQuery.Changes"/>
/// when <see cref="ChangeQuery.Previous"/> is enabled.
/// </summary>
/// <remarks>
/// Each instance holds a shared byte array with all snapshot data, and type/offset
/// metadata to project the individual component values on demand.
/// </remarks>
public readonly struct EntityChange
{
    /// <summary>The entity whose components changed.</summary>
    public readonly Entity Entity;

    internal readonly byte[] Data;
    internal readonly int OldOffset;
    internal readonly int NewOffset;
    internal readonly int SnapshotSize;
    internal readonly int[] Offsets;       // precomputed per-type byte offset into snapshot
    internal readonly ComponentType[] Types; // ordered captured types

    /// <summary>Constructs a change record.</summary>
    internal EntityChange(Entity entity, byte[] data, int oldOffset, int newOffset,
        int snapshotSize, int[] offsets, ComponentType[] types)
    {
        Entity = entity;
        Data = data;
        OldOffset = oldOffset;
        NewOffset = newOffset;
        SnapshotSize = snapshotSize;
        Offsets = offsets;
        Types = types;
    }

    /// <summary>The component values before the change.</summary>
    public EntitySnapshot Old => new(Data, OldOffset, Offsets, Types);

    /// <summary>The component values after the change.</summary>
    public EntitySnapshot New => new(Data, NewOffset, Offsets, Types);
}

/// <summary>
/// Projected view of one snapshot (Old or New) in a <see cref="EntityChange"/>. 
/// Provides typed access to captured component values via <see cref="Get{T}"/>.
/// </summary>
/// <remarks>
/// This is a <c>ref struct</c> — it must not escape the stack. It is obtained
/// from <see cref="EntityChange.Old"/> or <see cref="EntityChange.New"/> and
/// must be consumed within the scope of the <see cref="EntityChange"/> array.
/// </remarks>
public readonly ref struct EntitySnapshot
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly int[] _offsets;
    private readonly ComponentType[] _types;

    internal EntitySnapshot(byte[] data, int offset, int[] offsets, ComponentType[] types)
    {
        _data = data;
        _offset = offset;
        _offsets = offsets;
        _types = types;
    }

    /// <summary>Returns true if a component of type <typeparamref name="T"/> was captured.</summary>
    public bool Has<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        for (var i = 0; i < _types.Length; i++)
            if (_types[i].Value == typeId) return true;
        return false;
    }

    /// <summary>Reads the captured value of component <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <typeparamref name="T"/> was not captured in this change.
    /// </exception>
    public ref readonly T Get<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        for (var i = 0; i < _types.Length; i++)
        {
            if (_types[i].Value == typeId)
            {
                var off = _offset + _offsets[i];
                return ref Unsafe.As<byte, T>(ref _data[off]);
            }
        }
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} was not captured in this change.");
    }
}
