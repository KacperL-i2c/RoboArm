namespace RoboArm.Protocol.Capture;

public enum UsbTransferKind : byte
{
    Isochronous = 0,
    Interrupt = 1,
    Control = 2,
    Bulk = 3,
    Unknown = 0xFF,
}

/// <summary>A captured USB transfer (one USBPcap-captured packet, data direction by endpoint).</summary>
public sealed record CapturedTransfer(
    long TimestampMs,
    ushort Bus,
    ushort Device,
    byte Endpoint,
    UsbTransferKind Kind,
    byte[] Data)
{
    /// <summary>Host → device (OUT) when false; device → host (IN) when true.</summary>
    public bool IsDeviceToHost => (Endpoint & 0x80) != 0;
}

/// <summary>
/// Minimal pcapng reader + USBPcap transfer extraction. Only what Phase-0 analysis
/// needs: Enhanced Packet Blocks and the USBPcap packet header (26-byte base header,
/// data at headerLen for bulk/interrupt transfers). Link type 249 (USBPCAP) expected;
/// timestamps default to microseconds unless if_tsresol says otherwise.
/// </summary>
public static class Pcapng
{
    public const uint LinkTypeUsbPcap = 249;

    private const uint BlockTypeSectionHeader = 0x0A0D0D0A;
    private const uint BlockTypeInterfaceDescription = 0x00000001;
    private const uint BlockTypeEnhancedPacket = 0x00000006;

    public sealed record RawPacket(long TimestampMs, byte[] Data, uint InterfaceId);

    /// <summary>Reads all Enhanced Packet Blocks from a pcapng file.</summary>
    public static IReadOnlyList<RawPacket> ReadPackets(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var packets = new List<RawPacket>();
        var tsResolutions = new Dictionary<uint, double>(); // interface → seconds per tick

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var blockType = reader.ReadUInt32();
            var totalLength = reader.ReadUInt32();
            if (totalLength < 12)
                throw new InvalidDataException($"Malformed block at {reader.BaseStream.Position - 8}.");

            var bodyLength = totalLength - 12;
            switch (blockType)
            {
                case BlockTypeInterfaceDescription:
                {
                    var body = reader.ReadBytes((int)bodyLength);
                    var linkType = BitConverter.ToUInt16(body, 0);
                    if (linkType != LinkTypeUsbPcap)
                        throw new InvalidDataException(
                            $"Interface link type {linkType} is not USBPCAP ({LinkTypeUsbPcap}); " +
                            "capture the USBPcapN interface, not a network card.");
                    tsResolutions[(uint)tsResolutions.Count] = ParseTsResol(body);
                    break;
                }
                case BlockTypeEnhancedPacket:
                {
                    var body = reader.ReadBytes((int)bodyLength);
                    var interfaceId = BitConverter.ToUInt32(body, 0);
                    var tsHigh = BitConverter.ToUInt32(body, 4);
                    var tsLow = BitConverter.ToUInt32(body, 8);
                    var capturedLength = BitConverter.ToUInt32(body, 12);
                    var ticks = ((ulong)tsHigh << 32) | tsLow;
                    var secondsPerTick = tsResolutions.GetValueOrDefault(interfaceId, 1e-6);
                    var tsMs = (long)(ticks * secondsPerTick * 1000.0);
                    var data = new byte[capturedLength];
                    Array.Copy(body, 16, data, 0, (int)capturedLength);
                    packets.Add(new RawPacket(tsMs, data, interfaceId));
                    break;
                }
                default:
                    reader.BaseStream.Seek(bodyLength, SeekOrigin.Current);
                    break;
            }

            reader.BaseStream.Seek(4, SeekOrigin.Current); // trailing block length
        }

