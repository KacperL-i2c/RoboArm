using System.IO;
using System.Windows;
using RoboArm.App.ViewModels;
using RoboArm.Runtime;

namespace RoboArm.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MachineSession? _session;
    private ShellViewModel? _viewModel;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "RoboArm.Studio.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RoboArm Studio is already running.", "RoboArm Studio",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Target selection: default Simulator; --usb opens the CH340 board port
        // (pre-G0 skeleton: port opens and traffic is logged, but the board cannot
        // move until the protocol is decoded — docs/02).
        var auditDir = Path.Combine("data", "audit");
        try
        {
            _session = e.Args.Contains("--usb", StringComparer.OrdinalIgnoreCase)
                ? MachineSession.CreateUsb(auditDirectory: auditDir)
                : MachineSession.CreateSimulated(auditDir);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show($"{ex.Message}\n\nFalling back to the Simulator.", "RoboArm Studio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _session = MachineSession.CreateSimulated(auditDir);
        }
        _viewModel = new ShellViewModel(_session);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        CrashSafe("UI", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        CrashSafe("Domain", e.ExceptionObject as Exception ?? new Exception("unknown"));

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        CrashSafe("Task", e.Exception);

    /// <summary>H8: any crash path performs safe-close (stop → disable → dispose) and logs.</summary>
    private void CrashSafe(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine("data", "logs"));
            File.AppendAllText(
                Path.Combine("data", "logs", "crash.log"),
                $"[{DateTimeOffset.UtcNow:O}] [{source}] {exception}\n");
        }
        catch
        {
            // nothing more we can do
        }

        try
        {
            _session?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutdown best-effort on the crash path
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
            _session?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best-effort
        }
        _singleInstance?.Dispose();
    }
}
