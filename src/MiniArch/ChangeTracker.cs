using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Typed change tracker for a single component type. Stores old/new values
/// in entity-indexed arrays, matching what a hand-written user would do.
/// Used internally by <see cref="ChangeQuery"/> when Previous() is enabled
/// with a single Capture type.
/// </summary>
internal sealed class ChangeTracker<T> : IChangeTrackerDrain where T : unmanaged
{
    internal T[] OldValues = [];
    internal T[] NewValues = [];
    internal bool[] IsDirty = [];
    internal int[] DirtyList = [];
    internal Entity[] DirtyEntities = [];
    internal int DirtyCount;

    // Filter support
    internal QueryCache? FilterCache;
    internal bool HasFilter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureCapacity(int id)
    {
        if (id < IsDirty.Length) return;
        var newSize = Math.Max(id + 1, IsDirty.Length == 0 ? 64 : IsDirty.Length * 2);
        Array.Resize(ref IsDirty, newSize);
        Array.Resize(ref OldValues, newSize);
        Array.Resize(ref NewValues, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureDirtyListCapacity()
    {
        if (DirtyCount < DirtyList.Length) return;
        var newSize = Math.Max(DirtyCount + 1, DirtyList.Length == 0 ? 64 : DirtyList.Length * 2);
        Array.Resize(ref DirtyList, newSize);
        Array.Resize(ref DirtyEntities, newSize);
    }

    /// <summary>
    /// Called from World.Set&lt;T&gt; hot path. Captures old value on first write,
    /// always updates new value. Direct typed array access — no byte[] copies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Capture(Entity entity, ref T cell, in T newValue, Archetype archetype)
    {
        if (HasFilter && FilterCache is not null && !FilterCache.Matches(archetype))
            return;

        var id = entity.Id;
        EnsureCapacity(id);

        if (!IsDirty[id])
        {
            IsDirty[id] = true;
            EnsureDirtyListCapacity();
            DirtyList[DirtyCount] = id;
            DirtyEntities[DirtyCount] = entity;
            DirtyCount++;
            OldValues[id] = cell;  // capture first old value
        }

        NewValues[id] = newValue;  // always capture latest new value
    }

    /// <summary>
    /// Reset dirty state for next tick.
    /// </summary>
    internal void Reset()
    {
        for (var i = 0; i < DirtyCount; i++)
            IsDirty[DirtyList[i]] = false;
        DirtyCount = 0;
    }

    // ── IChangeTrackerDrain ──

    int IChangeTrackerDrain.DirtyCount => DirtyCount;
    int IChangeTrackerDrain.TypeSize => Unsafe.SizeOf<T>();

    Entity[] IChangeTrackerDrain.DirtyEntities => DirtyEntities;
    bool[] IChangeTrackerDrain.IsDirty => IsDirty;

    unsafe void IChangeTrackerDrain.CopyOldNewTo(byte[] data, int snapshotSize)
    {
        var elemSize = Unsafe.SizeOf<T>();
        for (var i = 0; i < DirtyCount; i++)
        {
            var id = DirtyList[i];
            var dstOff = i * snapshotSize * 2;
            // Copy Old
            Unsafe.CopyBlockUnaligned(ref data[dstOff],
                ref Unsafe.As<T, byte>(ref OldValues[id]), (uint)elemSize);
            // Copy New
            Unsafe.CopyBlockUnaligned(ref data[dstOff + snapshotSize],
                ref Unsafe.As<T, byte>(ref NewValues[id]), (uint)elemSize);
        }
    }

    void IChangeTrackerDrain.Reset() => Reset();
}

/// <summary>
/// Non-generic interface for typed tracker drain, used by ChangeQuery
/// to drain typed tracker data without knowing T at compile time.
/// </summary>
internal interface IChangeTrackerDrain
{
    int DirtyCount { get; }
    int TypeSize { get; }
    Entity[] DirtyEntities { get; }
    bool[] IsDirty { get; }
    unsafe void CopyOldNewTo(byte[] data, int snapshotSize);
    void Reset();
}
