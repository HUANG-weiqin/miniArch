using System;
using System.Collections.Generic;
using System.Linq;
using MiniArch.Core;

namespace Hero.Ecs;

public sealed class MiniArchRuntime
{
    private static readonly Type[] ComponentTypes = typeof(Request).Assembly
        .GetTypes()
        .Where(static type =>
            type.IsValueType &&
            !type.IsGenericTypeDefinition &&
            !type.IsNestedPrivate &&
            type.Namespace is not null &&
            type.Namespace.StartsWith("Hero.", StringComparison.Ordinal))
        .OrderBy(static type => type.FullName, StringComparer.Ordinal)
        .ToArray();

    private readonly List<ISystem> _systems = [];

    private MiniArchRuntime(MiniArch.World world)
    {
        World = world;
        PreRegisterComponentTypes();
        Commands = new CommandBuffer(world);
        Recorder = Commands;
        CurrentFrame = new FrameView(World);
    }

    public MiniArchRuntime() : this(new MiniArch.World()) { }

    public static MiniArchRuntime Create() => new(new MiniArch.World());

    public MiniArch.World World { get; }

    public CommandBuffer Commands { get; }
    public CommandBuffer Recorder { get; }

    public FrameView CurrentFrame { get; private set; }

    public bool IsStable { get; private set; } = true;

    public void AddSystem(ISystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _systems.Add(system);
    }

    public void Tick()
    {
        FlushPendingCommands();

        FrameContext context = new(Recorder, CurrentFrame);

        if (_systems.Count > 0)
        {
            RunSystems(context);
        }

        IsStable = !FlushPendingCommands();
    }

    private bool FlushPendingCommands()
    {
        return Commands.Submit();
    }

    private void RunSystems(FrameContext context)
    {
        foreach (ISystem system in _systems)
        {
            system.Execute(in context);
        }
    }

    private void PreRegisterComponentTypes()
    {
        foreach (Type type in ComponentTypes)
        {
            ComponentRegistry.Shared.GetOrCreate(type);
        }
    }
}
