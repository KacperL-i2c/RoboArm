using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RoboArm.App.Infrastructure;
using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;

namespace RoboArm.App.ViewModels;

public sealed class ShellViewModel : IDisposable
{
    private readonly MachineSession _session;
    private readonly JogController _jog;
    private readonly IReadOnlyDictionary<int, AxisCardViewModel> _cardsById;
    private long _lastUiUpdateMs;

    public ExecutionEngine Engine => _session.Engine;
    public MachineConfig Config => _session.Config;
    public ArmViewModel Arm { get; }
    public ObservableCollection<AxisCardViewModel> Axes { get; }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand EStopCommand { get; }
    public ICommand AcknowledgeFaultCommand { get; }

    public ShellViewModel(MachineSession session)
    {
        _session = session;
        _jog = new JogController(session.Engine, session.Config);

        ConnectCommand = new RelayCommand(_ => Run(() => Engine.ConnectAsync()), _ => Engine.CanTransition(RuntimeState.Connected));
        DisconnectCommand = new RelayCommand(_ => Run(() => Engine.DisconnectAsync()), _ => Engine.CanTransition(RuntimeState.Offline));
        EnableCommand = new RelayCommand(_ => Run(() => Engine.EnableAsync(true)), _ => CanEnable);
        DisableCommand = new RelayCommand(_ => Run(() => Engine.EnableAsync(false)), _ => Engine.CanTransition(RuntimeState.Connected));
        StopCommand = new RelayCommand(_ => Run(() => Engine.StopAsync(StopSeverity.SoftStop)), _ => Engine.State is RuntimeState.Executing or RuntimeState.Paused);
        EStopCommand = new RelayCommand(_ => Run(() => Engine.StopAsync(StopSeverity.Kill)), _ => Engine.State is not RuntimeState.Offline);
        AcknowledgeFaultCommand = new RelayCommand(_ => Run(() => Engine.AcknowledgeFaultAsync()), _ => Engine.State == RuntimeState.Faulted);

        Axes = new ObservableCollection<AxisCardViewModel>(
            session.Config.Axes.Select(a => new AxisCardViewModel(this, a, _jog)));
        _cardsById = Axes.ToDictionary(c => c.AxisId);

        Engine.StateChanged += OnStateChanged;
        Engine.Faulted += OnFaulted;
        Engine.Telemetry += OnTelemetry;
        _jog.JogFailed += message => SetStatus(message);

        Arm = new ArmViewModel(session.Config);
        Refresh();
    }

    // ---- Bindable state ----

    private RuntimeState _state;
    public RuntimeState State
    {
        get => _state;
        private set { _state = value; NotifyAll(); }
    }

    private string? _faultDetail;
    public bool HasFault => State == RuntimeState.Faulted;
    public string FaultText => _faultDetail is null ? "FAULT" : $"FAULT: {_faultDetail}";

    public bool RequiresRehomeNotice => Engine.RequiresRehome && State == RuntimeState.Connected;

    private string? _statusMessage;
    public string StatusMessage
    {
        get => _statusMessage ?? "";
        private set { _statusMessage = value; NotifyAll(); }
    }

    private int _speedPercent = 100;
    public int SpeedPercent
    {
        get => _speedPercent;
        set
        {
            _speedPercent = Math.Clamp(value, 0, 100);
            Engine.SpeedOverride = _speedPercent / 100.0;
            NotifyAll();
        }
    }

    public Brush StateBrush => State switch
    {
        RuntimeState.Offline => Brushes.Gray,
        RuntimeState.Connected => Brushes.DarkOrange,
        RuntimeState.Enabled => Brushes.ForestGreen,
        RuntimeState.Executing => Brushes.DodgerBlue,
        RuntimeState.Paused => Brushes.Goldenrod,
        RuntimeState.Faulted => Brushes.Red,
        _ => Brushes.Gray,
    };

    private bool CanEnable =>
        Engine.State == RuntimeState.Connected && !Engine.RequiresRehome;

    public JogController Jog => _jog;

    /// <summary>Single-axis absolute move from an axis card.</summary>
    public void RunMove(int axisId, double targetDeg) =>
        RunAsync(() => Engine.MoveToAsync(new Dictionary<int, double> { [axisId] = targetDeg }));

    public void RunMarkHomed() => RunAsync(() => Engine.MarkHomedAsync());

    // ---- Engine events (marshalled to UI thread) ----

    private void OnStateChanged(RuntimeState state) =>
        Dispatch(() => { State = state; Refresh(); });

    private void OnFaulted(FaultReason reason, string detail) =>
        Dispatch(() => { _faultDetail = $"{reason}: {detail}"; State = RuntimeState.Faulted; SetStatus($"FAULT {reason}"); });

    private void OnTelemetry(TelemetryFrame frame)
    {
        var now = Environment.TickCount64;
        if (now - _lastUiUpdateMs < 50) // throttle card updates to ~20 Hz
            return;
        _lastUiUpdateMs = now;
        Dispatch(() =>
        {
            Arm.Update(frame);
            foreach (var report in frame.Axes)
            {
                if (_cardsById.TryGetValue(report.AxisId, out var card))
                    card.Update(report);
            }
        });
    }

    private void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private void Run(Func<Task> operation)
    {
        RunAsync(operation);
    }

    private async void RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Rejected: {ex.Message}");
        }
        Refresh();
    }

    public void SetStatus(string message) => StatusMessage = message;

    private void Refresh() => State = Engine.State;

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        CommandManager.InvalidateRequerySuggested();
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        Engine.StateChanged -= OnStateChanged;
        Engine.Faulted -= OnFaulted;
        Engine.Telemetry -= OnTelemetry;
        _jog.Dispose();
    }

    public Task ShutdownAsync() => _session.ShutdownAsync();
}
