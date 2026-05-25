using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Spawn;

public static class CharacterSpawnKinds
{
    public static SpawnKind Player { get; } = new(6001);
    public static SpawnKind SandbagEnemy { get; } = new(6002);
    public static SpawnKind BasicMeleeEnemy { get; } = new(6003);
}
