using RoboArm.App.ViewModels;
using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.App;

public sealed class ArmViewModelTests
{
    private static MachineConfig Config(double gripperMin = 0, double gripperMax = 60) => new(
        "arm",
        [
            new AxisConfig(0, "base", SoftLimitMinDeg: -170, SoftLimitMaxDeg: 170),
            new AxisConfig(1, "shoulder", SoftLimitMinDeg: -100, SoftLimitMaxDeg: 100),
            new AxisConfig(2, "elbow", SoftLimitMinDeg: -110, SoftLimitMaxDeg: 110),
            new AxisConfig(3, "wrist", SoftLimitMinDeg: -120, SoftLimitMaxDeg: 120),
            new AxisConfig(4, "gripper", SoftLimitMinDeg: gripperMin, SoftLimitMaxDeg: gripperMax),
        ]);

    private static TelemetryFrame Frame(params (int Id, double Pos)[] axes) =>
        new(1000, axes.Select(a => new AxisReport(a.Id, a.Pos, a.Pos, true)).ToList(),
            RuntimeState.Enabled);

    [Fact]
    public void Maps_Axes_To_Joint_Roles_And_Updates()
    {
        var vm = new ArmViewModel(Config());
        vm.Update(Frame((0, 30), (1, -20), (2, 45), (3, 10), (4, 60)));

        Assert.Equal(30, vm.BaseYaw);
        Assert.Equal(-20, vm.Shoulder);
        Assert.Equal(45, vm.Elbow);
        Assert.Equal(10, vm.Wrist);
        Assert.Equal(60, vm.Gripper);
    }

    [Fact]
    public void Clamps_Telemetry_To_Soft_Limits()
    {
        var vm = new ArmViewModel(Config(gripperMin: 5, gripperMax: 50));
        vm.Update(Frame((0, 500), (1, -500), (4, -30)));

        Assert.Equal(170, vm.BaseYaw);
        Assert.Equal(-100, vm.Shoulder);
        Assert.Equal(5, vm.Gripper);
    }

    [Fact]
    public void Unknown_Axis_Id_Is_Ignored()
    {
        var vm = new ArmViewModel(Config());
        vm.Update(Frame((9, 123)));
        Assert.Equal(0, vm.BaseYaw);
        Assert.Equal(0, vm.Gripper);
    }

    [Fact]
    public void Change_Events_Fire_Only_When_Value_Changes()
    {
        var vm = new ArmViewModel(Config());
        var events = new List<string?>();

        vm.Update(Frame((0, 10)));
        vm.PropertyChanged += (_, e) => events.Add(e.PropertyName);
        vm.Update(Frame((0, 10))); // same value — no event
        vm.Update(Frame((0, 11))); // changed — event
        Assert.Equal(["BaseYaw"], events);
    }

    [Fact]
    public void Default5Axis_Config_Maps_By_Position()
    {
        var vm = new ArmViewModel(MachineConfig.CreateDefault5Axis());
        Assert.NotNull(vm.BaseAxis);
        Assert.NotNull(vm.GripperAxis);
        Assert.Equal(0, vm.BaseAxis!.Id);
        Assert.Equal(4, vm.GripperAxis!.Id);
        Assert.Equal(0, ClampGripper(vm, -10)); // gripper floor is 0 in default config
    }

    private static double ClampGripper(ArmViewModel vm, double raw) =>
        vm.ClampToRole(4, raw);
}
