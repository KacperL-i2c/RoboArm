using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.Runtime;

/// <summary>
/// G2 fuzz gate (docs/05): random commands x random faults against the simulator.
/// Invariants: zero motion while Faulted, zero illegal FSM transitions,
/// every fault recoverable via the documented ack + re-home path.
/// Seeded for reproducibility; failures print the seed.
/// </summary>
public sealed class FuzzTests
{
    private const int Scenarios = 30;
    private const int StepsPerScenario = 200;
    private const int BaseSeed = 20260915;

    private static readonly (RuntimeState From, RuntimeState To)[] LegalWalk =
    [
        (RuntimeState.Offline, RuntimeState.Connected),
        (RuntimeState.Connected, RuntimeState.Offline),
        (RuntimeState.Connected, RuntimeState.Enabled),
        (RuntimeState.Enabled, RuntimeState.Connected),
        (RuntimeState.Enabled, RuntimeState.Executing),
        (RuntimeState.Executing, RuntimeState.Enabled),
        (RuntimeState.Executing, RuntimeState.Paused),
        (RuntimeState.Paused, RuntimeState.Executing),
        (RuntimeState.Paused, RuntimeState.Enabled),
        (RuntimeState.Faulted, RuntimeState.Connected),
        // faults latch from any state (docs/03)
        (RuntimeState.Offline, RuntimeState.Faulted),
        (RuntimeState.Connected, RuntimeState.Faulted),
        (RuntimeState.Enabled, RuntimeState.Faulted),
        (RuntimeState.Executing, RuntimeState.Faulted),
        (RuntimeState.Paused, RuntimeState.Faulted),
    ];

    [Fact]
    public async Task Fuzz_Random_Commands_And_Faults_Hold_All_Invariants()
    {
        var stats = new FuzzStats();
        for (var scenario = 0; scenario < Scenarios; scenario++)
        {
            var seed = BaseSeed + scenario;
            await RunScenarioAsync(seed, stats);
        }

        Assert.True(stats.FaultsObserved > 0,
            "fuzzer never produced a fault — increase scenario coverage");
        Assert.Equal(0, stats.FramesWhileFaulted);
        Assert.Equal(0, stats.PositionsOutsideLimits);
        Assert.Equal(stats.FaultsObserved, stats.Recoveries);
        Assert.Equal(0, stats.UnrecoveredAtEnd);
    }

    private static async Task RunScenarioAsync(int seed, FuzzStats stats)
    {
        using var h = new Harness();
        var rng = new Random(seed);
        h.Recording.OnFrame += _ => { };
        var walkStart = 0;

        for (var step = 0; step < StepsPerScenario; step++)
        {
            var stateBefore = h.Engine.State;
            var framesBefore = h.Recording.TotalFrames;

            await DoActionAsync(h, rng, stats);
            h.Step(rng.Next(1, 10) * 10);

            // Invariant 2: no frame may be dispatched while Faulted.
            if (stateBefore == RuntimeState.Faulted
                && h.Recording.TotalFrames > framesBefore)
            {
                stats.FramesWhileFaulted++;
            }

            // Invariant 1: FSM walk stays legal.
            for (var i = walkStart; i < h.StateLog.Count - 1; i++)
            {
                var edge = (h.StateLog[i], h.StateLog[i + 1]);
                if (!LegalWalk.Contains(edge))
                {
                    var log = string.Join(">", h.StateLog.Take(Math.Max(0, i - 3)).Take(8));
                    Assert.Fail($"seed={seed} step={step}: illegal edge {edge.Item1}->{edge.Item2}; context {log}");
                }
            }
            walkStart = Math.Max(0, h.StateLog.Count - 1);

            // Invariant 3: physics never leaves the soft-limit envelope.
            foreach (var axis in h.Config.Axes)
            {
                var pos = h.Sim.EstimatedPositionDeg(axis.Id);
                if (pos < axis.SoftLimitMinDeg - 2 || pos > axis.SoftLimitMaxDeg + 2)
                    stats.PositionsOutsideLimits++;
            }

            // Fault bookkeeping + Invariant 4: every fault is recoverable.
            if (stateBefore != RuntimeState.Faulted && h.Engine.State == RuntimeState.Faulted)
            {
                stats.FaultsObserved++;
                await RecoverAsync(h, stats);
            }
        }

        if (h.Engine.State == RuntimeState.Faulted)
        {
            stats.UnrecoveredAtEnd++;
            await RecoverAsync(h, stats);
        }
    }

