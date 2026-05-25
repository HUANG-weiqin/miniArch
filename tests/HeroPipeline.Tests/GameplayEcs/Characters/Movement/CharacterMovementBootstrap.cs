using System;
using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Movement;

public static class CharacterMovementBootstrap
{
    public static MiniArch.Entity CreateMoveRequest(MiniArchRuntime runtime, MiniArch.Entity target, int dq, int dr)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Commands.Create();
        runtime.Commands.Add(request, new Request());
        runtime.Commands.Add(request, new RequestTarget(target));
        runtime.Commands.Add(request, CharacterMovementIds.MoveRule);
        runtime.Commands.Add(request, RuleTier.Normal);
        runtime.Commands.Add(request, new MoveIntent());
        runtime.Commands.Add(request, new MoveDqValue(dq));
        runtime.Commands.Add(request, new MoveDrValue(dr));
        return request;
    }
}


