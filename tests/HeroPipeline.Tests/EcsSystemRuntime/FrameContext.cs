using MiniArch.Core;

namespace Hero.Ecs;

public readonly struct FrameContext
{
    internal FrameContext(
        ICommandRecorder commands,
        FrameView frame)
    {
        Commands = commands;
        Frame = frame;
    }

    public ICommandRecorder Commands { get; }

    public FrameView Frame { get; }
}


