using System.IO.Ports;

namespace RoboArm.Protocol.Transport;

/// <summary>
/// Serial channel over a CH340 virtual COM port. A dedicated read loop pumps
/// <see cref="SerialPort.BaseStream"/> into <see cref="ISerialChannel.DataReceived"/>
/// (raised without internal locks). Write is synchronous with a bounded timeout.
/// </summary>
public sealed class SerialPortChannel : ISerialChannel
{
    private readonly SerialPort _port;
    private readonly object _gate = new();
    private CancellationTokenSource? _readCts;

    public string PortName { get; }
    public int Baud { get; }
    public bool IsOpen
    {
        get { lock (_gate) return _port.IsOpen; }
    }

    public event Action<byte[]>? DataReceived;
    public event Action<Exception>? Error;

    public SerialPortChannel(string portName, int baud = NmotionCodec.BaudRate)
    {
        PortName = portName;
        Baud = baud;
        _port = new SerialPort(portName, baud)
        {
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 16 * 1024,
            WriteTimeout = 500,
            DtrEnable = false,
            RtsEnable = false,
        };
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_port.IsOpen)
                return;
            _port.Open();
            _port.DiscardInBuffer();
            _readCts = new CancellationTokenSource();
            var token = _readCts.Token;
            _ = Task.Run(() => ReadLoopAsync(token));
        }
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _readCts;
            _readCts = null;
        }
        cts?.Cancel();
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch (IOException)
        {
            // port already vanished (USB yank) — close is best-effort
        }
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            if (!_port.IsOpen)
                throw new InvalidOperationException($"Serial port {PortName} is not open.");
            var bytes = buffer.ToArray();
            _port.Write(bytes, 0, bytes.Length);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _port.IsOpen)
            {
                var read = await _port.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                    continue;
                var data = new byte[read];
                Array.Copy(buffer, data, read);
                DataReceived?.Invoke(data); // raised without any lock held
            }
        }
        catch (OperationCanceledException)
        {
            // normal close
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}
