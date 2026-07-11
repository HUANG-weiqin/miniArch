using FsCheck;
using FsCheck.Xunit;
using MiniArch.Core;

namespace MiniArchTests.PropertyBased;

public sealed class SerializationRoundtripPropertyTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public bool Snapshot_roundtrip_preserves_canonical_checksum(
        EntityDef[] entities)
    {
        var world = BuildWorld(entities);
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;
        var loaded = WorldSnapshot.Load(ms);

        var originalHash = world.CanonicalChecksum();
        var loadedHash = loaded.CanonicalChecksum();
        return originalHash.SequenceEqual(loadedHash);
    }

    private static World BuildWorld(EntityDef[] defs)
    {
        var world = new World();
        foreach (var def in defs)
        {
            var e = world.CreateEmpty();
            if (def.HasPosition)
                world.Add(e, new Position(def.X, def.Y));
            if (def.HasVelocity)
                world.Add(e, new Velocity(def.Dx, def.Dy));
            if (def.HasHealth)
                world.Add(e, new Health(def.HealthValue));
        }
        return world;
    }
}

public sealed record EntityDef(
    bool HasPosition, int X, int Y,
    bool HasVelocity, int Dx, int Dy,
    bool HasHealth, int HealthValue);
