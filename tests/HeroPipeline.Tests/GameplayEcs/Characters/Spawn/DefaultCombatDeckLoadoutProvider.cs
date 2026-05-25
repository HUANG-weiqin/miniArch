using System;
using Hero.Ecs;
using Hero.GameplayEcs.Cards.Decks;

namespace Hero.GameplayEcs.Characters.Spawn;

public sealed class DefaultCombatDeckLoadoutProvider : ICombatDeckLoadoutProvider
{
    public CombatDeckLoadout GetLoadout(SpawnKind characterKind)
    {
        if (characterKind == CharacterSpawnKinds.Player)
        {
            return DeckRecipeCatalog.CreateLoadout(DeckRecipeIds.PlayerStarter);
        }

        if (characterKind == CharacterSpawnKinds.BasicMeleeEnemy)
        {
            return DeckRecipeCatalog.CreateLoadout(DeckRecipeIds.BasicMeleeEnemy);
        }

        if (characterKind == CharacterSpawnKinds.SandbagEnemy)
        {
            return CombatDeckLoadout.Empty;
        }

        throw new InvalidOperationException($"Unknown character spawn kind for deck loadout: {characterKind.Value}");
    }
}
