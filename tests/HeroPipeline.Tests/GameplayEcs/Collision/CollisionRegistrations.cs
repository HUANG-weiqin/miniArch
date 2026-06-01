using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using MiniArch.Core;

namespace Hero.GameplayEcs.Collision;

public static class CollisionRegistrations
{
    private static readonly MiniArch.QueryDescription CollisionTargetDescription = new MiniArch.QueryDescription()
        .With<PositionQValue>()
        .With<PositionRValue>();

    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CollisionIds.ResolveRule, ApplyCollisionRule);
    }

    private static void ApplyCollisionRule(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        RequestTarget requestTarget = frame.Get<RequestTarget>(request);
        CollisionShape shape = frame.Get<CollisionShape>(request);
        CollisionTargetQValue targetQ = frame.Get<CollisionTargetQValue>(request);
        CollisionTargetRValue targetR = frame.Get<CollisionTargetRValue>(request);
        CollisionRange range = frame.Get<CollisionRange>(request);
        CollisionFilter filter = frame.Get<CollisionFilter>(request);
        HitConsequenceRuleId consequenceRuleId = frame.Get<HitConsequenceRuleId>(request);

        foreach (var row in frame.ChunkQuery(CollisionTargetDescription).EachSpan<PositionQValue, PositionRValue>())
        {
            if (!MatchesShape(shape, targetQ.Value, targetR.Value, range.Value, row.Get0().Value, row.Get1().Value))
            {
                continue;
            }

            MiniArch.Entity candidate = row.Entity;

            if (!MatchesFilter(frame, candidate, filter))
            {
                continue;
            }

            MiniArch.Entity hitRequest = context.Commands.Create();
            context.Commands.Add(hitRequest, new Request());
            context.Commands.Add(hitRequest, new CollisionHitRequest());
            context.Commands.Add(hitRequest, new RequestTarget(candidate));
            context.Commands.Add(hitRequest, consequenceRuleId.Value);
            context.Commands.Add(hitRequest, RuleTier.Normal);
            context.Commands.Add(hitRequest, new CollisionSource(requestTarget.Target));
            context.Commands.Add(hitRequest, new CollisionTarget(candidate));
            context.Commands.Add(hitRequest, consequenceRuleId);
        }
    }

    private static bool MatchesShape(CollisionShape shape, int targetQ, int targetR, int range, int positionQ, int positionR)
    {
        return shape.Value switch
        {
            CollisionShapeKind.Tile => positionQ == targetQ && positionR == targetR,
            CollisionShapeKind.Area => HexGeometry.Distance(targetQ, targetR, positionQ, positionR) <= range,
            _ => throw new InvalidOperationException($"Unsupported collision shape '{shape.Value}'."),
        };
    }

    private static bool MatchesFilter(FrameView frame, MiniArch.Entity candidate, CollisionFilter filter)
    {
        return filter.Value switch
        {
            CollisionFilterKind.Any => true,
            CollisionFilterKind.CurrentHp => frame.TryGet(candidate, out CurrentHpValue _),
            _ => throw new InvalidOperationException($"Unsupported collision filter '{filter.Value}'."),
        };
    }
}


