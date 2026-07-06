namespace MiniArch;

public enum TransitionKind
{
    Entered,
    Exited,
}

public readonly struct Transition
{
    public readonly TransitionKind Kind;
    public readonly Entity Entity;

    public Transition(TransitionKind kind, Entity entity)
    {
        Kind = kind;
        Entity = entity;
    }
}
