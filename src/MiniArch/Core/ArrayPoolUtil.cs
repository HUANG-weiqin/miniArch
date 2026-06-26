using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

internal static class ArrayPoolUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushPooled<T>(ref T[] array, ref int count, T value)
    {
        if ((uint)count >= (uint)array.Length)
            GrowPooled(ref array);
        array[count++] = value;
    }

    public static void GrowPooled<T>(ref T[] array)
    {
        var next = ArrayPool<T>.Shared.Rent(array.Length == 0 ? 16 : array.Length * 2);
        if (array.Length > 0)
        {
            Array.Copy(array, next, array.Length);
            ArrayPool<T>.Shared.Return(array);
        }
        array = next;
    }
}
