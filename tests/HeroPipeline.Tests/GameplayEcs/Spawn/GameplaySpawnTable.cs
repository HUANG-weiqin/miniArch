using Hero.Ecs;
using Hero.GameplayEcs.Characters.Spawn;

namespace Hero.GameplayEcs.Spawn;

public static class GameplaySpawnTable
{
    public static SpawnTable Create()
    {
        SpawnTable table = new();
        CharacterSpawnRegistrations.Register(table);
        return table;
    }
}


