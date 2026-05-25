namespace Hero.Ecs;

public interface ISystem
{
    void Execute(in FrameContext context);
}


