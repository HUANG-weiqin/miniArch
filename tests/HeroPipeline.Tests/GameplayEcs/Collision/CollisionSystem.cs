using System;
using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using MiniArch;
using MiniArch.Core;

namespace Hero.GameplayEcs.Collision;

/// <summary>
/// Batch collision system: groups collision requests by (targetQ, targetR, shape, range, filter),
/// scans candidates once per group, and creates hit requests for all requests in the group.
/// </summary>
public sealed class CollisionSystem : ISystem
{
    private static readonly MiniArch.QueryDescription CollisionRequestDescription = new MiniArch.QueryDescription()
        .With<CollisionRequest>()
        .With<Validated>();

    private static readonly MiniArch.QueryDescription CollisionTargetDescription = new MiniArch.QueryDescription()
        .With<PositionQValue>()
        .With<PositionRValue>();

    public void Execute(in FrameContext context)
    {
        FrameView frame = context.Frame;
        ICommandRecorder commands = context.Commands;

        // Pass 1: collect all collision requests, grouped by dedup key.
        Dictionary<GroupKey, List<CollisionRequestData>> groups = CollectGroups(frame);

        // Pass 2: scan candidates once per group, create hit requests.
        foreach (var (key, requests) in groups)
        {
            ProcessGroup(frame, commands, in key, requests);
        }

        // Pass 3: destroy all processed collision requests.
        DestroyAll(frame, commands);
    }

    private static Dictionary<GroupKey, List<CollisionRequestData>> CollectGroups(FrameView frame)
    {
        var groups = new Dictionary<GroupKey, List<CollisionRequestData>>();

        foreach (Chunk chunk in frame.ChunkQuery(CollisionRequestDescription).Chunks)
        {
            ReadOnlySpan<Entity> entities = chunk.GetEntities();
            ReadOnlySpan<RequestTarget> targets = chunk.GetComponentSpan<RequestTarget>(Component<RequestTarget>.ComponentType);
            ReadOnlySpan<CollisionShape> shapes = chunk.GetComponentSpan<CollisionShape>(Component<CollisionShape>.ComponentType);
            ReadOnlySpan<CollisionTargetQValue> targetQs = chunk.GetComponentSpan<CollisionTargetQValue>(Component<CollisionTargetQValue>.ComponentType);
            ReadOnlySpan<CollisionTargetRValue> targetRs = chunk.GetComponentSpan<CollisionTargetRValue>(Component<CollisionTargetRValue>.ComponentType);
            ReadOnlySpan<CollisionRange> ranges = chunk.GetComponentSpan<CollisionRange>(Component<CollisionRange>.ComponentType);
            ReadOnlySpan<CollisionFilter> filters = chunk.GetComponentSpan<CollisionFilter>(Component<CollisionFilter>.ComponentType);
            ReadOnlySpan<HitConsequenceRuleId> consequenceIds = chunk.GetComponentSpan<HitConsequenceRuleId>(Component<HitConsequenceRuleId>.ComponentType);

            for (int i = 0; i < entities.Length; i++)
            {
                var data = new CollisionRequestData(
                    entities[i],
                    targets[i],
                    shapes[i],
                    targetQs[i],
                    targetRs[i],
                    ranges[i],
                    filters[i],
                    consequenceIds[i]);

                var key = new GroupKey(data.TargetQ.Value, data.TargetR.Value, data.Shape.Value, data.Range.Value, data.Filter.Value);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<CollisionRequestData>();
                    groups[key] = list;
                }
                list.Add(data);
            }
        }

        return groups;
    }

    private static void ProcessGroup(FrameView frame, ICommandRecorder commands, in GroupKey key, List<CollisionRequestData> requests)
    {
        foreach (Chunk chunk in frame.ChunkQuery(CollisionTargetDescription).Chunks)
        {
            ReadOnlySpan<Entity> candidates = chunk.GetEntities();
            ReadOnlySpan<PositionQValue> qs = chunk.GetComponentSpan<PositionQValue>(Component<PositionQValue>.ComponentType);
            ReadOnlySpan<PositionRValue> rs = chunk.GetComponentSpan<PositionRValue>(Component<PositionRValue>.ComponentType);

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!MatchesShape(key.Shape, key.TargetQ, key.TargetR, key.Range, qs[i].Value, rs[i].Value))
                    continue;

                Entity candidate = candidates[i];

                if (!MatchesFilter(frame, candidate, key.Filter))
                    continue;

                foreach (CollisionRequestData req in requests)
                {
                    CreateHitRequest(commands, in req, candidate);
                }
            }
        }
    }

    private static void DestroyAll(FrameView frame, ICommandRecorder commands)
    {
        foreach (Chunk chunk in frame.ChunkQuery(CollisionRequestDescription).Chunks)
        {
            ReadOnlySpan<Entity> entities = chunk.GetEntities();
            for (int i = 0; i < entities.Length; i++)
            {
                commands.Destroy(entities[i]);
            }
        }
    }

    private static void CreateHitRequest(ICommandRecorder commands, in CollisionRequestData req, Entity candidate)
    {
        Entity hitRequest = commands.Create();
        commands.Add(hitRequest, new Request());
        commands.Add(hitRequest, new CollisionHitRequest());
        commands.Add(hitRequest, new RequestTarget(candidate));
        commands.Add(hitRequest, req.ConsequenceRuleId.Value);
        commands.Add(hitRequest, RuleTier.Normal);
        commands.Add(hitRequest, new CollisionSource(req.RequestTarget.Target));
        commands.Add(hitRequest, new CollisionTarget(candidate));
        commands.Add(hitRequest, req.ConsequenceRuleId);
    }

    private static bool MatchesShape(CollisionShapeKind shape, int targetQ, int targetR, int range, int positionQ, int positionR)
    {
        return shape switch
        {
            CollisionShapeKind.Tile => positionQ == targetQ && positionR == targetR,
            CollisionShapeKind.Area => HexGeometry.Distance(targetQ, targetR, positionQ, positionR) <= range,
            _ => throw new InvalidOperationException($"Unsupported collision shape '{shape}'."),
        };
    }

    private static bool MatchesFilter(FrameView frame, Entity candidate, CollisionFilterKind filter)
    {
        return filter switch
        {
            CollisionFilterKind.Any => true,
            CollisionFilterKind.CurrentHp => frame.TryGet(candidate, out CurrentHpValue _),
            _ => throw new InvalidOperationException($"Unsupported collision filter '{filter}'."),
        };
    }

    private readonly record struct GroupKey(int TargetQ, int TargetR, CollisionShapeKind Shape, int Range, CollisionFilterKind Filter);

    private readonly record struct CollisionRequestData(
        Entity Entity,
        RequestTarget RequestTarget,
        CollisionShape Shape,
        CollisionTargetQValue TargetQ,
        CollisionTargetRValue TargetR,
        CollisionRange Range,
        CollisionFilter Filter,
        HitConsequenceRuleId ConsequenceRuleId);
}
