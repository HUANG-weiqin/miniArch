using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Movement;
using Hero.GameplayEcs.Cards.Decks;
using Hero.GameplayEcs.TurnSerial;

namespace Hero.GameplayEcs.Characters.Spawn;

public static class CharacterSpawnRegistrations
{
    private readonly record struct CharacterDefaults(
        int Hp, int MaxHp, int AttackPower, int Ap, int MaxAp,
        bool HasActions);

    private static readonly ICombatDeckLoadoutProvider DeckLoadouts = new DefaultCombatDeckLoadoutProvider();

    private static readonly CharacterDefaults Player = new(10, 10, 3, 3, 3, true);
    private static readonly CharacterDefaults BasicMeleeEnemy = new(10, 10, 2, 3, 3, true);
    private static readonly CharacterDefaults SandbagEnemy = new(100, 100, 0, 0, 0, false);

    public static SpawnTable Register(SpawnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table
            .Register(CharacterSpawnKinds.Player, (ctx, e) => ApplyDefaults(ctx, e, Player, CharacterSpawnKinds.Player))
            .Register(CharacterSpawnKinds.SandbagEnemy, (ctx, e) => ApplyDefaults(ctx, e, SandbagEnemy, CharacterSpawnKinds.SandbagEnemy))
            .Register(CharacterSpawnKinds.BasicMeleeEnemy, (ctx, e) => ApplyDefaults(ctx, e, BasicMeleeEnemy, CharacterSpawnKinds.BasicMeleeEnemy));
    }

    private static void ApplyDefaults(FrameContext context, MiniArch.Entity entity, CharacterDefaults defaults, SpawnKind spawnKind)
    {
        SetIfMissing(context, entity, new PositionQValue(0));
        SetIfMissing(context, entity, new PositionRValue(0));
        SetIfMissing(context, entity, new CurrentHpValue(defaults.Hp));
        SetIfMissing(context, entity, new MaxHpValue(defaults.MaxHp));
        SetIfMissing(context, entity, new CurrentArmorValue(0));
        SetIfMissing(context, entity, new AttackPowerValue(defaults.AttackPower));
        SetIfMissing(context, entity, new DecisionParticipant());
        SetIfMissing(context, entity, new CurrentApValue(defaults.Ap));
        SetIfMissing(context, entity, new MaxApValue(defaults.MaxAp));

        if (defaults.HasActions)
        {
            AttachAction(context, entity, CharacterActionKinds.Move, CharacterMovementIds.MoveRule);
            AttachAction(context, entity, CharacterActionKinds.Attack, CharacterAttackIds.AttackRule);
        }

        CombatDeckLoadout loadout = DeckLoadouts.GetLoadout(spawnKind);
        if (loadout.Cards.Count > 0)
        {
            DeckBuilder.Build(context, entity, loadout);
        }
    }

    private static void SetIfMissing<TValue>(FrameContext context, MiniArch.Entity entity, TValue value)
        where TValue : struct
    {
        if (!context.Frame.TryGet(entity, out TValue _))
        {
            context.Commands.Add(entity, value);
        }
    }

    private static void AttachAction(FrameContext context, MiniArch.Entity entity, ActionKind kind, RuleId ruleId)
    {
        MiniArch.Entity action = context.Commands.Create();
        context.Commands.Add(action, new ActionEntity());
        context.Commands.Add(action, kind);
        context.Commands.Add(action, new ActionRuleId(ruleId));
        context.Commands.Add(action, new ActionPointCostValue(1));

        context.Commands.Link(entity, action);
    }
}
