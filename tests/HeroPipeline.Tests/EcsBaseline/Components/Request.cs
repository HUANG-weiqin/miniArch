namespace Hero.Ecs;

public readonly record struct Request;
public readonly record struct PendingRequest;
public readonly record struct Validated;
public readonly record struct Rejected;
public readonly record struct RequestTarget(MiniArch.Entity Target);


