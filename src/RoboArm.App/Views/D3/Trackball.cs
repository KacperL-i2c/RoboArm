using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace RoboArm.App.Views.D3;

/// <summary>
/// Minimal trackball: left-drag orbits around a fixed center, wheel zooms.
/// No dependencies, no inertia — deterministic and enough for inspection.
/// </summary>
internal sealed class Trackball
{
    private readonly PerspectiveCamera _camera;
    private readonly Point3D _center;
    private double _yawDeg = 35;
    private double _pitchDeg = 22;
    private double _distance = 620;
    private Point? _last;

    public Trackball(PerspectiveCamera camera, Point3D center)
    {
        _camera = camera;
        _center = center;
        Apply();
    }

    public void Attach(UIElement element)
    {
        element.MouseLeftButtonDown += OnDown;
        element.MouseMove += OnMove;
        element.MouseLeftButtonUp += OnUp;
        element.MouseWheel += OnWheel;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _last = e.GetPosition(null);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_last is not { } last)
            return;
        var now = e.GetPosition(null);
        _yawDeg -= (now.X - last.X) * 0.4;
        _pitchDeg = Math.Clamp(_pitchDeg + (now.Y - last.Y) * 0.3, -85, 85);
        _last = now;
        Apply();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _last = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9 : 1.1), 200, 2500);
        Apply();
    }

    private void Apply()
    {
        var yaw = _yawDeg * Math.PI / 180;
        var pitch = _pitchDeg * Math.PI / 180;
        var cp = Math.Cos(pitch);
        var offset = new Vector3D(
            _distance * cp * Math.Sin(yaw),
            -_distance * cp * Math.Cos(yaw),
            _distance * Math.Sin(pitch));
        _camera.Position = _center + offset;
        _camera.LookDirection = _center - _camera.Position;
        _camera.UpDirection = new Vector3D(0, 0, 1);
    }
}
