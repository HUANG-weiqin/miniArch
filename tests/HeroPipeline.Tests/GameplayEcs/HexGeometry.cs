using System;

namespace Hero.GameplayEcs;

public static class HexGeometry
{
    public static readonly (int Dq, int Dr)[] Directions =
    [
        (1, 0),
        (1, -1),
        (0, -1),
        (-1, 0),
        (-1, 1),
        (0, 1),
    ];

    public static int Distance(int q1, int r1, int q2, int r2)
    {
        int dq = q2 - q1;
        int dr = r2 - r1;
        return (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
    }
}
