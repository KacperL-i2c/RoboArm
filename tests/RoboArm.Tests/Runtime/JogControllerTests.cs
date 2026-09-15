using RoboArm.Runtime;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.Runtime;

public sealed class JogControllerTests
{
    private static (Harness Harness, JogController Jog) Create(bool autoTick = false)
    {
        var h = new Harness();
        var jog = new JogController(h.Engine, h.Config, h.Clock, autoTick: autoTick);
        return (h, jog);
    }

    private static void StepWithJog(Harness h, JogController jog, int ms)
    {
        for (var e = 0; e < ms; e += 10)
        {
            h.Clock.Advance(10);
            h.Sim.Tick();
            jog.Tick();
            h.Engine.Pump(h.Clock.NowMs);
        }
    }

    [Fact]
    public async Task Jog_Requires_Enabled_State()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;

        await h.Engine.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            jog.Start(0, 1);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Jog_Moves_Monotonically_Toward_Direction()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;
        await h.ConnectAndEnableAsync();

        jog.Start(0, 1); // positive
        StepWithJog(h, jog, 3000);

        var pos = h.Engine.MeasuredPositionsDeg[0];
        Assert.True(pos > 3, $"jog should move positive, got {pos}");
    }

    [Fact]
    public async Task Jog_Stops_Within_Soft_Limits()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;
        await h.ConnectAndEnableAsync();

        jog.Start(0, 1);
        StepWithJog(h, jog, 60000); // 60 s of virtual jogging toward +110

        var pos = h.Engine.MeasuredPositionsDeg[0];
        Assert.True(pos <= 110.5, $"jog crossed the soft limit: {pos}");
        Assert.InRange(pos, 108, 110.5); // it should have gotten there
    }

    [Fact]
    public async Task Jog_Stop_Releases_Axis_And_It_Settles()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;
        await h.ConnectAndEnableAsync();

        jog.Start(0, 1);
        StepWithJog(h, jog, 800);
        jog.Stop(0);
        StepWithJog(h, jog, 2000); // settle

        var settled = h.Engine.MeasuredPositionsDeg[0];
        StepWithJog(h, jog, 500);
        Assert.InRange(h.Engine.MeasuredPositionsDeg[0], settled - 1, settled + 1);
        Assert.False(jog.IsJogging(0));
    }

    [Fact]
    public async Task Jog_Aborts_Cleanly_When_Engine_Faults()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;
        await h.ConnectAndEnableAsync();

        jog.Start(0, 1);
        StepWithJog(h, jog, 200);
        h.Sim.InjectEStop();
        StepWithJog(h, jog, 200); // tick while Faulted: must not throw

        Assert.True(h.Engine.State == RuntimeState.Faulted);
    }

    [Fact]
    public async Task Jog_Cannot_Be_Started_While_Faulted()
    {
        var (h, jog) = Create();
        using var _ = h;
        using var __ = jog;
        await h.ConnectAndEnableAsync();
        h.Sim.InjectEStop();

        Assert.Throws<InvalidOperationException>(() => jog.Start(0, 1));
    }
}
