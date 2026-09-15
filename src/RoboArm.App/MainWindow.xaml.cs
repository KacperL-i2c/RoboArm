using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RoboArm.App.ViewModels;
using RoboArm.App.Views;

namespace RoboArm.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly ArmView3D _armView = new();
    private ArmWindow? _armWindow;
    private bool _shutdownComplete;

    public MainWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _armView.Bind(viewModel.Arm);
        _armView.DetachRequested += OnArmDetachToggle;
        ArmHost.Content = _armView;
    }

    // ---- 3D view detach / dock ----

    private void OnArmDetachToggle(object? sender, EventArgs e)
    {
        if (_armWindow is null)
            DetachArmView();
        else
            DockArmView();
    }

    private void DetachArmView()
    {
        ArmHost.Content = new TextBlock
        {
            Text = "3D view detached — close its window to dock it back",
            Foreground = System.Windows.Media.Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };
        _armWindow = new ArmWindow(_armView);
        _armWindow.DockBackRequested += (_, _) => DockArmView();
        _armView.SetDetached(true);
        _armWindow.Show();
        _armWindow.Activate();
    }

    private void DockArmView()
    {
        if (_armWindow is null)
            return;
        var window = _armWindow;
        _armWindow = null;
        window.DockBackRequested -= (_, _) => { };
        window.PrepareForDock();
        if (window.IsLoaded)
            window.Close();
        _armView.SetDetached(false);
        ArmHost.Content = _armView;
    }

    /// <summary>
    /// Safe-close (docs/03 L5): stop → disable → dispose, then close for real.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;
        e.Cancel = true;
        _armWindow?.Close(); // docks the 3D view back and disposes nothing extra
        try
        {
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;
            _viewModel.Dispose();
            Close();
        }
    }
}
