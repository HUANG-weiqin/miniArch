using System;

namespace Hero.Ecs;

public readonly record struct TriggerConditionId(int Value);
public readonly record struct TriggerActionId(int Value);
public readonly record struct TriggerCondition(TriggerConditionId Id);
public readonly record struct TriggerAction(TriggerActionId Id);
public readonly record struct TriggerGuard(Func<FrameView, MiniArch.Entity, bool> Condition);
public readonly record struct TriggerPostAction(Action<FrameContext, MiniArch.Entity, TriggerTargets> Handler);
