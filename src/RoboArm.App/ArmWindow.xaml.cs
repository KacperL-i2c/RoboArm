using System.ComponentModel;
using System.Windows;
using RoboArm.App.Views;

namespace RoboArm.App;

/// <summary>Floating host for the detached 3D view (draggable to any monitor).</summary>
public partial class ArmWindow : Window
{
    private readonly ArmView3D _view;
    private bool _dockingBack;

    public ArmWindow(ArmView3D view)
    {
        _view = view;
        InitializeComponent();
        Title = "RoboArm Studio — 3D view";
        Width = 520;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = view;
        Owner = Application.Current?.MainWindow;
    }

    /// <summary>Triggers when the user wants the view docked back (button or window close).</summary>
    public event EventHandler? DockBackRequested;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_dockingBack)
        {
            _dockingBack = true;
            DockBackRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    public void PrepareForDock() => _dockingBack = true;
}
