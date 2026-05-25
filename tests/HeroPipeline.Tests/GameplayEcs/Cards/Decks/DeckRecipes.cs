using System;
using System.Collections.Generic;
using System.Linq;
using Hero.Ecs;

namespace Hero.GameplayEcs.Cards.Decks;

public readonly record struct CombatDeckCardSpec(CardTemplateId TemplateId, int UpgradeLevel = 0);

public sealed class CombatDeckLoadout
{
    public CombatDeckLoadout(IReadOnlyList<CombatDeckCardSpec> cards)
    {
        Cards = cards.ToArray();
    }

    public IReadOnlyList<CombatDeckCardSpec> Cards { get; }

    public static CombatDeckLoadout Empty { get; } = new([]);
}

public interface ICombatDeckLoadoutProvider
{
    CombatDeckLoadout GetLoadout(SpawnKind characterKind);
}

public readonly record struct DeckRecipeId(int Value);

public static class DeckRecipeIds
{
    public static DeckRecipeId None { get; } = new(0);
    public static DeckRecipeId PlayerStarter { get; } = new(1);
    public static DeckRecipeId BasicMeleeEnemy { get; } = new(2);
}

public static class DeckRecipeCatalog
{
    public static CombatDeckLoadout CreateLoadout(DeckRecipeId id)
    {
        if (id == DeckRecipeIds.None)
        {
            return CombatDeckLoadout.Empty;
        }

        if (id == DeckRecipeIds.PlayerStarter)
        {
            return CreateRepeatedLoadout(CardTemplateIds.Move, 8, CardTemplateIds.Attack, 8);
        }

        if (id == DeckRecipeIds.BasicMeleeEnemy)
        {
            return CreateRepeatedLoadout(CardTemplateIds.Move, 8, CardTemplateIds.DelayedFinaleAttack, 8);
        }

        throw new InvalidOperationException($"Unknown deck recipe id: {id.Value}");
    }

    private static CombatDeckLoadout CreateRepeatedLoadout(
        CardTemplateId firstTemplateId,
        int firstCount,
        CardTemplateId secondTemplateId,
        int secondCount)
    {
        List<CombatDeckCardSpec> cards = new(firstCount + secondCount);
        AddRepeated(cards, firstTemplateId, firstCount);
        AddRepeated(cards, secondTemplateId, secondCount);
        return new CombatDeckLoadout(cards);
    }

    private static void AddRepeated(List<CombatDeckCardSpec> cards, CardTemplateId templateId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            cards.Add(new CombatDeckCardSpec(templateId));
        }
    }
}
