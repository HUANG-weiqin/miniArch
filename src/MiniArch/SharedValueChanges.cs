using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// A handle to the world-shared boundary diff for component type <typeparamref name="T"/>.
/// Obtained via <see cref="World.TrackValueChanges{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Multiple handles for the same <typeparamref name="T"/> alias one world-shared
/// per-component baseline. <see cref="ClearAll"/> affects all same-type handles.
/// </para>
    /// <para>
    /// A default-initialized handle (e.g. default(SharedValueChanges&lt;T&gt;)) is inert:
    /// its <see cref="Changes"/> returns empty and <see cref="ClearAll"/> is a no-op.
    /// </para>
/// </remarks>
public readonly struct SharedValueChanges<T> where T : unmanaged
{
    private readonly World? _world;

    internal SharedValueChanges(World world)
    {
        _world = world;
    }

    /// <summary>
    /// Non-destructive read-only view of the current value changes for component type <typeparamref name="T"/>.
    /// Reading scans the current world and compares it with the last baseline.
    /// </summary>
    public ReadOnlySpan<ValueChange<T>> Changes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var world = _world;
            if (world is null)
                return ReadOnlySpan<ValueChange<T>>.Empty;
            if (world.IsDisposed)
                throw new ObjectDisposedException(nameof(World));

            var tracker = world.SharedTrackers?.GetTracker<T>();
            if (tracker is null)
                return ReadOnlySpan<ValueChange<T>>.Empty;

            return tracker.Read();
        }
    }

    /// <summary>
    /// Moves the world-shared baseline for component type <typeparamref name="T"/>
    /// to current world values, affecting all handles for this type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearAll()
    {
        var world = _world;
        if (world is null)
            return;
        if (world.IsDisposed)
            throw new ObjectDisposedException(nameof(World));

        var tracker = world.SharedTrackers?.GetTracker<T>();
        tracker?.Clear();
    }
}
