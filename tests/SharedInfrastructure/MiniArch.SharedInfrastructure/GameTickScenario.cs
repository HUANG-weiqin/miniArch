namespace MiniArchBenchmarks.GameTick;

public static class GameTickData
{
    public const int DurationSeconds = 5;
    public const int ReportInterval = 10;
    public const int WarmupTicks = 20;
    public const int SpawnPerTick = 4_500;
    public const int DestroyPerTick = 1_500;
    public const int DebuffPerTick = 9_000;

    public const int PlayerCount = 1_000;
    public const int MeleeEnemyCount = 7_500;
    public const int RangedEnemyCount = 5_000;
    public const int BossEnemyCount = 100;
    public const int NpcCount = 3_000;
    public const int PetCount = 2_000;
    public const int ProjectileCount = 4_000;
    public const int StaticObjectCount = 4_000;
    public const int DestructibleCount = 3_000;
    public const int EnvironmentCount = 18_400;
    public const int TrapCount = 1_000;
    public const int LootDropCount = 1_000;

    public const int TotalEntityCount = PlayerCount + MeleeEnemyCount + RangedEnemyCount
        + BossEnemyCount + NpcCount + PetCount + ProjectileCount + StaticObjectCount
        + DestructibleCount + EnvironmentCount + TrapCount + LootDropCount;
}
