using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public static unsafe class ComponentWriterCache
{
    private static readonly ConcurrentDictionary<Type, ColumnWriterDelegate> ColumnWriters = new();
    private static readonly ConcurrentDictionary<Type, ComponentReaderDelegate> Readers = new();
    private static readonly ConcurrentDictionary<Type, int> Sizes = new();

    public delegate void ColumnWriterDelegate(Array column, int row, byte* source);

    internal delegate void ComponentReaderDelegate(void* destination, byte* source);

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

    internal static ComponentReaderDelegate GetReader(Type type)
    {
        return Readers.GetOrAdd(type, static t =>
        {
            var method = typeof(ComponentWriterCache)
                .GetMethod(nameof(CreateReader), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(t);
            return (ComponentReaderDelegate)method.Invoke(null, null)!;
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
        return (Array column, int row, byte* source) =>
        {
            Unsafe.As<T[]>(column)[row] = Unsafe.Read<T>(source);
        };
    }

    private static ComponentReaderDelegate CreateReader<T>()
    {
        return (void* destination, byte* source) =>
        {
            Unsafe.Write(destination, Unsafe.Read<T>(source));
        };
    }

    private static int GetSizeGeneric<T>()
    {
        return Unsafe.SizeOf<T>();
    }
}
