using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Caches typed byte-to-column writers used by raw command replay.
/// </summary>
public static unsafe class ComponentWriterCache
{
    private static readonly ConcurrentDictionary<Type, ColumnWriterDelegate> ColumnWriters = new();
    private static readonly ConcurrentDictionary<Type, int> Sizes = new();

    /// <summary>
    /// Writes a component value from raw bytes into flat chunk storage.
    /// </summary>
    public delegate void ColumnWriterDelegate(Chunk chunk, int columnIndex, int row, byte* source);

    internal static ColumnWriterDelegate GetColumnWriter(Type type)
    {
        return ColumnWriters.GetOrAdd(type, static t =>
        {
            var method = typeof(ComponentWriterCache)
                .GetMethod(nameof(CreateColumnWriter), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(t);
            return (ColumnWriterDelegate)method.Invoke(null, null)!;
        });
    }

    internal static int GetSize(Type type)
    {
        return Sizes.GetOrAdd(type, static t =>
        {
            var method = typeof(ComponentWriterCache)
                .GetMethod(nameof(GetSizeGeneric), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(t);
            return (int)method.Invoke(null, null)!;
        });
    }

    private static ColumnWriterDelegate CreateColumnWriter<T>()
    {
        return (Chunk chunk, int columnIndex, int row, byte* source) =>
        {
            var value = Unsafe.Read<T>(source);
            chunk.SetComponentAtTyped(columnIndex, row, in value);
        };
    }

    private static int GetSizeGeneric<T>()
    {
        return Unsafe.SizeOf<T>();
    }
}
