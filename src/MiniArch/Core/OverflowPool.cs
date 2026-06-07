using System.Buffers;
using System.Collections.Generic;

namespace MiniArch.Core;

internal struct OverflowPool<TKey, TValue> where TKey : unmanaged
{
    private TKey[] _keys;
    private TValue[] _values;
    private int[] _next;
    private int _count;

    public int Add(TKey key, TValue value, int currentHead)
    {
        if (_keys is null || _count == _keys.Length)
        {
            Grow();
        }

        int idx = _count++;
        _keys![idx] = key;
        _values![idx] = value;
        _next![idx] = currentHead;
        return idx;
    }

    public int FindIndex(int head, TKey key)
    {
        var comparer = EqualityComparer<TKey>.Default;
        for (var idx = head; idx >= 0; idx = _next[idx])
        {
            if (comparer.Equals(_keys[idx], key))
            {
                return idx;
            }
        }
        return -1;
    }

    public bool Remove(ref int head, TKey key)
    {
        var comparer = EqualityComparer<TKey>.Default;
        int prev = -1;
        for (var idx = head; idx >= 0; idx = _next[idx])
        {
            if (comparer.Equals(_keys[idx], key))
            {
                if (prev == -1)
                {
                    head = _next[idx];
                }
                else
                {
                    _next[prev] = _next[idx];
                }
                return true;
            }
            prev = idx;
        }
        return false;
    }

    public ref TValue GetValue(int index) => ref _values[index];
    public ref readonly TValue GetValueReadonly(int index) => ref _values[index];
    public ref readonly TKey GetKeyReadonly(int index) => ref _keys[index];
    public int GetNext(int index) => _next[index];

    public void Clear() => _count = 0;

    public void ReturnArrays()
    {
        if (_keys is not null)
        {
            ArrayPool<TKey>.Shared.Return(_keys, true);
            ArrayPool<TValue>.Shared.Return(_values, true);
            ArrayPool<int>.Shared.Return(_next, true);
            _keys = null!;
            _values = null!;
            _next = null!;
            _count = 0;
        }
    }

    private void Grow()
    {
        int newCapacity = _keys is null ? 8 : _keys.Length * 2;
        var newKeys = ArrayPool<TKey>.Shared.Rent(newCapacity);
        var newValues = ArrayPool<TValue>.Shared.Rent(newCapacity);
        var newNext = ArrayPool<int>.Shared.Rent(newCapacity);

        if (_keys is not null)
        {
            _keys.AsSpan(0, _count).CopyTo(newKeys.AsSpan(0, _count));
            _values.AsSpan(0, _count).CopyTo(newValues.AsSpan(0, _count));
            _next.AsSpan(0, _count).CopyTo(newNext.AsSpan(0, _count));

            ArrayPool<TKey>.Shared.Return(_keys);
            ArrayPool<TValue>.Shared.Return(_values);
            ArrayPool<int>.Shared.Return(_next);
        }

        _keys = newKeys;
        _values = newValues;
        _next = newNext;
    }
}
