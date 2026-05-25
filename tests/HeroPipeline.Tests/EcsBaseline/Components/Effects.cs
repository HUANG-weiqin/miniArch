namespace Hero.Ecs;

public readonly record struct Effect;
public readonly record struct EffectId(int Value);
public readonly record struct EffectSource(MiniArch.Entity Source);
public readonly record struct EffectTarget(MiniArch.Entity Target);


