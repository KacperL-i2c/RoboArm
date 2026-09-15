namespace RoboArm.Protocol.Transport;

/// <summary>
/// Raw serial channel contract (CH340 virtual COM port). Implementations must never
/// raise <see cref="DataReceived"/> under an internal lock (IMotionTarget contract,
/// docs/04) and must tolerate Write from any thread while open.
/// </summary>
public interface ISerialChannel : IDisposable
{
    string PortName { get; }
    int Baud { get; }
    bool IsOpen { get; }

    event Action<byte[]>? DataReceived;
    event Action<Exception>? Error;

    void Open();
    void Close();
    void Write(ReadOnlySpan<byte> buffer);
}
