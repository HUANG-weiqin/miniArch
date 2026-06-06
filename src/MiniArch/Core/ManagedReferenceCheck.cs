using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Cached check for whether a type contains managed references.
/// </summary>
internal static class ManagedReferenceCheck
{
    private static readonly ConcurrentDictionary<Type, bool> Cache = new();
    private static readonly MethodInfo Method = typeof(ManagedReferenceCheck)
        .GetMethod(nameof(IsManagedInternal), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static bool IsManaged(Type type) =>
        GenericMethodCache.GetOrInvoke(Cache, type, Method);

    private static bool IsManagedInternal<T>() =>
        RuntimeHelpers.IsReferenceOrContainsReferences<T>();
}
