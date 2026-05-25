using System;
using Hero.Ecs;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Movement;

namespace Hero.GameplayEcs.TurnSerial;

public static class TurnSerialBootstrap
{
    public static MiniArch.Entity CreateContext(MiniArchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity context = runtime.Commands.Create();
        runtime.Commands.Add(context, new TurnSerialContext());
        runtime.Commands.Add(context, new TurnRoundIndex(0));
        return context;
    }

    public static void Activate(MiniArchRuntime runtime, MiniArch.Entity context, MiniArch.Entity character)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        runtime.Commands.Set(context, new ActiveTurnCharacter(character));
        runtime.Commands.Set(context, new NextTurnCharacter(character));
    }

    public static MiniArch.Entity SubmitEndTurn(MiniArchRuntime runtime, MiniArch.Entity character)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Commands.Create();
        runtime.Commands.Add(request, new Request());
        runtime.Commands.Add(request, new RequestTarget(character));
        runtime.Commands.Add(request, TurnSerialIds.EndTurnRule);
        runtime.Commands.Add(request, RuleTier.Normal);
        return request;
    }

    public static MiniArch.Entity SubmitCardPlay(MiniArchRuntime runtime, MiniArch.Entity card)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        MiniArch.Entity request = runtime.Commands.Create();
        runtime.Commands.Add(request, new Request());
        runtime.Commands.Add(request, new RequestTarget(card));
        runtime.Commands.Add(request, TurnSerialIds.CardPlayRule);
        runtime.Commands.Add(request, RuleTier.Normal);
        return request;
    }

    public static MiniArch.Entity SubmitMoveCardPlay(MiniArchRuntime runtime, MiniArch.Entity card, int dq, int dr)
    {
        MiniArch.Entity request = SubmitCardPlay(runtime, card);
        runtime.Commands.Add(request, new MoveIntent());
        runtime.Commands.Add(request, new MoveDqValue(dq));
        runtime.Commands.Add(request, new MoveDrValue(dr));
        return request;
    }

    public static MiniArch.Entity SubmitAttackCardPlay(MiniArchRuntime runtime, MiniArch.Entity card, int q, int r)
    {
        MiniArch.Entity request = SubmitCardPlay(runtime, card);
        runtime.Commands.Add(request, new AttackIntent());
        runtime.Commands.Add(request, new AttackTargetQValue(q));
        runtime.Commands.Add(request, new AttackTargetRValue(r));
        return request;
    }
}
