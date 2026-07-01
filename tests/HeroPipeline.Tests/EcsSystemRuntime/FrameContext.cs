using MiniArch.Core;

namespace Hero.Ecs;

public readonly struct FrameContext
{
    internal FrameContext(
        CommandStream commands,
        FrameView frame)
    {
        Commands = commands;
        Frame = frame;
    }

    public CommandStream Commands { get; }

    public FrameView Frame { get; }
}


