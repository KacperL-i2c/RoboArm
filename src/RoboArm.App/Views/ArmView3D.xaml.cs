using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using RoboArm.App.ViewModels;
using RoboArm.App.Views.D3;

namespace RoboArm.App.Views;

public partial class ArmView3D : UserControl
{
    private readonly ArmSkeleton _skeleton = new();
    private ArmViewModel? _model;

    /// <summary>Raised when the user asks to detach this view into its own window.</summary>
    public event EventHandler? DetachRequested;

    public ArmView3D()
    {
        InitializeComponent();
        var modelVisual = new ModelVisual3D { Content = _skeleton.Build() };
        Viewport.Children.Add(modelVisual);
        _skeleton.Update(0, 0, 0, 0, 0);

        var trackball = new Trackball((PerspectiveCamera)Viewport.Camera, ArmSkeleton.OrbitCenter);
        trackball.Attach(Viewport);
    }

    public void Bind(ArmViewModel model)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnJointChanged;
        _model = model;
        model.PropertyChanged += OnJointChanged;
        UpdatePose(model);
    }

    private void OnJointChanged(object? sender, PropertyChangedEventArgs e) => UpdatePose(_model!);

    private void UpdatePose(ArmViewModel model) =>
        _skeleton.Update(model.BaseYaw, model.Shoulder, model.Elbow, model.Wrist, model.Gripper);

    private void OnDetachClick(object sender, RoutedEventArgs e) => DetachRequested?.Invoke(this, e);

    /// <summary>Header text/button adapt when hosted in the floating window.</summary>
    public void SetDetached(bool detached)
    {
        DetachButton.Content = detached ? "Dock back ⤡" : "Detach ⤢";
        DetachButton.Click -= OnDetachClick;
        if (detached)
            DetachButton.Click += OnDockBackClick;
        else
            DetachButton.Click += OnDetachClick;
    }

    private void OnDockBackClick(object sender, RoutedEventArgs e) => DetachRequested?.Invoke(this, e);
}
