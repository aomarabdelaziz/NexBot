using System.Collections;

namespace NexBot.Game;

/// <summary>
/// A slot-indexed list with a capacity (inventory, storage, pet inventory ...). Empty slots are <c>null</c>.
/// </summary>
/// <remarks>Replaces the legacy <c>xList&lt;T&gt;</c>. Writing past the capacity grows the list.</remarks>
public sealed class SlotList<T> : IReadOnlyList<T?> where T : class
{
    private readonly List<T?> _slots;
    private int _occupied;

    public SlotList() => _slots = [];

    public SlotList(int capacity)
    {
        _slots = new List<T?>(capacity);
        for (int i = 0; i < capacity; i++)
            _slots.Add(null);
    }

    /// <summary>Number of slots.</summary>
    public int Capacity => _slots.Count;

    /// <summary>Number of non-empty slots.</summary>
    public int Count => _occupied;

    /// <summary>Gets the item in a slot (<c>null</c> if empty or out of range) or sets it.</summary>
    public T? this[int index]
    {
        get => (uint)index < (uint)_slots.Count ? _slots[index] : null;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            while (index >= _slots.Count)
                _slots.Add(null);

            var current = _slots[index];
            if (value is null && current is not null) _occupied--;
            else if (value is not null && current is null) _occupied++;
            _slots[index] = value;
        }
    }

    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        for (int i = _slots.Count - 1; i >= capacity; i--)
        {
            if (_slots[i] is not null) _occupied--;
            _slots.RemoveAt(i);
        }
        while (_slots.Count < capacity)
            _slots.Add(null);
    }

    public bool Exists(Predicate<T?> match) => _slots.Exists(match);

    public T? Find(Predicate<T?> match) => _slots.Find(match);

    /// <summary>First slot index from <paramref name="startIndex"/> to <paramref name="endIndex"/> (inclusive) matching, or -1.</summary>
    public int FindIndex(Predicate<T?> match, int startIndex = 0, int endIndex = int.MaxValue)
    {
        int last = Math.Min(endIndex, _slots.Count - 1);
        for (int i = Math.Max(0, startIndex); i <= last; i++)
            if (match(_slots[i]))
                return i;
        return -1;
    }

    /// <summary>First empty slot at or after <paramref name="startIndex"/>, or -1.</summary>
    public int FindEmptySlot(int startIndex = 0) => FindIndex(static i => i is null, startIndex);

    int IReadOnlyCollection<T?>.Count => _slots.Count;

    public IEnumerator<T?> GetEnumerator() => _slots.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
