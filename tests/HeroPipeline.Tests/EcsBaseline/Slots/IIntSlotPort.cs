using CoreCommandBuffer = MiniArch.Core.ICommandRecorder;

namespace Hero.Ecs;

public interface IIntSlotPort
{
    bool TryRead(FrameView frame, MiniArch.Entity target, out int current);

    void Write(CoreCommandBuffer cb, MiniArch.Entity target, int next);
}


