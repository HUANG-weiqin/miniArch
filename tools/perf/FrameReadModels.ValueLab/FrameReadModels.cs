// Component models used in FrameReadModel ValueLab experiments.
// Not part of the public MiniArch API — local to this lab only.

namespace FrameReadModels.ValueLab;

/// <summary>
/// Minimal 2D position component for query smoke tests and layout benchmarks.
/// </summary>
internal struct Position
{
    public float X;
    public float Y;

    public Position(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// Minimal 2D velocity component for multi-component query smoke tests.
/// </summary>
internal struct Velocity
{
    public float Dx;
    public float Dy;

    public Velocity(float dx, float dy)
    {
        Dx = dx;
        Dy = dy;
    }
}
