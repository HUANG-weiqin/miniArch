using System.Collections.Generic;

namespace MiniArch.Core;

internal struct InlineMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
{
    public int Count;
    public TKey Key0; public TValue Value0;
    public TKey Key1; public TValue Value1;
    public TKey Key2; public TValue Value2;
    public TKey Key3; public TValue Value3;

    public int OverflowHead;
    public int OverflowCount;

    public bool IsEmpty => Count == 0 && OverflowCount == 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool KeyEquals(TKey a, TKey b) => a.Equals(b);

    public void Set(TKey key, TValue value, ref OverflowPool<TKey, TValue> pool)
    {
        if (Count >= 1 && KeyEquals(Key0, key)) { Value0 = value; return; }
        if (Count >= 2 && KeyEquals(Key1, key)) { Value1 = value; return; }
        if (Count >= 3 && KeyEquals(Key2, key)) { Value2 = value; return; }
        if (Count >= 4 && KeyEquals(Key3, key)) { Value3 = value; return; }

        if (OverflowCount > 0)
        {
            var idx = pool.FindIndex(OverflowHead, key);
            if (idx >= 0)
            {
                pool.GetValue(idx) = value;
                return;
            }
        }

        switch (Count)
        {
            case 0: Key0 = key; Value0 = value; Count = 1; return;
            case 1: Key1 = key; Value1 = value; Count = 2; return;
            case 2: Key2 = key; Value2 = value; Count = 3; return;
            case 3: Key3 = key; Value3 = value; Count = 4; return;
            default:
                OverflowHead = pool.Add(key, value, OverflowCount > 0 ? OverflowHead : -1);
                OverflowCount++;
                return;
        }
    }

    public bool Remove(TKey key, ref OverflowPool<TKey, TValue> pool)
    {
        if (Count >= 1 && KeyEquals(Key0, key)) { RemoveAt(0); return true; }
        if (Count >= 2 && KeyEquals(Key1, key)) { RemoveAt(1); return true; }
        if (Count >= 3 && KeyEquals(Key2, key)) { RemoveAt(2); return true; }
        if (Count >= 4 && KeyEquals(Key3, key)) { RemoveAt(3); return true; }
        if (OverflowCount > 0 && pool.Remove(ref OverflowHead, key))
        {
            OverflowCount--;
            return true;
        }
        return false;
    }

    private void RemoveAt(int index)
    {
        var last = Count - 1;
        switch (index)
        {
            case 0:
                if (last >= 1) { Key0 = Key1; Value0 = Value1; }
                if (last >= 2) { Key1 = Key2; Value1 = Value2; }
                if (last >= 3) { Key2 = Key3; Value2 = Value3; }
                break;
            case 1:
                if (last >= 2) { Key1 = Key2; Value1 = Value2; }
                if (last >= 3) { Key2 = Key3; Value2 = Value3; }
                break;
            case 2:
                if (last >= 3) { Key2 = Key3; Value2 = Value3; }
                break;
        }
        Count = last;
    }

    public void CopyTo(List<(TKey, TValue)> target, ref OverflowPool<TKey, TValue> pool)
    {
        if (Count >= 1) target.Add((Key0, Value0));
        if (Count >= 2) target.Add((Key1, Value1));
        if (Count >= 3) target.Add((Key2, Value2));
        if (Count >= 4) target.Add((Key3, Value3));
        if (OverflowCount > 0)
        {
            for (var nodeIdx = OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx))
            {
                target.Add((pool.GetKeyReadonly(nodeIdx), pool.GetValueReadonly(nodeIdx)));
            }
        }
    }

    public void Clear()
    {
        Count = 0;
        OverflowHead = -1;
        OverflowCount = 0;
    }
}
