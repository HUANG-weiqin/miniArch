// Correctness smoke tests for FrameReadModel ValueLab.
// Confirms that the MiniArch query infrastructure works correctly
// with the local Position/Velocity component model.

using MiniArch;

namespace FrameReadModels.ValueLab;

/// <summary>
/// Runs minimal smoke correctness scenarios using real MiniArch.World,
/// pre-constructed QueryDescription, and world.Query().GetChunks().
/// No LINQ. Must find at least one entity with a Position component.
/// </summary>
internal static class FrameReadModelCorrectness
{
    /// <summary>
    /// Runs all smoke tests. Returns true on pass, false on failure.
    /// </summary>
    public static bool RunAll()
    {
        var pass = true;

        pass &= Run("Smoke: single entity with Position", SmokeSinglePosition);
        pass &= Run("Smoke: multiple entities with Position+Velocity", SmokePositionVelocity);
        pass &= Run("Smoke: mixed archetypes", SmokeMixedArchetypes);

        return pass;
    }

    private static bool Run(string name, Func<bool> test)
    {
        try
        {
            var result = test();
            if (!result)
                Console.Error.WriteLine($"  FAIL: {name}");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAIL: {name} — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Create one entity with Position. Query for Position → must find it.
    /// </summary>
    private static bool SmokeSinglePosition()
    {
        using var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1.0f, 2.0f));

        var query = new QueryDescription().With<Position>();
        int found = 0;
        foreach (var chunk in world.Query(query).GetChunks())
        {
            found += chunk.Count;
            // Read a Position to confirm access
            var positions = chunk.GetSpan<Position>();
            _ = positions[0];
        }

        if (found < 1)
        {
            Console.Error.WriteLine("  Expected at least 1 entity with Position, found 0");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Create multiple entities with Position+Velocity. Query for both.
    /// </summary>
    private static bool SmokePositionVelocity()
    {
        using var world = new World();
        const int count = 10;

        for (int i = 0; i < count; i++)
        {
            var e = world.Create();
            world.Add(e, new Position(i, i * 2));
            world.Add(e, new Velocity(1, 0));
        }

        var query = new QueryDescription().With<Position>().With<Velocity>();
        int found = 0;
        foreach (var chunk in world.Query(query).GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (int i = 0; i < chunk.Count; i++)
            {
                _ = positions[i];
                _ = velocities[i];
                found++;
            }
        }

        if (found != count)
        {
            Console.Error.WriteLine($"  Expected {count} entities with Position+Velocity, found {found}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Create entities in two different archetypes (Position only, Position+Velocity).
    /// Query for Position only — must find entities in both archetypes.
    /// </summary>
    private static bool SmokeMixedArchetypes()
    {
        using var world = new World();
        const int posOnlyCount = 5;
        const int posVelCount = 7;

        for (int i = 0; i < posOnlyCount; i++)
        {
            var e = world.Create();
            world.Add(e, new Position(i, 0));
        }

        for (int i = 0; i < posVelCount; i++)
        {
            var e = world.Create();
            world.Add(e, new Position(i, i));
            world.Add(e, new Velocity(0, 1));
        }

        var query = new QueryDescription().With<Position>();
        int found = 0;
        foreach (var chunk in world.Query(query).GetChunks())
        {
            found += chunk.Count;
        }

        int expected = posOnlyCount + posVelCount;
        if (found != expected)
        {
            Console.Error.WriteLine($"  Expected {expected} entities with Position, found {found}");
            return false;
        }

        return true;
    }
}
