using MiniArch;

namespace MiniArch.Tests.Core.TestSupport;

/// <summary>
/// Test-only convenience to materialize a <see cref="ChildrenEnumerable"/>
/// into a <see cref="List{T}"/> for sorting/indexing assertions.
/// Production code should use <c>foreach</c> directly.
/// </summary>
internal static class ChildrenEnumerableTestExtensions
{
    internal static List<Entity> ToChildList(this ChildrenEnumerable source)
    {
        var list = new List<Entity>();
        foreach (var child in source)
        {
            list.Add(child);
        }

        return list;
    }
}
