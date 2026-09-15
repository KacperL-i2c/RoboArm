namespace RoboArm.Protocol.Transport;

/// <summary>
/// Fixed-size, thread-safe byte ring used to keep the last N bytes of TX/RX traffic
/// for crash diagnosis (docs/03 L5). Allocation-free in steady state.
/// </summary>
public sealed class RingLog(int capacity = 64 * 1024)
{
    private readonly byte[] _buffer = new byte[capacity];
    private readonly object _gate = new();
    private int _start;
    private int _count;
    private long _totalWritten;

    public long TotalWritten
    {
        get { lock (_gate) return _totalWritten; }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _totalWritten += data.Length;
            foreach (var b in data)
            {
                _buffer[(_start + _count) % capacity] = b;
                if (_count < capacity)
                    _count++;
                else
                    _start = (_start + 1) % capacity;
            }
        }
    }

    /// <summary>Newest-last copy of the retained bytes.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            var result = new byte[_count];
            for (var i = 0; i < _count; i++)
                result[i] = _buffer[(_start + i) % capacity];
            return result;
        }
    }

    public string DumpHex(int maxBytes = 512)
    {
        var snapshot = Snapshot();
        var take = Math.Min(maxBytes, snapshot.Length);
        return Convert.ToHexString(snapshot[^take..]);
    }
}
