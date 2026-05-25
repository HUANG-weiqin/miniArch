using System;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public sealed class EffectDispatchSystem : ISystem
{
    private readonly EffectTable _effectTable;
    private static readonly MiniArch.QueryDescription EffectQueryDescription = new MiniArch.QueryDescription()
        .With<Effect>()
        .With<EffectId>()
        .With<Validated>()
        .Without<Rejected>();

    public EffectDispatchSystem(EffectTable effectTable)
    {
        _effectTable = effectTable ?? throw new ArgumentNullException(nameof(effectTable));
    }

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;

        foreach (MiniArch.Entity entity in context.Frame.Each(EffectQueryDescription))
        {
            EffectId effectId = context.Frame.Get<EffectId>(entity);

            if (!_effectTable.TryGet(effectId, out EffectHandler handler))
            {
                throw new InvalidOperationException(
                    $"No effect handler is registered for effect '{effectId.Value}'.");
            }

            if (handler is not null)
            {
                handler(context, entity);
            }

            commands.Destroy(entity);
        }
    }
}


