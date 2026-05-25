namespace Hero.Ecs;

public readonly record struct ModifierSlot(SlotKey Slot);
public readonly record struct DeltaModifier(int Value);
public readonly record struct SetModifier(int Value);


