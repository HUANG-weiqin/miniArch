using System;
using Hero.Ecs;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Movement;
using MiniArch.Core;

namespace Hero.GameplayEcs.TurnSerial;

public static class TurnSerialCardPlayRegistrations
{
    private static readonly MiniArch.QueryDescription ContextDescription = new MiniArch.QueryDescription()
        .With<TurnSerialContext>()
        .With<ActiveTurnCharacter>();

    private static readonly MiniArch.QueryDescription CardPlayRequestDescription = new MiniArch.QueryDescription()
        .With<Request>()
        .With<RequestTarget>()
        .With<RuleId>()
        .With<Validated>()
        .Without<PendingRequest>()
        .Without<Rejected>();

    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(TurnSerialIds.CardPlayRule, ApplyPlayCard);
    }

    private static void ApplyPlayCard(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        ThrowIfMultipleCardPlayRequests(frame);

        MiniArch.Entity card = frame.Get<RequestTarget>(request).Target;

        if (!TryGetCardOwner(frame, card, out MiniArch.Entity owner) ||
            !IsActiveCharacter(frame, owner) ||
            !CanPayForCard(frame, owner, card))
        {
            return;
        }

        int cost = frame.Get<ActionPointCostValue>(card).Value;

        MiniArch.Entity apCost = context.Commands.Create();
        context.Commands.Add(apCost, new Request());
        context.Commands.Add(apCost, new RequestTarget(owner));
        context.Commands.Add(apCost, new ModifierSlot(TurnSerialSlotKeys.CurrentAp));
        context.Commands.Add(apCost, new DeltaModifier(-cost));

        bool delayed = frame.TryGet(card, out CardTraitDelayed _);
        CreateActionDispatchRequest(context, frame, request, card, delayed);

        int nextOrder = CardOrder.NextAfterMax(frame, owner, CardZoneKind.Discard, 0);
        CardZoneTransition.MoveTo(context, card, CardZoneKind.Discard, nextOrder);

        if (frame.TryGet(card, out CardTraitFinale _))
        {
            CreateEndTurnRequest(context, owner);
        }
    }

    private static void CreateEndTurnRequest(FrameContext context, MiniArch.Entity character)
    {
        MiniArch.Entity endTurn = context.Commands.Create();
        context.Commands.Add(endTurn, new Request());
        context.Commands.Add(endTurn, new RequestTarget(character));
        context.Commands.Add(endTurn, TurnSerialIds.EndTurnRule);
        context.Commands.Add(endTurn, RuleTier.Normal);
    }

    private static void ThrowIfMultipleCardPlayRequests(FrameView frame)
    {
        int count = 0;
        foreach (var chunk in frame.ChunkQuery(CardPlayRequestDescription).GetChunks())
        {
            var ruleIds = chunk.GetSpan<RuleId>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (ruleIds[i] != TurnSerialIds.CardPlayRule)
                {
                    continue;
                }

                count++;
                if (count > 1)
                {
                    throw new InvalidOperationException("Serial card play only supports one play request per frame.");
                }
            }
        }
    }

    private static bool TryGetCardOwner(FrameView frame, MiniArch.Entity card, out MiniArch.Entity owner)
    {
        owner = default;

        if (!frame.TryGet(card, out ActionEntity _) ||
            !frame.TryGet(card, out CardZone zone) ||
            zone.Value != CardZoneKind.Hand ||
            !frame.TryGetParent(card, out owner))
        {
            return false;
        }

        return true;
    }

    private static bool IsActiveCharacter(FrameView frame, MiniArch.Entity character)
    {
        foreach (var chunk in frame.ChunkQuery(ContextDescription).GetChunks())
        {
            var activeChars = chunk.GetSpan<ActiveTurnCharacter>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (activeChars[i].Value == character)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CanPayForCard(FrameView frame, MiniArch.Entity owner, MiniArch.Entity card)
    {
        if (!frame.TryGet(owner, out CurrentApValue currentAp) ||
            !frame.TryGet(card, out ActionPointCostValue cost))
        {
            return false;
        }

        return currentAp.Value >= cost.Value;
    }

    private static void CreateActionDispatchRequest(FrameContext context, FrameView frame, MiniArch.Entity request, MiniArch.Entity card, bool delayed)
    {
        MiniArch.Entity actionRequest = context.Commands.Create();
        context.Commands.Add(actionRequest, new Request());
        context.Commands.Add(actionRequest, new RequestTarget(card));
        context.Commands.Add(actionRequest, CharacterActionIds.DispatchRule);
        context.Commands.Add(actionRequest, RuleTier.Normal);

        if (delayed)
        {
            context.Commands.Add(actionRequest, new PendingRequest());
        }

        CopyIfPresent<AttackIntent>(frame, request, actionRequest, context);
        CopyIfPresent<AttackTargetQValue>(frame, request, actionRequest, context);
        CopyIfPresent<AttackTargetRValue>(frame, request, actionRequest, context);
        CopyIfPresent<MoveIntent>(frame, request, actionRequest, context);
        CopyIfPresent<MoveDqValue>(frame, request, actionRequest, context);
        CopyIfPresent<MoveDrValue>(frame, request, actionRequest, context);
    }

    private static void CopyIfPresent<TValue>(FrameView frame, MiniArch.Entity source, MiniArch.Entity target, FrameContext context)
        where TValue : unmanaged
    {
        if (frame.TryGet(source, out TValue value))
        {
            context.Commands.Add(target, value);
        }
    }
}
