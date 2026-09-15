using System.Globalization;
using RoboArm.Machine;
using RoboArm.Persistence;
using RoboArm.Poses;
using RoboArm.Programs;
using RoboArm.Protocol;
using RoboArm.Protocol.Capture;
using RoboArm.Protocol.Transport;
using RoboArm.Runtime;
using RoboArm.Safety;
using RoboArm.Simulator;
using RoboArm.Time;

var command = args.FirstOrDefault("help");

switch (command)
{
    case "sim":
        return await RunSimAsync();
    case "schemas":
        return ExportSchemas(args.ElementAtOrDefault(1) ?? "docs/schemas");
    case "usb":
        return Usb(args.Skip(1).ToList());
    case "capture":
        return Capture(args.Skip(1).ToList());
    case "board":
        Console.WriteLine("Expected board: CH340 USB-serial, " +
            $"{NmotionCodec.BaudRate} baud, VID/PID " +
            $"{NmotionCodec.UsbVendorId:X4}:{NmotionCodec.UsbProductId:X4}");
        Console.WriteLine("Plan: docs/02-protocol-plan.md | Captures: captures/");
        return 0;
    default:
        Console.WriteLine("""
            ReWorkbench — capture/replay tooling + simulator gate

            Usage:
              reworkbench sim       G1 gate: run a 3-move program in the simulator (live)
              reworkbench schemas   Export JSON schemas for config/poses/programs
              reworkbench usb list  List CH340 (nMotion) COM ports on this machine
              reworkbench capture scan <file.pcapng>
                                    List devices/endpoints/byte counts in a capture
              reworkbench capture stream <file.pcapng> [--endpoint 0x02] [--max 40]
                                    Dump transfers with timestamps as hex
              reworkbench capture cadence <file.pcapng> [--endpoint 0x02]
                                    Inter-transfer gap histogram
              reworkbench capture diff <a.pcapng> <b.pcapng> [--endpoint 0x02]
                                    First-divergence diff of concatenated TX streams
              reworkbench board     Show expected board parameters
            """);
        return command == "help" ? 0 : 1;
}

static int Usb(List<string> rest)
{
    switch (rest.FirstOrDefault("list"))
    {
        case "list":
        {
            var ports = new Ch340Discovery().List();
            if (ports.Count == 0)
            {
                Console.WriteLine("No CH340 (VID_1A86&PID_7523) COM ports found. Is the board plugged in?");
                return 1;
            }
            foreach (var port in ports)
                Console.WriteLine($"{port.PortName,-7} {port.DeviceId}  {port.Description}");
            return 0;
        }
        default:
            Console.WriteLine("Usage: reworkbench usb list");
            return 1;
    }
}

static int Capture(List<string> rest)
{
    var sub = rest.FirstOrDefault();
    var options = ParseOptions(rest);
    byte? endpointFilter = options.TryGetValue("endpoint", out var epValue)
        && epValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? byte.Parse(epValue[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : null;

    switch (sub)
    {
        case "scan":
        {
            var file = rest.ElementAtOrDefault(1);
            if (file is null) { Console.WriteLine("Usage: capture scan <file.pcapng>"); return 1; }
            var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(file));
            Console.WriteLine($"{transfers.Count} transfers total");
            foreach (var group in transfers.GroupBy(t => (t.Bus, t.Device, t.Endpoint, t.Kind))
                         .OrderByDescending(g => g.Sum(t => t.Data.Length)))
            {
                var bytes = group.Sum(t => t.Data.Length);
                var direction = (group.Key.Endpoint & 0x80) != 0 ? "IN  (board->PC)" : "OUT (PC->board)";
                Console.WriteLine(
                    $"bus {group.Key.Bus} dev {group.Key.Device} ep 0x{group.Key.Endpoint:X2} " +
                    $"{group.Key.Kind,-11} {direction,-14} {group.Count(),5} transfers {bytes,8} bytes");
            }
            return 0;
        }
        case "stream":
        {
            var file = rest.ElementAtOrDefault(1);
            if (file is null) { Console.WriteLine("Usage: capture stream <file.pcapng> [--endpoint 0x02] [--max N]"); return 1; }
            var max = options.TryGetValue("max", out var m) ? int.Parse(m, CultureInfo.InvariantCulture) : 40;
            var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(file))
                .Where(t => endpointFilter is null || t.Endpoint == endpointFilter)
                .OrderBy(t => t.TimestampMs)
                .Take(max);
            foreach (var t in transfers)
                Console.WriteLine($"[{t.TimestampMs,8} ms] ep 0x{t.Endpoint:X2} len {t.Data.Length,3}  {Convert.ToHexString(t.Data)}");
            return 0;
        }
        case "cadence":
        {
            var file = rest.ElementAtOrDefault(1);
            if (file is null) { Console.WriteLine("Usage: capture cadence <file.pcapng> [--endpoint 0x02]"); return 1; }
            var transfers = Pcapng.ExtractTransfers(Pcapng.ReadPackets(file));
            var ep = endpointFilter ?? FirstOutEndpoint(transfers);
            Console.WriteLine($"endpoint 0x{ep:X2}:");
            foreach (var (bucket, count) in Pcapng.CadenceHistogram(transfers, ep))
                Console.WriteLine($"  {bucket,-10} {count,6} {'#'.ToString().PadRight(Math.Min(count / 5 + 1, 60), '#')}");
            return 0;
        }
        case "diff":
        {
            var a = rest.ElementAtOrDefault(1);
            var b = rest.ElementAtOrDefault(2);
            if (a is null || b is null) { Console.WriteLine("Usage: capture diff <a.pcapng> <b.pcapng> [--endpoint 0x02]"); return 1; }
            var transfersA = Pcapng.ExtractTransfers(Pcapng.ReadPackets(a));
            var transfersB = Pcapng.ExtractTransfers(Pcapng.ReadPackets(b));
            var ep = endpointFilter ?? FirstOutEndpoint(transfersA);
            var streamA = Pcapng.ConcatenatedStream(transfersA, ep);
            var streamB = Pcapng.ConcatenatedStream(transfersB, ep);
            var (common, aNext, bNext) = Pcapng.DiffStreams(streamA, streamB);
            Console.WriteLine($"endpoint 0x{ep:X2}: A={streamA.Length} bytes, B={streamB.Length} bytes, common prefix={common}");
            Console.WriteLine($"A next: {Convert.ToHexString(aNext)}");
            Console.WriteLine($"B next: {Convert.ToHexString(bNext)}");
            return 0;
        }
        default:
            Console.WriteLine("Usage: capture scan|stream|cadence|diff ... (see help)");
            return 1;
    }
}

