using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Caches typed byte-to-column writers used by raw command replay.
/// </summary>
internal static unsafe class ComponentWriterCache
{
    private static readonly ConcurrentDictionary<Type, ColumnWriterDelegate> ColumnWriters = new();
    private static readonly MethodInfo CreateColumnWriterMethod = typeof(ComponentWriterCache)
        .GetMethod(nameof(CreateColumnWriter), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// Writes a component value from raw bytes into flat chunk storage.
    /// </summary>
    internal delegate void ColumnWriterDelegate(Chunk chunk, int columnIndex, int row, byte* source);

    internal static ColumnWriterDelegate GetColumnWriter(Type type)
    {
        return GenericMethodCache.GetOrInvoke(ColumnWriters, type, CreateColumnWriterMethod);
    }

    internal static int GetSize(Type type)
    {
        return ComponentSizeCache.GetSize(type);
    }

    private static ColumnWriterDelegate CreateColumnWriter<T>()
    {
        return (Chunk chunk, int columnIndex, int row, byte* source) =>
        {
            var value = Unsafe.Read<T>(source);
            chunk.SetComponentAtTyped(columnIndex, row, in value);
        };
    }
}
