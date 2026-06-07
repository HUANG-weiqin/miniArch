using System;
using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Collision;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Heal;
using Hero.GameplayEcs.Characters.Movement;

namespace Hero.GameplayEcs.Characters.Actions;

public delegate void CharacterActionPayloadTranslator(FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId);

public sealed class CharacterActionTranslationTable
{
    private readonly Dictionary<ActionKind, CharacterActionPayloadTranslator> _translators = new();

    public CharacterActionTranslationTable Register(ActionKind kind, CharacterActionPayloadTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        _translators[kind] = translator;
        return this;
    }

    public void Translate(ActionKind kind, FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId)
    {
        if (!_translators.TryGetValue(kind, out CharacterActionPayloadTranslator? translator))
        {
            throw new InvalidOperationException($"Action kind '{kind.Value}' has no registered payload translator.");
        }

        translator(context, sourceRequest, parent, actionRuleId);
    }

    public static CharacterActionTranslationTable Create()
    {
        return new CharacterActionTranslationTable()
            .Register(CharacterActionKinds.Move, TranslateMovePayload)
            .Register(CharacterActionKinds.Attack, TranslateAttackPayload)
            .Register(CardActionKinds.Move, TranslateMovePayload)
            .Register(CardActionKinds.Attack, TranslateAttackPayload)
            .Register(CardActionKinds.Defend, TranslateDefendPayload)
            .Register(CardActionKinds.Heal, TranslateHealPayload);
    }

    private static void TranslateMovePayload(FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId)
    {
        FrameView frame = context.Frame;

        MiniArch.Entity request = context.Commands.Create();
        context.Commands.Add(request, new Request());
        context.Commands.Add(request, new RequestTarget(parent));
        context.Commands.Add(request, actionRuleId.Value);
        context.Commands.Add(request, RuleTier.Normal);
        context.Commands.Add(request, frame.Get<MoveIntent>(sourceRequest));
        context.Commands.Add(request, frame.Get<MoveDqValue>(sourceRequest));
        context.Commands.Add(request, frame.Get<MoveDrValue>(sourceRequest));
    }

    private static void TranslateAttackPayload(FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId)
    {
        FrameView frame = context.Frame;

        int targetQ = frame.Get<AttackTargetQValue>(sourceRequest).Value;
        int targetR = frame.Get<AttackTargetRValue>(sourceRequest).Value;

        MiniArch.Entity collisionRequest = context.Commands.Create();
        context.Commands.Add(collisionRequest, new Request());
        context.Commands.Add(collisionRequest, new RequestTarget(parent));
        context.Commands.Add(collisionRequest, new CollisionRequest());
        context.Commands.Add(collisionRequest, new CollisionOrigin(parent));
        context.Commands.Add(collisionRequest, new CollisionShape(CollisionShapeKind.Tile));
        context.Commands.Add(collisionRequest, new CollisionTargetQValue(targetQ));
        context.Commands.Add(collisionRequest, new CollisionTargetRValue(targetR));
        context.Commands.Add(collisionRequest, new CollisionRange(0));
        context.Commands.Add(collisionRequest, new CollisionFilter(CollisionFilterKind.CurrentHp));
        context.Commands.Add(collisionRequest, new HitConsequenceRuleId(actionRuleId.Value));

        MiniArch.Entity swingEffect = context.Commands.Create();
        context.Commands.Add(swingEffect, new Effect());
        context.Commands.Add(swingEffect, CharacterAttackIds.SwingEffect);
        context.Commands.Add(swingEffect, new AttackSwingEffectMarker());
        context.Commands.Add(swingEffect, new EffectSource(parent));
        context.Commands.Add(swingEffect, new AttackTargetQValue(targetQ));
        context.Commands.Add(swingEffect, new AttackTargetRValue(targetR));
        context.Commands.Add(swingEffect, new AttackPresentationKindValue(AttackPresentationKind.Melee));
    }

    private static void TranslateDefendPayload(FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId)
    {
        FrameView frame = context.Frame;

        // Read armor amount from the card (sourceRequest's target is the card entity)
        MiniArch.Entity card = frame.Get<RequestTarget>(sourceRequest).Target;
        var armorAmount = frame.Get<ArmorAmountValue>(card);

        MiniArch.Entity request = context.Commands.Create();
        context.Commands.Add(request, new Request());
        context.Commands.Add(request, new RequestTarget(parent));
        context.Commands.Add(request, actionRuleId.Value);
        context.Commands.Add(request, RuleTier.Normal);
        context.Commands.Add(request, armorAmount);
    }

    private static void TranslateHealPayload(FrameContext context, MiniArch.Entity sourceRequest, MiniArch.Entity parent, ActionRuleId actionRuleId)
    {
        FrameView frame = context.Frame;

        // Read heal amount from the card (sourceRequest's target is the card entity)
        MiniArch.Entity card = frame.Get<RequestTarget>(sourceRequest).Target;
        var healAmount = frame.Get<HealAmountValue>(card);

        MiniArch.Entity request = context.Commands.Create();
        context.Commands.Add(request, new Request());
        context.Commands.Add(request, new RequestTarget(parent));
        context.Commands.Add(request, actionRuleId.Value);
        context.Commands.Add(request, RuleTier.Normal);
        context.Commands.Add(request, healAmount);
    }
}

