namespace MiniArchBenchmarks;

public struct Position : System.IEquatable<Position>
{
    public Position(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;

    public readonly bool Equals(Position other) => X == other.X && Y == other.Y;
    public override readonly bool Equals(object? obj) => obj is Position p && Equals(p);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Position left, Position right) => left.Equals(right);
    public static bool operator !=(Position left, Position right) => !left.Equals(right);
}

public struct Velocity
{
    public Velocity(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
}

public struct Health
{
    public Health(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Acceleration
{
    public Acceleration(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
}

public struct Mana
{
    public Mana(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Armor
{
    public Armor(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Mass
{
    public Mass(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Rotation
{
    public Rotation(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Team
{
    public Team(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Shield
{
    public Shield(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct DamageRange
{
    public DamageRange(int min, int max)
    {
        Min = min;
        Max = max;
    }

    public int Min;
    public int Max;
}

public struct Cooldown
{
    public Cooldown(int ticks)
    {
        Ticks = ticks;
    }

    public int Ticks;
}

public struct SpawnTick
{
    public SpawnTick(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct Target
{
    public Target(int entityId)
    {
        EntityId = entityId;
    }

    public int EntityId;
}

public struct Damage
{
    public Damage(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct ExcludedTag
{
    public ExcludedTag(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct AnyTagA
{
    public AnyTagA(int value)
    {
        Value = value;
    }

    public int Value;
}

public struct AnyTagB
{
    public AnyTagB(int value)
    {
        Value = value;
    }

    public int Value;
}
