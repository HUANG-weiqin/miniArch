using System.Collections.Generic;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

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

        AddValidated(commands, context.Frame.Each(RequestQueryDescription));
        AddValidated(commands, context.Frame.Each(SpawnQueryDescription));
        AddValidated(commands, context.Frame.Each(EffectQueryDescription));
    }

    private static void AddValidated(CoreCommandBuffer commands, IEnumerable<MiniArch.Entity> entities)
    {
        foreach (MiniArch.Entity entity in entities)
        {
            commands.Add(entity, new Validated());
        }
    }
}


