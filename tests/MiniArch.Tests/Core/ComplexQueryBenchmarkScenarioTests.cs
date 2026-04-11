using MiniArch.Benchmarks;
using MiniArch.Core;
using System.Reflection;

namespace MiniArch.Tests.Core;

public sealed class ComplexQueryBenchmarkScenarioTests
{
    [Fact]
    public void Complex_query_benchmark_world_uses_multiple_dense_archetypes()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);
        var archetypeComponentCounts = new HashSet<int>();
        var signatures = new HashSet<string>();

        foreach (var entity in state.Entities)
        {
            Assert.True(state.World.TryGetLocation(entity, out var info));
            archetypeComponentCounts.Add(info.Archetype.Signature.Count);
            signatures.Add(string.Join(",", info.Archetype.Signature.AsSpan().ToArray().Select(component => component.Value)));
        }

        Assert.Equal(5, signatures.Count);
        Assert.All(archetypeComponentCounts, count => Assert.True(count >= 8));
    }

    [Fact]
    public void Complex_query_benchmark_queries_return_expected_populations()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);

        var withAll = state.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Build();

        var withWithout = state.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>()
            .Build();

        var withAny = state.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Any<AnyTagA>()
            .Or<AnyTagB>()
            .Build();

        Assert.Equal(90, CountEntities(withAll));
        Assert.Equal(70, CountEntities(withWithout));
        Assert.Equal(55, CountEntities(withAny));
    }

    [Fact]
    public void Complex_query_benchmark_matching_entities_have_at_least_eight_components()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);

        var query = state.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Build();

        var inspectedChunkCount = 0;
        foreach (var chunk in query.Chunks)
        {
            if (chunk.Count == 0)
            {
                continue;
            }

            inspectedChunkCount++;
            var entity = chunk.GetEntity(0);
            Assert.True(state.World.TryGetLocation(entity, out var info));
            Assert.True(info.Archetype.Signature.Count >= 8);
        }

        Assert.True(inspectedChunkCount > 0);
    }

    [Fact]
    public void Complex_query_benchmark_world_does_not_leave_excessive_empty_archetypes()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);
        var archetypes = GetArchetypes(state.World);
        var emptyCount = archetypes.Count(archetype => archetype.EntityCount == 0);

        Assert.True(emptyCount <= 12, $"Expected at most 12 empty archetypes, but found {emptyCount}.");
    }

    private static int CountEntities(Query query)
    {
        var total = 0;
        foreach (var chunk in query.Chunks)
        {
            total += chunk.Count;
        }

        return total;
    }

    private static IReadOnlyList<Archetype> GetArchetypes(World world)
    {
        var field = typeof(World).GetField("_archetypes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(world));

        var archetypes = new List<Archetype>(dictionary.Count);
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            archetypes.Add(Assert.IsType<Archetype>(entry.Value));
        }

        return archetypes;
    }
}
