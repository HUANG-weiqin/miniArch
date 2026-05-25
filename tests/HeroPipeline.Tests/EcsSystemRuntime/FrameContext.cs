using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public readonly struct FrameContext
{
    internal FrameContext(
        CoreCommandBuffer commands,
        FrameView frame)
    {
        Commands = commands;
        Frame = frame;
    }

    public CoreCommandBuffer Commands { get; }

    public FrameView Frame { get; }
}


