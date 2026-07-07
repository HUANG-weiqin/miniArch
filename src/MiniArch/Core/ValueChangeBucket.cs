using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Non-generic marker for the per-type bucket array on <see cref="World"/>.
/// </summary>
internal interface IValueChangeBucket
{
    bool HasSinks { get; }
    unsafe void DispatchRaw(Entity entity, Archetype archetype, int colIndex, int row, byte* source);
}

/// <summary>
/// Per-component-type bucket that holds weak references to value-change sinks
/// and dispatches typed old/new values on each semantic <c>Set&lt;T&gt;</c> write.
/// </summary>
internal sealed class ValueChangeBucket<T> : IValueChangeBucket where T : unmanaged
{
    private readonly List<WeakReference<IValueChangeSink<T>>> _sinks = new();

    public bool HasSinks => _sinks.Count > 0;

    public void Register(IValueChangeSink<T> sink)
    {
        _sinks.Add(new WeakReference<IValueChangeSink<T>>(sink));
    }

    public void Dispatch(Entity entity, Archetype archetype, in T oldValue, in T newValue)
    {
        for (var i = _sinks.Count - 1; i >= 0; i--)
        {
            if (_sinks[i].TryGetTarget(out var sink))
            {
                if (sink.Matches(archetype))
                    sink.OnValueChange(entity, oldValue, newValue);
            }
            else
            {
                _sinks[i] = _sinks[_sinks.Count - 1];
                _sinks.RemoveAt(_sinks.Count - 1);
            }
        }
    }

    unsafe void IValueChangeBucket.DispatchRaw(Entity entity, Archetype archetype, int colIndex, int row, byte* source)
    {
        if (!HasSinks) return;
        var old = archetype.GetComponentRefAt<T>(colIndex, row);
        var newVal = Unsafe.ReadUnaligned<T>(source);
        archetype.WriteComponentRaw(colIndex, row, source);
        Dispatch(entity, archetype, in old, in newVal);
    }
}

/// <summary>
/// Typed callback for value-change events. Implemented by <see cref="ChangeQuery{T}"/>
/// when configured with <c>WithPreviousValues</c>, and stored in a
/// <see cref="ValueChangeBucket{T}"/> on the <see cref="World"/>.
/// </summary>
internal interface IValueChangeSink<T> where T : unmanaged
{
    bool Matches(Archetype archetype);
    void OnValueChange(Entity entity, in T oldValue, in T newValue);
}