    private static async Task RecoverAsync(Harness h, FuzzStats stats)
    {
        h.Sim.ClearFaults(); // operator power-cycles the board
        await h.Sim.ConnectAsync(); // power cycle ends Offline
        try
        {
            await h.Engine.AcknowledgeFaultAsync();
            await h.Engine.MarkHomedAsync();
            if (h.Engine.State == RuntimeState.Connected)
            {
                await h.Engine.EnableAsync(true);
                if (h.Engine.State == RuntimeState.Enabled)
                    stats.Recoveries++;
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // fall through: counted as unrecovered below
        }
        stats.RecoveryFailures++;
    }

    private static async Task DoActionAsync(Harness h, Random rng, FuzzStats stats)
    {
        stats.Actions++;
        try
        {
            switch (rng.Next(0, 14))
            {
                case 0:
                case 1:
                case 2:
                    if (h.Engine.State == RuntimeState.Enabled)
                        await h.Engine.MoveToAsync(RandomTargets(h, rng));
                    stats.Moves++;
                    break;
                case 3:
                    await h.Engine.StopAsync((StopSeverity)rng.Next(0, 3));
                    stats.Stops++;
                    break;
                case 4:
                    await h.Engine.PauseAsync();
                    break;
                case 5:
                    await h.Engine.ResumeAsync();
                    break;
                case 6:
                    if (h.Engine.State == RuntimeState.Offline)
                        await h.Engine.ConnectAsync();
                    break;
                case 7:
                    if (h.Engine.State == RuntimeState.Connected)
                        await h.Engine.EnableAsync(true);
                    else if (h.Engine.State == RuntimeState.Enabled)
                        await h.Engine.EnableAsync(false);
                    break;
                case 8:
                    h.Engine.SpeedOverride = rng.Next(1, 11) / 10.0;
                    break;
                case 9:
                    if (h.Engine.State != RuntimeState.Faulted)
                        h.Sim.InjectEStop();
                    break;
                case 10:
                    h.Sim.InjectCommLoss(TimeSpan.FromMilliseconds(rng.Next(50, 400)));
                    break;
                case 11:
                    InjectBoundedDivergence(h, rng);
                    break;
                case 12:
                    h.Sim.InjectFrameDelay(rng.Next(0, 30));
                    break;
                case 13:
                    if (h.Engine.State == RuntimeState.Connected)
                        await h.Engine.DisconnectAsync();
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            stats.Rejected++; // illegal command properly rejected by the FSM
        }
    }

    /// <summary>Divergence injection that cannot itself push the estimate outside the soft-limit envelope.</summary>
    private static void InjectBoundedDivergence(Harness h, Random rng)
    {
        var axisId = rng.Next(0, 2);
        var axis = h.Config.Axis(axisId)!;
        var pos = h.Sim.EstimatedPositionDeg(axisId);
        var requested = rng.Next(-30, 30);
        var bounded = Math.Clamp(pos + requested, axis.SoftLimitMinDeg + 1, axis.SoftLimitMaxDeg - 1) - pos;
        if (Math.Abs(bounded) > 0.5)
            h.Sim.InjectDivergence(axisId, bounded);
    }

    private static Dictionary<int, double> RandomTargets(Harness h, Random rng)
    {
        var result = new Dictionary<int, double>();
        foreach (var axis in h.Config.Axes)
        {
            if (rng.Next(0, 2) == 0)
                result[axis.Id] = rng.Next((int)axis.SoftLimitMinDeg + 2, (int)axis.SoftLimitMaxDeg - 1);
        }
        if (result.Count == 0)
            result[h.Config.Axes[0].Id] = rng.Next(-50, 50);
        return result;
    }

    private sealed class FuzzStats
    {
        public long Actions;
        public long Moves;
        public long Stops;
        public long Rejected;
        public long FaultsObserved;
        public long Recoveries;
        public long RecoveryFailures;
        public long FramesWhileFaulted;
        public long PositionsOutsideLimits;
        public long UnrecoveredAtEnd;
    }
}
