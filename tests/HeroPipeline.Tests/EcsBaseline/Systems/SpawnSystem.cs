using System;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public sealed class SpawnSystem : ISystem
{
    private readonly SpawnTable _spawnTable;
    private static readonly MiniArch.QueryDescription SpawnQueryDescription = new MiniArch.QueryDescription()
        .With<SpawnPending>()
        .With<SpawnKind>()
        .With<Validated>()
        .Without<Rejected>();

    public SpawnSystem(SpawnTable spawnTable)
    {
        _spawnTable = spawnTable ?? throw new ArgumentNullException(nameof(spawnTable));
    }

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;

        foreach (MiniArch.Entity entity in context.Frame.Each(SpawnQueryDescription))
        {
            SpawnKind kind = context.Frame.Get<SpawnKind>(entity);

            if (!_spawnTable.TryGet(kind, out SpawnHandler handler))
            {
                throw new InvalidOperationException(
                    $"No spawn handler is registered for spawn kind '{kind.Value}'.");
            }

            handler(context, entity);
            commands.Remove<SpawnPending>(entity);
            commands.Remove<Validated>(entity);
        }
    }
}


