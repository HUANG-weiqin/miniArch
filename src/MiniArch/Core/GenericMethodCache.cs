using System.Collections.Concurrent;
using System.Reflection;

namespace MiniArch.Core;

internal static class GenericMethodCache
{
    public static TValue GetOrInvoke<TValue>(
        ConcurrentDictionary<Type, TValue> cache,
        Type typeArg,
        MethodInfo openMethod)
    {
        return cache.GetOrAdd(typeArg, static (t, method) =>
        {
            return (TValue)method.MakeGenericMethod(t).Invoke(null, null)!;
        }, openMethod);
    }
}
