using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;

namespace Hero.GameplayEcs.Characters.Attack;

public static class CharacterAttackBootstrap
{
    public static MiniArch.Entity CreateAttackRequest(MiniArchRuntime runtime, MiniArch.Entity actionEntity, int q, int r)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Commands.Create();
        runtime.Commands.Add(request, new Request());
        runtime.Commands.Add(request, new RequestTarget(actionEntity));
        runtime.Commands.Add(request, CharacterActionIds.DispatchRule);
        runtime.Commands.Add(request, RuleTier.Normal);
        runtime.Commands.Add(request, new AttackIntent());
        runtime.Commands.Add(request, new AttackTargetQValue(q));
        runtime.Commands.Add(request, new AttackTargetRValue(r));
        return request;
    }
}


