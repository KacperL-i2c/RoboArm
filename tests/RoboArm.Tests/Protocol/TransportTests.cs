using RoboArm.Motion;
using RoboArm.Protocol;
using RoboArm.Protocol.Transport;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.Protocol;

public sealed class FakeChannel : ISerialChannel
{
    public string PortName => "COMFAKE";
    public int Baud => NmotionCodec.BaudRate;
    public bool IsOpen { get; private set; }
    public List<byte[]> Written { get; } = [];

    public event Action<byte[]>? DataReceived;
    public event Action<Exception>? Error { add { } remove { } }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> buffer)
    {
        Written.Add(buffer.ToArray());
    }

    public void SimulateReceive(params byte[] data) => DataReceived?.Invoke(data);

    public void Dispose() => IsOpen = false;
}

public sealed class NmotionMotionTargetTests
{
    [Fact]
    public async Task Connect_Opens_The_Port()
    {
        using var channel = new FakeChannel();
        using var target = new NmotionMotionTarget(channel);

        await target.ConnectAsync();

        Assert.True(channel.IsOpen);
        Assert.True(target.IsOpen);
    }

    [Fact]
    public async Task Received_Bytes_Are_Logged_To_The_Ring()
    {
        using var channel = new FakeChannel();
        using var target = new NmotionMotionTarget(channel);
        await target.ConnectAsync();

        channel.SimulateReceive(0xAA, 0xBB);
        channel.SimulateReceive(0xCC);

        Assert.Equal(3, target.RxLog.TotalWritten);
        Assert.Equal([0xAA, 0xBB, 0xCC], target.RxLog.Snapshot());
    }

    [Fact]
    public async Task Motion_Calls_Never_Transmit_And_Never_Throw()
    {
        using var channel = new FakeChannel();
        using var target = new NmotionMotionTarget(channel);
        await target.ConnectAsync();

        await target.EnableAsync(true);
        await target.SendFrameAsync(new MotionFrame(0, [new AxisSetpoint(0, 10, 5)]));
        await target.SendFrameAsync(new MotionFrame(1, [new AxisSetpoint(0, 20, 5)]));
        await target.StopAsync(StopSeverity.SoftStop);

        Assert.Empty(channel.Written); // rule zero: no unknown bytes ever leave the PC
        Assert.Equal(1, target.EnableRequests);
        Assert.Equal(2, target.FramesDropped);
    }

    [Fact]
    public async Task Kill_Closes_The_Port_Strongest_PreProtocol_Stop()
    {
        using var channel = new FakeChannel();
        using var target = new NmotionMotionTarget(channel);
        await target.ConnectAsync();

        await target.StopAsync(StopSeverity.Kill);

        Assert.False(channel.IsOpen);
        Assert.Empty(channel.Written);
    }

    [Fact]
    public async Task Calls_After_Dispose_Throw()
    {
        using var channel = new FakeChannel();
        var target = new NmotionMotionTarget(channel);
        target.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => target.ConnectAsync());
    }
}

public sealed class Ch340DiscoveryTests
{
    private sealed class FakeSource : IPortSource
    {
        public IEnumerable<(string, string, string?)> EnumerateUsbSerialPorts() =>
        [
            (@"USB\VID_1A86&PID_7523\SER1234", "COM7", "USB-SERIAL CH340 (COM7)"),
            (@"USB\VID_0403&PID_6001\FTDI001", "COM5", "USB Serial Port (COM5)"),
            (@"USB\VID_1A86&PID_7523\SER9999", "COM12", "nMotion board (COM12)"),
        ];
    }

    [Fact]
    public void Filters_By_VidPid_And_Returns_All_Matches()
    {
        var ports = new Ch340Discovery(new FakeSource()).List();

        Assert.Equal(2, ports.Count);
        Assert.Equal("COM7", ports[0].PortName);
        Assert.Equal("COM12", ports[1].PortName);
        Assert.All(ports, p => Assert.Contains("VID_1A86&PID_7523", p.DeviceId, StringComparison.OrdinalIgnoreCase));
    }
}
