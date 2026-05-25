namespace Hero.GameplayEcs.Characters.Attack;

public readonly record struct AttackIntent;
public readonly record struct AttackTargetQValue(int Value);
public readonly record struct AttackTargetRValue(int Value);
public readonly record struct DamageAmountValue(int Value);
public readonly record struct AttackSwingEffectMarker;
public readonly record struct AttackPresentationKindValue(AttackPresentationKind Value);

public enum AttackPresentationKind
{
    Melee = 0,
}


