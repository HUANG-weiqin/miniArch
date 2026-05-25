namespace Hero.GameplayEcs.TurnSerial;

public readonly record struct TurnSerialContext;
public readonly record struct ActiveTurnCharacter(MiniArch.Entity Value);
public readonly record struct NextTurnCharacter(MiniArch.Entity Value);

public readonly record struct TurnRoundIndex(int Value);
public readonly record struct DecisionParticipant;
public readonly record struct CurrentApValue(int Value);
public readonly record struct MaxApValue(int Value);
public readonly record struct ActionPointCostValue(int Value);
public readonly record struct CardTraitDelayed;
public readonly record struct CardTraitFinale;
