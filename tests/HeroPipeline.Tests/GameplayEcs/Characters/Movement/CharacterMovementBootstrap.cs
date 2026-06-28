using System;
using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Movement;

public static class CharacterMovementBootstrap
{
    public static MiniArch.Entity CreateMoveRequest(MiniArchRuntime runtime, MiniArch.Entity target, int dq, int dr)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Recorder.CreateImmediate();
        runtime.Recorder.Add(request, new Request());
        runtime.Recorder.Add(request, new RequestTarget(target));
        runtime.Recorder.Add(request, CharacterMovementIds.MoveRule);
        runtime.Recorder.Add(request, RuleTier.Normal);
        runtime.Recorder.Add(request, new MoveIntent());
        runtime.Recorder.Add(request, new MoveDqValue(dq));
        runtime.Recorder.Add(request, new MoveDrValue(dr));
        return request;
    }
}