static Dictionary<string, string> ParseOptions(List<string> args) =>
    args.Select((value, index) => (value, index))
        .Where(t => t.value.StartsWith("--", StringComparison.Ordinal) && t.index + 1 < args.Count)
        .ToDictionary(t => t.value[2..], t => args[t.index + 1]);

static byte FirstOutEndpoint(IReadOnlyList<CapturedTransfer> transfers) =>
    transfers.FirstOrDefault(t => (t.Endpoint & 0x80) == 0 && t.Data.Length > 0)?.Endpoint
        ?? (byte)0x02;

static async Task<int> RunSimAsync()
{
    Console.WriteLine("=== G1 gate: 3-move program in Simulator ===");
    var config = MachineConfig.CreateDefault5Axis();
    Console.WriteLine($"Machine: {config.Name}, axes: {string.Join(", ", config.Axes.Select(a => $"{a.Name}[{a.SoftLimitMinDeg}..{a.SoftLimitMaxDeg}]"))}");

    using var sim = new SimulatedMotionTarget(config, tickHz: SimulatedMotionTarget.DefaultTickHz, autoTick: true);
    using var engine = new ExecutionEngine(config, sim, SystemWallClock.Instance,
        audit: new JsonlAuditSink(Path.Combine("data", "audit")), autoPump: true);

    engine.StateChanged += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] state -> {s}");
    engine.Faulted += (reason, detail) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] FAULT {reason}: {detail}");

    Console.WriteLine("connect + enable...");
    await engine.ConnectAsync();
    await engine.EnableAsync(true);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    await RunMoveAsync(engine, new Dictionary<int, double> { [0] = 30 }, "move 1: base -> 30°");
    await RunMoveAsync(engine, new Dictionary<int, double> { [3] = -45 }, "move 2: wrist -> -45°");
    await RunMoveAsync(engine, new Dictionary<int, double> { [0] = 10, [3] = 20 }, "move 3: base -> 10°, wrist -> 20°");
    sw.Stop();

    var measured = engine.MeasuredPositionsDeg;
    Console.WriteLine($"Final state: {engine.State}");
    foreach (var axis in config.Axes)
        Console.WriteLine($"  {axis.Name,-9} measured {measured[axis.Id],7:F2}°");
    Console.WriteLine($"3 moves completed in {sw.Elapsed.TotalSeconds:F2}s wall time (simulator).");
    Console.WriteLine(engine.State == RuntimeState.Enabled
        ? "G1 PASS: engine returned to Enabled with correct final positions."
        : "G1 FAIL: engine did not return to Enabled.");
    return engine.State == RuntimeState.Enabled ? 0 : 1;
}

static async Task RunMoveAsync(ExecutionEngine engine, Dictionary<int, double> targets, string label)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {label}");
    var done = new TaskCompletionSource();
    engine.MoveCompleted += OnDone;
    await engine.MoveToAsync(targets);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    engine.MoveCompleted -= OnDone;

    // Wait for the physics to converge onto the commanded targets.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(5))
    {
        var measured = engine.MeasuredPositionsDeg;
        if (targets.All(kv => Math.Abs(measured[kv.Key] - kv.Value) < 0.5))
            break;
        await Task.Delay(20);
    }
    return;
    void OnDone() => done.TrySetResult();
}

static int ExportSchemas(string dir)
{
    Directory.CreateDirectory(dir);
    var exports = new (string File, Func<string> Json)[]
    {
        ("machine-config.schema.json", () => SchemaExporter.Export<MachineConfig>("MachineConfig")),
        ("pose-library.schema.json", () => SchemaExporter.Export<PoseLibrary>("PoseLibrary")),
        ("robot-program.schema.json", () => SchemaExporter.Export<RobotProgram>("RobotProgram")),
    };
    foreach (var (file, json) in exports)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, json() + "\n");
        Console.WriteLine($"wrote {path}");
    }
    return 0;
}
