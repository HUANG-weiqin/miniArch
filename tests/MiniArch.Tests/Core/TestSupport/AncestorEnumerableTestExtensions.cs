using MiniArch;

namespace MiniArch.Tests.Core.TestSupport;

/// <summary>
/// Test-only convenience to materialize an <see cref="AncestorEnumerable"/>
/// into a <see cref="List{T}"/> for sorting/indexing assertions.
/// Production code should use <c>foreach</c> directly.
/// </summary>
internal static class AncestorEnumerableTestExtensions
{
    internal static List<Entity> ToAncestorList(this AncestorEnumerable source)
    {
        var list = new List<Entity>();
        foreach (var ancestor in source)
        {
            list.Add(ancestor);
        }

        return list;
    }
}
