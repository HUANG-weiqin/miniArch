using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CoreCommandBuffer = MiniArch.Core.ICommandRecorder;

namespace Hero.Ecs;

public sealed class ModifierApplySystem : ISystem
{
    private readonly IReadOnlyDictionary<SlotKey, IIntSlotPort> _ports;
    private readonly Dictionary<ModifierBucketKey, ModifierBucket> _buckets = new();
    private static readonly MiniArch.QueryDescription RequestQueryDescription = new MiniArch.QueryDescription()
        .With<Request>()
        .With<Validated>()
        .With<RequestTarget>()
        .With<ModifierSlot>()
        .Without<Rejected>();

    public ModifierApplySystem(IReadOnlyDictionary<SlotKey, IIntSlotPort> ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;
        FrameView frame = context.Frame;
        Dictionary<ModifierBucketKey, ModifierBucket> buckets = _buckets;

        buckets.Clear();

        foreach (MiniArch.Entity entity in frame.Each(RequestQueryDescription))
        {
            RequestTarget requestTarget = frame.Get<RequestTarget>(entity);
            ModifierSlot modifierSlot = frame.Get<ModifierSlot>(entity);

            if (!_ports.TryGetValue(modifierSlot.Slot, out IIntSlotPort? port))
            {
                throw new InvalidOperationException(
                    $"No slot port is registered for slot '{modifierSlot.Slot.Value}'.");
            }

            ModifierBucketKey bucketKey = new(requestTarget.Target, modifierSlot.Slot);
            if (!buckets.ContainsKey(bucketKey))
            {
                buckets[bucketKey] = new ModifierBucket(requestTarget.Target, port);
            }

            ref ModifierBucket bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, bucketKey, out _);

            if (frame.TryGet(entity, out SetModifier setModifier))
            {
                bucket.AddSet(setModifier.Value);
            }

            if (frame.TryGet(entity, out DeltaModifier deltaModifier))
            {
                bucket.AddDelta(deltaModifier.Value);
            }

            commands.Destroy(entity);
        }

        foreach (var kvp in buckets)
        {
            ModifierBucket bucket = kvp.Value;
            int current = 0;
            bool hasCurrent = bucket.Port.TryRead(frame, bucket.Target, out current);
            if (!hasCurrent && !bucket.HasSet)
            {
                throw new InvalidOperationException(
                    "Current slot value is required when SetModifier is absent.");
            }

            int next = bucket.HasSet ? bucket.SetValue + bucket.TotalDelta : checked(current + bucket.TotalDelta);
            bucket.Port.Write(commands, bucket.Target, next);
        }
    }

    private readonly record struct ModifierBucketKey(MiniArch.Entity Target, SlotKey Slot);

    private struct ModifierBucket
    {
        public ModifierBucket(MiniArch.Entity target, IIntSlotPort port)
        {
            Target = target;
            Port = port;
        }

        public MiniArch.Entity Target { get; }

        public IIntSlotPort Port { get; }

        public bool HasSet { get; private set; }

        public int SetValue { get; private set; }

        public int TotalDelta { get; private set; }

        public void AddSet(int value)
        {
            if (HasSet && SetValue != value)
            {
                throw new InvalidOperationException("Multiple SetModifier values are not allowed for the same target slot.");
            }

            HasSet = true;
            SetValue = value;
        }

        public void AddDelta(int value) => TotalDelta += value;
    }
}


