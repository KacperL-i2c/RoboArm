using System.ComponentModel;
using System.Runtime.CompilerServices;
using RoboArm.Machine;
using RoboArm.Motion;

namespace RoboArm.App.ViewModels;

/// <summary>
/// Live joint state for the 3D visualizer, fed from telemetry at ~20 Hz.
/// Axes map by position in MachineConfig.Axes (0=base yaw, 1=shoulder, 2=elbow,
/// 3=wrist, 4=gripper). Angles are clamped to each axis's soft limits.
/// Link lengths are placeholders until real arm dimensions are captured (Phase 0).
/// </summary>
public sealed class ArmViewModel : INotifyPropertyChanged
{
    private readonly MachineConfig _config;
    private readonly AxisConfig?[] _roles = new AxisConfig?[5];

    public ArmViewModel(MachineConfig config)
    {
        _config = config;
        for (var i = 0; i < _roles.Length && i < config.Axes.Count; i++)
            _roles[i] = config.Axes[i];
    }

    public AxisConfig? BaseAxis => _roles[0];
    public AxisConfig? ShoulderAxis => _roles[1];
    public AxisConfig? ElbowAxis => _roles[2];
    public AxisConfig? WristAxis => _roles[3];
    public AxisConfig? GripperAxis => _roles[4];

    private double _baseYaw;
    public double BaseYaw
    {
        get => _baseYaw;
        private set => Set(ref _baseYaw, value);
    }

    private double _shoulder;
    public double Shoulder
    {
        get => _shoulder;
        private set => Set(ref _shoulder, value);
    }

    private double _elbow;
    public double Elbow
    {
        get => _elbow;
        private set => Set(ref _elbow, value);
    }

    private double _wrist;
    public double Wrist
    {
        get => _wrist;
        private set => Set(ref _wrist, value);
    }

    private double _gripper;
    public double Gripper
    {
        get => _gripper;
        private set => Set(ref _gripper, value);
    }

    /// <summary>Clamps a raw telemetry angle into the axis's soft limits.</summary>
    public double ClampToRole(int roleIndex, double valueDeg)
    {
        var axis = _roles[roleIndex];
        if (axis is null)
            return 0;
        return Math.Clamp(valueDeg, axis.SoftLimitMinDeg, axis.SoftLimitMaxDeg);
    }

    /// <summary>Telemetry feed (called on the UI thread by the shell).</summary>
    public void Update(TelemetryFrame frame)
    {
        foreach (var report in frame.Axes)
        {
            var role = RoleOf(report.AxisId);
            if (role < 0)
                continue;
            var clamped = ClampToRole(role, report.EstimatedPositionDeg);
            switch (role)
            {
                case 0: BaseYaw = clamped; break;
                case 1: Shoulder = clamped; break;
                case 2: Elbow = clamped; break;
                case 3: Wrist = clamped; break;
                case 4: Gripper = clamped; break;
            }
        }
    }

    private int RoleOf(int axisId)
    {
        for (var i = 0; i < _roles.Length; i++)
        {
            if (_roles[i]?.Id == axisId)
                return i;
        }
        return -1;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new(name!));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
