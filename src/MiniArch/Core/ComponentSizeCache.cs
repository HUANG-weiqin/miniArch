using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

internal static class ComponentSizeCache
{
    private static readonly ConcurrentDictionary<Type, int> Cache = new();

    private static readonly MethodInfo SizeOfMethod = typeof(ComponentSizeCache)
        .GetMethod(nameof(SizeOfGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;

    public static int GetSize(Type componentType) => GenericMethodCache.GetOrInvoke(Cache, componentType, SizeOfMethod);

    private static int SizeOfGeneric<T>() => Unsafe.SizeOf<T>();
}
