using Microsoft.Win32;

namespace RoboArm.Protocol.Transport;

public sealed record DiscoveredPort(string PortName, string DeviceId, string? Description);

/// <summary>
/// Finds CH340 virtual COM ports by scanning the USB device registry
/// (HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&amp;PID_yyyy → Device Parameters\PortName).
/// Registry access is abstracted so parsing is unit-testable.
/// </summary>
public sealed class Ch340Discovery(IPortSource? source = null)
{
    private readonly IPortSource _source = source ?? new RegistryPortSource();

    public IReadOnlyList<DiscoveredPort> List(int vendorId = NmotionCodec.UsbVendorId,
        int productId = NmotionCodec.UsbProductId)
    {
        var prefix = $"VID_{vendorId:X4}&PID_{productId:X4}";
        var result = new List<DiscoveredPort>();
        foreach (var (deviceId, portName, description) in _source.EnumerateUsbSerialPorts())
        {
            if (deviceId.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                result.Add(new DiscoveredPort(portName, deviceId, description));
        }
        return result;
    }
}

public interface IPortSource
{
    /// <summary>Yields (full USB device id like USB\VID_1A86&PID_7523\SERIAL, COM name, description).</summary>
    IEnumerable<(string DeviceId, string PortName, string? Description)> EnumerateUsbSerialPorts();
}

public sealed class RegistryPortSource : IPortSource
{
    public IEnumerable<(string, string, string?)> EnumerateUsbSerialPorts()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null)
            yield break;

        foreach (var vidPid in usb.GetSubKeyNames())
        {
            using var vidPidKey = usb.OpenSubKey(vidPid);
            if (vidPidKey is null)
                continue;
            foreach (var instance in vidPidKey.GetSubKeyNames())
            {
                using var instanceKey = vidPidKey.OpenSubKey(instance);
                if (instanceKey?.GetValue("Class") as string != "Ports")
                    continue;

                using var deviceParams = instanceKey.OpenSubKey("Device Parameters");
                var portName = deviceParams?.GetValue("PortName") as string;
                if (string.IsNullOrEmpty(portName))
                    continue;

                var description = instanceKey.GetValue("FriendlyName") as string
                    ?? instanceKey.GetValue("DeviceDesc") as string;
                yield return ($@"USB\{vidPid}\{instance}", portName!, description);
            }
        }
    }
}
