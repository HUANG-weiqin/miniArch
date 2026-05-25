using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public interface IIntSlotPort
{
    bool TryRead(FrameView frame, MiniArch.Entity target, out int current);

    void Write(CoreCommandBuffer cb, MiniArch.Entity target, int next);
}


