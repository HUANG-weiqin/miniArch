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
