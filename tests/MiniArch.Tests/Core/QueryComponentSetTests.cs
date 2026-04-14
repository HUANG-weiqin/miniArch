using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class QueryComponentSetTests
{
    [Fact]
    public void Create_from_array_sorts_components_without_extra_allocations()
    {
        _ = QueryComponentSet.CreateFrom(new[]
        {
            new ComponentType(3),
            new ComponentType(1),
            new ComponentType(2),
        });

        var components = new[]
        {
            new ComponentType(7),
            new ComponentType(2),
            new ComponentType(5),
            new ComponentType(1),
            new ComponentType(9),
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var set = QueryComponentSet.CreateFrom(components);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(new[]
        {
            new ComponentType(1),
            new ComponentType(2),
            new ComponentType(5),
            new ComponentType(7),
            new ComponentType(9),
        }, set.AsSpan().ToArray());
    }
}
