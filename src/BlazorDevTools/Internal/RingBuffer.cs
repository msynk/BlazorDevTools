namespace BlazorDevTools.Internal;

/// <summary>Fixed-capacity FIFO buffer. Thread-safe; snapshots allocate one array.</summary>
internal sealed class RingBuffer<T>
{
    private readonly object _lock = new();
    private T[] _items;
    private int _head; // index of oldest
    private int _count;
    private long _version;

    public RingBuffer(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public long Version => Volatile.Read(ref _version);

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_count < _items.Length)
            {
                _items[(_head + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_head] = item;
                _head = (_head + 1) % _items.Length;
            }

            _version++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
            _version++;
        }
    }

    public T[] ToArray()
    {
        lock (_lock)
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(_head + i) % _items.Length];
            }

            return result;
        }
    }

    /// <summary>Returns the newest item or default when empty.</summary>
    public T? Last
    {
        get
        {
            lock (_lock)
            {
                return _count == 0 ? default : _items[(_head + _count - 1) % _items.Length];
            }
        }
    }

    /// <summary>
    /// Finds an item by a strictly increasing key (all our records carry monotonic ids) in O(log n).
    /// A linear scan here is a real cost: lookups happen on every completed HTTP request, JS call and render batch.
    /// </summary>
    public T? FindByKey(Func<T, long> key, long value)
    {
        lock (_lock)
        {
            var lo = 0;
            var hi = _count - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var item = _items[(_head + mid) % _items.Length];
                var current = key(item);
                if (current == value)
                {
                    return item;
                }

                if (current < value)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return default;
        }
    }

    /// <summary>Finds the newest item matching a predicate without allocating.</summary>
    public T? FindLast(Func<T, bool> predicate)
    {
        lock (_lock)
        {
            for (var i = _count - 1; i >= 0; i--)
            {
                var item = _items[(_head + i) % _items.Length];
                if (predicate(item))
                {
                    return item;
                }
            }

            return default;
        }
    }

    public void Resize(int capacity)
    {
        lock (_lock)
        {
            var snapshot = ToArrayUnsafe();
            _items = new T[Math.Max(1, capacity)];
            _head = 0;
            _count = Math.Min(snapshot.Length, _items.Length);
            Array.Copy(snapshot, snapshot.Length - _count, _items, 0, _count);
            _version++;
        }
    }

    private T[] ToArrayUnsafe()
    {
        var result = new T[_count];
        for (var i = 0; i < _count; i++)
        {
            result[i] = _items[(_head + i) % _items.Length];
        }

        return result;
    }
}
