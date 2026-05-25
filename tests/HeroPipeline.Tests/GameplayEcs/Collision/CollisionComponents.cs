using Hero.Ecs;

namespace Hero.GameplayEcs.Collision;

public readonly record struct CollisionRequest;
public readonly record struct CollisionHitRequest;
public readonly record struct CollisionOrigin(MiniArch.Entity Entity);
public readonly record struct CollisionSource(MiniArch.Entity Entity);
public readonly record struct CollisionTarget(MiniArch.Entity Entity);
public readonly record struct CollisionTargetQValue(int Value);
public readonly record struct CollisionTargetRValue(int Value);
public readonly record struct CollisionRange(int Value);
public readonly record struct CollisionShape(CollisionShapeKind Value);
public readonly record struct CollisionFilter(CollisionFilterKind Value);
public readonly record struct HitConsequenceRuleId(RuleId Value);


public enum CollisionShapeKind
{
    Tile = 1,
    Area = 2,
}

public enum CollisionFilterKind
{
    Any = 0,
    CurrentHp = 1,
}


