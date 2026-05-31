using System.Collections.Generic;

namespace MiniArch.Core;

internal struct InlineMap<TKey, TValue>
    where TKey : unmanaged
{
    public int Count;
    public TKey Key0; public TValue Value0;
    public TKey Key1; public TValue Value1;
    public TKey Key2; public TValue Value2;
    public TKey Key3; public TValue Value3;
    public Dictionary<TKey, TValue>? Overflow;

    public bool Set(TKey key, TValue value)
    {
        if (Count >= 1 && EqualityComparer<TKey>.Default.Equals(Key0, key)) { Value0 = value; return false; }
        if (Count >= 2 && EqualityComparer<TKey>.Default.Equals(Key1, key)) { Value1 = value; return false; }
        if (Count >= 3 && EqualityComparer<TKey>.Default.Equals(Key2, key)) { Value2 = value; return false; }
        if (Count >= 4 && EqualityComparer<TKey>.Default.Equals(Key3, key)) { Value3 = value; return false; }

        switch (Count)
        {
            case 0: Key0 = key; Value0 = value; Count = 1; return false;
            case 1: Key1 = key; Value1 = value; Count = 2; return false;
            case 2: Key2 = key; Value2 = value; Count = 3; return false;
            case 3: Key3 = key; Value3 = value; Count = 4; return false;
            default:
                var allocated = Overflow is null;
                Overflow ??= new Dictionary<TKey, TValue>(4);
                Overflow[key] = value;
                return allocated;
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (Count >= 1 && EqualityComparer<TKey>.Default.Equals(Key0, key)) { value = Value0; return true; }
        if (Count >= 2 && EqualityComparer<TKey>.Default.Equals(Key1, key)) { value = Value1; return true; }
        if (Count >= 3 && EqualityComparer<TKey>.Default.Equals(Key2, key)) { value = Value2; return true; }
        if (Count >= 4 && EqualityComparer<TKey>.Default.Equals(Key3, key)) { value = Value3; return true; }
        if (Overflow is not null) return Overflow.TryGetValue(key, out value!);
        value = default!;
        return false;
    }

    public bool Remove(TKey key)
    {
        if (Count >= 1 && EqualityComparer<TKey>.Default.Equals(Key0, key)) { RemoveAt(0); return true; }
        if (Count >= 2 && EqualityComparer<TKey>.Default.Equals(Key1, key)) { RemoveAt(1); return true; }
        if (Count >= 3 && EqualityComparer<TKey>.Default.Equals(Key2, key)) { RemoveAt(2); return true; }
        if (Count >= 4 && EqualityComparer<TKey>.Default.Equals(Key3, key)) { RemoveAt(3); return true; }
        if (Overflow is not null) return Overflow.Remove(key);
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

    public void CopyTo(List<(TKey, TValue)> target)
    {
        if (Count >= 1) target.Add((Key0, Value0));
        if (Count >= 2) target.Add((Key1, Value1));
        if (Count >= 3) target.Add((Key2, Value2));
        if (Count >= 4) target.Add((Key3, Value3));
        if (Overflow is not null)
        {
            foreach (var kv in Overflow)
                target.Add((kv.Key, kv.Value));
        }
    }

    public void Clear()
    {
        Count = 0;
        Overflow?.Clear();
    }
}