        return packets;
    }

    /// <summary>Decodes USBPcap packet headers into transfers (bulk/interrupt kept, control skipped).</summary>
    public static IReadOnlyList<CapturedTransfer> ExtractTransfers(IEnumerable<RawPacket> packets)
    {
        var transfers = new List<CapturedTransfer>();
        foreach (var packet in packets)
        {
            if (packet.Data.Length < 27)
                continue;

            var headerLen = BitConverter.ToUInt16(packet.Data, 0);
            var bus = BitConverter.ToUInt16(packet.Data, 16);
            var device = BitConverter.ToUInt16(packet.Data, 18);
            var endpoint = packet.Data[20];
            var transferKind = (UsbTransferKind)packet.Data[21];
            var dataLength = BitConverter.ToInt32(packet.Data, 22);

            if (transferKind is UsbTransferKind.Control)
                continue; // control transfers carry no stream data (CH340 enumeration etc.)

            var dataStart = headerLen;
            var available = Math.Min(dataLength, packet.Data.Length - dataStart);
            if (available <= 0)
            {
                transfers.Add(new CapturedTransfer(packet.TimestampMs, bus, device,
                    endpoint, transferKind, []));
                continue;
            }
            var data = new byte[available];
            Array.Copy(packet.Data, dataStart, data, 0, available);
            transfers.Add(new CapturedTransfer(packet.TimestampMs, bus, device,
                endpoint, transferKind, data));
        }
        return transfers;
    }

    /// <summary>Concatenated byte stream for one endpoint (order by timestamp).</summary>
    public static byte[] ConcatenatedStream(IEnumerable<CapturedTransfer> transfers, byte endpoint) =>
        transfers.Where(t => t.Endpoint == endpoint)
            .OrderBy(t => t.TimestampMs)
            .SelectMany(t => t.Data)
            .ToArray();

    /// <summary>Common-prefix comparison of two byte streams (field-diff aid).</summary>
    public static (int CommonPrefixLength, byte[] ANext, byte[] BNext) DiffStreams(
        byte[] a, byte[] b, int context = 16)
    {
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common])
            common++;
        var aNext = a[common..Math.Min(a.Length, common + context)];
        var bNext = b[common..Math.Min(b.Length, common + context)];
        return (common, aNext, bNext);
    }

    /// <summary>Inter-transfer gap histogram (1 ms buckets up to 50 ms, then coarse).</summary>
    public static IReadOnlyList<(string Bucket, int Count)> CadenceHistogram(
        IEnumerable<CapturedTransfer> transfers, byte endpoint)
    {
        var ordered = transfers.Where(t => t.Endpoint == endpoint && t.Data.Length > 0)
            .OrderBy(t => t.TimestampMs)
            .ToList();
        var buckets = new Dictionary<string, int>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].TimestampMs - ordered[i - 1].TimestampMs;
            var label = gap switch
            {
                <= 1 => "0-1 ms",
                <= 2 => "1-2 ms",
                <= 5 => "2-5 ms",
                <= 10 => "5-10 ms",
                <= 20 => "10-20 ms",
                <= 50 => "20-50 ms",
                <= 100 => "50-100 ms",
                <= 500 => "100-500 ms",
                _ => ">500 ms",
            };
            buckets[label] = buckets.GetValueOrDefault(label) + 1;
        }
        var order = new[] { "0-1 ms", "1-2 ms", "2-5 ms", "5-10 ms", "10-20 ms", "20-50 ms", "50-100 ms", "100-500 ms", ">500 ms" };
        return order.Where(buckets.ContainsKey).Select(b => (b, buckets[b])).ToList();
    }

    private static double ParseTsResol(byte[] interfaceBody)
    {
        // Options follow: linktype(2) + reserved(2) + snaplen(4) = 8 bytes header.
        var offset = 8;
        while (offset + 4 <= interfaceBody.Length)
        {
            var code = BitConverter.ToUInt16(interfaceBody, offset);
            var length = BitConverter.ToUInt16(interfaceBody, offset + 2);
            if (code == 0)
                break;
            if (code == 9 && length >= 1) // if_tsresol
            {
                var value = interfaceBody[offset + 4];
                return value < 128
                    ? Math.Pow(10, -value)
                    : Math.Pow(2, -(value & 0x7F));
            }
            offset += 4 + length + (length % 2); // options padded to 4 bytes
        }
        return 1e-6; // pcapng default: microseconds
    }
}
