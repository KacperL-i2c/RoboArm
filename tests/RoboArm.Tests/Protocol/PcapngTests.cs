using System.Buffers.Binary;
using RoboArm.Protocol.Capture;
using Xunit;

namespace RoboArm.Tests.Protocol;

/// <summary>Builds real pcapng bytes with USBPcap-style packets — golden fixtures for the parser.</summary>
public sealed class PcapngBuilder
{
    private readonly List<byte[]> _blocks = [];
    private readonly List<(long TsUs, byte[] Packet)> _packets = [];
    private int _interfaceCount;

    public PcapngBuilder AddInterface(uint linkType = Pcapng.LinkTypeUsbPcap)
    {
        _interfaceCount++;
        var body = new byte[8]; // linktype(2) + reserved(2) + snaplen(4)
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)linkType);
        _blocks.Add(Block(0x00000001, body));
        return this;
    }

    public PcapngBuilder AddUsbTransfer(long tsUs, byte endpoint, UsbTransferKind kind,
        ushort bus, ushort device, params byte[] data)
    {
        const ushort headerLen = 26;
        var packet = new byte[headerLen + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0), headerLen);
        // irpId (8 bytes) left zero
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), bus);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18), device);
        packet[20] = endpoint;
        packet[21] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(22), data.Length);
        data.CopyTo(packet, headerLen);
        _packets.Add((tsUs, packet));
        return this;
    }

    public byte[] ToBytes()
    {
        var result = new List<byte[]>(_blocks);
        foreach (var (tsUs, packet) in _packets)
            result.Add(EnhancedPacketBlock((uint)_packets.IndexOf((tsUs, packet)), tsUs, packet));
        var file = new MemoryStream();
        foreach (var block in result.Concat([SectionHeader()]))
            file.Write(block);
        return file.ToArray();
    }

    private static byte[] SectionHeader()
    {
        var body = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(body, -1); // byte-order magic
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1); // major
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0); // minor
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 8); // section length: unset
        return Block(0x0A0D0D0A, body);
    }

    private static byte[] EnhancedPacketBlock(uint interfaceId, long tsUs, byte[] data)
    {
        var body = new byte[16 + data.Length + Padding(data.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(tsUs >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)(tsUs & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)data.Length);
        data.CopyTo(body, 16);
        return Block(0x00000006, body);
    }

    private static int Padding(int length) => (4 - length % 4) % 4;

    private static byte[] Block(uint type, byte[] body)
    {
        var block = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, type);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)block.Length);
        body.CopyTo(block, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8 + body.Length), (uint)block.Length);
        return block;
    }
}

public sealed class PcapngTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), "roboarm-tests", $"{Guid.NewGuid():N}.pcapng");

    private string Write(PcapngBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, builder.ToBytes());
        return _file;
    }

    [Fact]
    public void Reads_Transfers_With_Endpoint_Direction_And_Timestamps()
    {
        var path = Write(new PcapngBuilder()
            .AddInterface()
            .AddUsbTransfer(1_000, 0x02, UsbTransferKind.Bulk, bus: 1, device: 7, 0xAA, 0xBB, 0xCC)
            .AddUsbTransfer(2_500, 0x82, UsbTransferKind.Bulk, bus: 1, device: 7, 0x01)
            .AddUsbTransfer(3_000, 0x02, UsbTransferKind.Bulk, bus: 1, device: 7, 0xDD));

        var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(path));

        Assert.Equal(3, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(UsbTransferKind.Bulk, t.Kind));
        Assert.All(transfers, t => Assert.Equal(1, t.Bus));
        Assert.All(transfers, t => Assert.Equal(7, t.Device));

        var out0 = transfers[0];
        Assert.Equal(0x02, out0.Endpoint);
        Assert.False(out0.IsDeviceToHost);
        Assert.Equal([0xAA, 0xBB, 0xCC], out0.Data);
        Assert.Equal(1, out0.TimestampMs);

        var in1 = transfers[1];
        Assert.Equal(0x82, in1.Endpoint);
        Assert.True(in1.IsDeviceToHost);
        Assert.Equal([0x01], in1.Data);
        Assert.Equal(3, transfers[2].TimestampMs); // 3000 µs → 3 ms
    }

    [Fact]
    public void Control_Transfers_Are_Skipped()
    {
        var path = Write(new PcapngBuilder()
            .AddInterface()
            .AddUsbTransfer(0, 0x00, UsbTransferKind.Control, 1, 7, 0x80, 0x06, 0x00)
            .AddUsbTransfer(10_000, 0x02, UsbTransferKind.Bulk, 1, 7, 0xFF));

        var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(path));

        Assert.Single(transfers);
        Assert.Equal(UsbTransferKind.Bulk, transfers[0].Kind);
    }

    [Fact]
    public void Concatenated_Stream_Preserves_Timestamp_Order()
    {
        var path = Write(new PcapngBuilder()
            .AddInterface()
            .AddUsbTransfer(5_000, 0x02, UsbTransferKind.Bulk, 1, 7, 0x02)
            .AddUsbTransfer(1_000, 0x02, UsbTransferKind.Bulk, 1, 7, 0x01)
            .AddUsbTransfer(3_000, 0x82, UsbTransferKind.Bulk, 1, 7, 0x99));

        var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(path));

        Assert.Equal([0x01, 0x02], Pcapng.ConcatenatedStream(transfers, 0x02));
        Assert.Equal([0x99], Pcapng.ConcatenatedStream(transfers, 0x82));
    }

    [Fact]
    public void DiffStreams_Finds_First_Divergence()
    {
        var a = new byte[] { 1, 2, 3, 4, 5, 6 };
        var b = new byte[] { 1, 2, 3, 9, 9 };
        var (common, aNext, bNext) = Pcapng.DiffStreams(a, b);

        Assert.Equal(3, common);
        Assert.Equal([4, 5, 6], aNext);
        Assert.Equal([9, 9], bNext);
    }

    [Fact]
    public void Cadence_Histogram_Buckets_Gaps()
    {
        var path = Write(new PcapngBuilder()
            .AddInterface()
            .AddUsbTransfer(0, 0x02, UsbTransferKind.Bulk, 1, 7, 1)
            .AddUsbTransfer(10_000, 0x02, UsbTransferKind.Bulk, 1, 7, 2)
            .AddUsbTransfer(20_000, 0x02, UsbTransferKind.Bulk, 1, 7, 3)
            .AddUsbTransfer(80_000, 0x02, UsbTransferKind.Bulk, 1, 7, 4));

        var histogram = Pcapng.CadenceHistogram(
            Pcapng.ExtractTransfers(Pcapng.ReadPackets(path)), 0x02);

        Assert.Contains(("5-10 ms", 2), histogram);
        Assert.Contains(("50-100 ms", 1), histogram);
    }

    [Fact]
    public void Non_UsbPcap_LinkType_Is_Rejected_With_Clear_Error()
    {
        var path = Write(new PcapngBuilder().AddInterface(linkType: 1)); // Ethernet
        Assert.Throws<InvalidDataException>(() => Pcapng.ReadPackets(path));
    }

    public void Dispose()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }
}
