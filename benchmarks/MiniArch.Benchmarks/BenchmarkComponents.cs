namespace MiniArch.Benchmarks;

public struct Position
{
    public Position(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
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

public struct Damage
{
    public Damage(int min, int max)
    {
        Min = min;
        Max = max;
    }

    public int Min;
    public int Max;
}

public struct Team
{
    public Team(int value)
    {
        Value = value;
    }

    public int Value;
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
