using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Spawn;

namespace Hero.GameplayEcs.Bootstrap;

public static class CharacterSpawnBootstrap
{
    public static MiniArch.Entity CreatePlayer(MiniArchRuntime runtime)
    {
        MiniArch.Entity player = runtime.Recorder.CreateImmediate();
        runtime.Recorder.Add(player, new SpawnPending());
        runtime.Recorder.Add(player, CharacterSpawnKinds.Player);
        return player;
    }

    public static MiniArch.Entity CreatePlayerAt(MiniArchRuntime runtime, int q, int r) =>
        CreateEntityAt(runtime, CharacterSpawnKinds.Player, q, r);

    public static MiniArch.Entity CreateSandbagEnemyAt(MiniArchRuntime runtime, int q, int r) =>
        CreateEntityAt(runtime, CharacterSpawnKinds.SandbagEnemy, q, r);

    public static MiniArch.Entity CreateBasicMeleeEnemyAt(MiniArchRuntime runtime, int q, int r) =>
        CreateEntityAt(runtime, CharacterSpawnKinds.BasicMeleeEnemy, q, r);

    private static MiniArch.Entity CreateEntityAt(MiniArchRuntime runtime, SpawnKind kind, int q, int r)
    {
        MiniArch.Entity entity = runtime.Recorder.CreateImmediate();
        runtime.Recorder.Add(entity, new SpawnPending());
        runtime.Recorder.Add(entity, kind);
        runtime.Recorder.Add(entity, new PositionQValue(q));
        runtime.Recorder.Add(entity, new PositionRValue(r));
        return entity;
    }
}
