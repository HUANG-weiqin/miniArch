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

    private MiniArchRuntime(MiniArch.World world, bool useFastCommandBuffer)
    {
        World = world;
        PreRegisterComponentTypes();
        if (useFastCommandBuffer)
        {
            _fastCommands = new FastCommandBuffer(world);
            Recorder = _fastCommands;
        }
        else
        {
            _commands = new CommandBuffer(world);
            Recorder = _commands;
        }
        CurrentFrame = new FrameView(World);
    }

    public MiniArchRuntime() : this(new MiniArch.World(), false) { }

    public static MiniArchRuntime WithFastCommandBuffer() => new(new MiniArch.World(), true);

    public MiniArch.World World { get; }

    private readonly CommandBuffer? _commands;
    private readonly FastCommandBuffer? _fastCommands;
    public ICommandRecorder Recorder { get; }

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
        CurrentFrame = new FrameView(World);

        FrameContext context = new(Recorder, CurrentFrame);

        if (_systems.Count > 0)
        {
            RunSystems(context);
        }

        IsStable = !FlushPendingCommands();
        CurrentFrame = new FrameView(World);
    }

    private bool FlushPendingCommands()
    {
        if (_fastCommands is not null)
        {
            return _fastCommands.Submit();
        }
        return _commands!.CompileAndReplay();
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
            World.Components.GetOrCreate(type);
        }
    }
}


