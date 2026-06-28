using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;

namespace Hero.GameplayEcs.Characters.Attack;

public static class CharacterAttackBootstrap
{
    public static MiniArch.Entity CreateAttackRequest(MiniArchRuntime runtime, MiniArch.Entity actionEntity, int q, int r)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Recorder.CreateImmediate();
        runtime.Recorder.Add(request, new Request());
        runtime.Recorder.Add(request, new RequestTarget(actionEntity));
        runtime.Recorder.Add(request, CharacterActionIds.DispatchRule);
        runtime.Recorder.Add(request, RuleTier.Normal);
        runtime.Recorder.Add(request, new AttackIntent());
        runtime.Recorder.Add(request, new AttackTargetQValue(q));
        runtime.Recorder.Add(request, new AttackTargetRValue(r));
        return request;
    }
}


