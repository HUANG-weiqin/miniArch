using System;
using MiniArch.Core;
using CoreCommandBuffer = MiniArch.Core.ICommandRecorder;

namespace Hero.Ecs;

public sealed class ValidationSystem : ISystem
{
    private static readonly MiniArch.QueryDescription RequestQueryDescription = new MiniArch.QueryDescription()
        .With<Request>()
        .Without<PendingRequest>()
        .Without<Resident>()
        .Without<Validated>()
        .Without<Rejected>();

    private static readonly MiniArch.QueryDescription SpawnQueryDescription = new MiniArch.QueryDescription()
        .With<SpawnPending>()
        .With<SpawnKind>()
        .Without<Validated>()
        .Without<Rejected>();

    private static readonly MiniArch.QueryDescription EffectQueryDescription = new MiniArch.QueryDescription()
        .With<Effect>()
        .With<EffectId>()
        .Without<PendingRequest>()
        .Without<Resident>()
        .Without<Validated>()
        .Without<Rejected>();

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;
        FrameView frame = context.Frame;

        AddValidated(commands, frame, RequestQueryDescription);
        AddValidated(commands, frame, SpawnQueryDescription);
        AddValidated(commands, frame, EffectQueryDescription);
    }

    private static void AddValidated(CoreCommandBuffer commands, FrameView frame, MiniArch.QueryDescription query)
    {
        foreach (MiniArch.Entity entity in frame.ChunkQuery(query).EachSpan())
        {
            commands.Add(entity, new Validated());
        }
    }
}
