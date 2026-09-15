using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RoboArm.App.Infrastructure;
using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;

namespace RoboArm.App.ViewModels;

public sealed class AxisCardViewModel : INotifyPropertyChanged
{
    private readonly ShellViewModel _shell;
    private readonly AxisConfig _axis;
    private readonly JogController _jog;

    public AxisCardViewModel(ShellViewModel shell, AxisConfig axis, JogController jog)
    {
        _shell = shell;
        _axis = axis;
        _jog = jog;
        _target = Math.Clamp(0, axis.SoftLimitMinDeg, axis.SoftLimitMaxDeg);

        MoveToCommand = new RelayCommand(
            _ => MoveTo(),
            _ => _shell.Engine.State == RuntimeState.Enabled);
        SetHomeCommand = new RelayCommand(_ => SetHome(), _ => true);
        JogPlusStart = new RelayCommand(_ => StartJog(1));
        JogMinusStart = new RelayCommand(_ => StartJog(-1));
        JogStopCommand = new RelayCommand(_ => StopJog());
    }

    public int AxisId => _axis.Id;
    public string Name => _axis.Name;
    public double Min => _axis.SoftLimitMinDeg;
    public double Max => _axis.SoftLimitMaxDeg;
    public string LimitsText => $"{Min:F0}° … {Max:F0}°";

    private double _measured;
    public double Measured
    {
        get => _measured;
        private set { _measured = value; OnPropertyChanged(); }
    }

    private double _commanded;
    public double Commanded
    {
        get => _commanded;
        private set { _commanded = value; OnPropertyChanged(); }
    }

    private bool _driveEnabled;
    public bool DriveEnabled
    {
        get => _driveEnabled;
        private set { _driveEnabled = value; OnPropertyChanged(); }
    }

    private double _target;
    public double Target
    {
        get => _target;
        set { _target = Math.Clamp(value, Min, Max); OnPropertyChanged(); }
    }

    public ICommand MoveToCommand { get; }
    public ICommand SetHomeCommand { get; }
    public ICommand JogPlusStart { get; }
    public ICommand JogMinusStart { get; }
    public ICommand JogStopCommand { get; }

    public void Update(AxisReport report)
    {
        Measured = report.EstimatedPositionDeg;
        Commanded = report.CommandedPositionDeg;
        DriveEnabled = report.Enabled;
    }

    private void MoveTo()
    {
        _jog.Stop(AxisId);
        _shell.RunMove(AxisId, Math.Clamp(Target, Min, Max));
    }

    private void SetHome()
    {
        _shell.RunMarkHomed();
        _shell.SetStatus($"{Name}: home set at current position (real homing needs hardware)");
    }

    private void StartJog(int direction)
    {
        try
        {
            _jog.Start(AxisId, direction);
        }
        catch (InvalidOperationException ex)
        {
            _shell.SetStatus($"Jog rejected: {ex.Message}");
        }
    }

    private void StopJog() => _jog.Stop(AxisId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}
