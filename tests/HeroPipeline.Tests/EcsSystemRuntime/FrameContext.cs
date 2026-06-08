using MiniArch.Core;

namespace Hero.Ecs;

public readonly struct FrameContext
{
    internal FrameContext(
        CommandBuffer commands,
        FrameView frame)
    {
        Commands = commands;
        Frame = frame;
    }

    public CommandBuffer Commands { get; }

    public FrameView Frame { get; }
}


