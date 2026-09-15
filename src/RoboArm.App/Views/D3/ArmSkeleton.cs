using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace RoboArm.App.Views.D3;

/// <summary>
/// Schematic 5-DOF arm: base yaw → shoulder pitch → elbow pitch → wrist pitch →
/// gripper fingers. Scene graph is built once; per frame only rotation angles and
/// the finger gap change (no rebuilds, allocation-free steady state).
///
/// Link lengths are placeholders pending real dimensions from Phase 0 photos.
/// Neutral pose (all angles 0): arm points straight up from the base column.
/// </summary>
internal sealed class ArmSkeleton
{
    // Placeholder dimensions (mm-ish units, scene-local).
    public const double BaseColumnHeight = 70;
    public const double LinkShoulder = 190;
    public const double LinkElbow = 150;
    public const double LinkWrist = 80;
    public const double FingerLength = 45;

    private readonly AxisAngleRotation3D _baseYaw = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _shoulder = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _elbow = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _wrist = new(new Vector3D(1, 0, 0), 0);
    private readonly TranslateTransform3D _fingerLeft = new();
    private readonly TranslateTransform3D _fingerRight = new();

    /// <summary>Center of the scene (orbit target) — mid-arm height.</summary>
    public static Point3D OrbitCenter => new(0, 0, BaseColumnHeight + LinkShoulder * 0.5);

    public Model3DGroup Build()
    {
        var arm = new Model3DGroup();

        // ---- Base: plate + rotating column ----
        var basePlate = new GeometryModel3D(Primitives.Box(110, 110, 18), Primitives.Material(Color.FromRgb(70, 70, 78)))
        {
            Transform = new TranslateTransform3D(0, 0, 9),
        };
        arm.Children.Add(basePlate);

        var yawGroup = new Model3DGroup { Transform = RotationAt(0, 0, 18, _baseYaw) };
        yawGroup.Children.Add(new GeometryModel3D(
            Primitives.Cylinder(26, BaseColumnHeight, 14), Primitives.Material(Color.FromRgb(90, 96, 120))));
        yawGroup.Children.Add(JointSphere(0, 0, BaseColumnHeight));

        // ---- Shoulder → elbow ----
        var shoulderGroup = new Model3DGroup { Transform = RotationAt(0, 0, BaseColumnHeight, _shoulder) };
        shoulderGroup.Children.Add(Link(LinkShoulder, Color.FromRgb(64, 110, 160)));
        shoulderGroup.Children.Add(JointSphere(0, 0, LinkShoulder));

        // ---- Elbow → wrist ----
        var elbowGroup = new Model3DGroup { Transform = RotationAt(0, 0, LinkShoulder, _elbow) };
        elbowGroup.Children.Add(Link(LinkElbow, Color.FromRgb(56, 142, 142)));
        elbowGroup.Children.Add(JointSphere(0, 0, LinkElbow));

        // ---- Wrist → gripper ----
        var wristGroup = new Model3DGroup { Transform = RotationAt(0, 0, LinkElbow, _wrist) };
        wristGroup.Children.Add(Link(LinkWrist, Color.FromRgb(200, 130, 60)));
        wristGroup.Children.Add(Palm());
        wristGroup.Children.Add(Finger(_fingerLeft, +1));
        wristGroup.Children.Add(Finger(_fingerRight, -1));

        shoulderGroup.Children.Add(elbowGroup);
        elbowGroup.Children.Add(wristGroup);
        yawGroup.Children.Add(shoulderGroup);
        arm.Children.Add(yawGroup);

        // ---- Environment: ground disc + RGB axes ----
        var scene = new Model3DGroup();
        scene.Children.Add(new GeometryModel3D(
            Primitives.Cylinder(220, 4, 40), Primitives.Material(Color.FromRgb(38, 38, 44)))
        {
            Transform = new TranslateTransform3D(0, 0, -4),
            BackMaterial = Primitives.Material(Color.FromRgb(38, 38, 44)),
        });
        scene.Children.Add(AxisBar(70, new Vector3D(1, 0, 0), Color.FromRgb(190, 70, 70)));  // +X red
        scene.Children.Add(AxisBar(70, new Vector3D(0, 1, 0), Color.FromRgb(80, 180, 90)));  // +Y green
        scene.Children.Add(AxisBar(70, new Vector3D(0, 0, 1), Color.FromRgb(90, 120, 230))); // +Z blue
        scene.Children.Add(arm);
        return scene;
    }

    /// <summary>Drives the pose from joint angles (deg). Cheap: sets 5 numbers.</summary>
    public void Update(double baseYawDeg, double shoulderDeg, double elbowDeg,
        double wristDeg, double gripperDeg)
    {
        _baseYaw.Angle = baseYawDeg;
        _shoulder.Angle = shoulderDeg;
        _elbow.Angle = elbowDeg;
        _wrist.Angle = wristDeg;

        // Gripper angle (0..60° default) → finger gap; 0 = open, max = closed.
        var gap = 6 + gripperDeg * 0.45;
        _fingerLeft.OffsetX = gap;
        _fingerRight.OffsetX = -gap;
    }

    private static Transform3D RotationAt(double x, double y, double z, AxisAngleRotation3D rotation)
    {
        var group = new Transform3DGroup();
        group.Children.Add(new TranslateTransform3D(x, y, z));
        group.Children.Add(new RotateTransform3D(rotation));
        return group;
    }

    private static GeometryModel3D Link(double length, Color color) => new(
        Primitives.Cylinder(14, length, 12), Primitives.Material(color));

    private static GeometryModel3D JointSphere(double x, double y, double z) => new(
        Primitives.Sphere(19, 10, 8), Primitives.Material(Color.FromRgb(150, 150, 158)))
    {
        Transform = new TranslateTransform3D(x, y, z),
    };

    private static GeometryModel3D Palm() => new(
        Primitives.Box(44, 26, 18), Primitives.Material(Color.FromRgb(120, 170, 90)))
    {
        Transform = new TranslateTransform3D(0, 0, LinkWrist),
    };

    private static GeometryModel3D Finger(TranslateTransform3D offset, int side)
    {
        var group = new Transform3DGroup();
        group.Children.Add(offset);
        group.Children.Add(new TranslateTransform3D(7 * side, 0, LinkWrist + 9));
        return new GeometryModel3D(
            Primitives.Box(6, 22, FingerLength), Primitives.Material(Color.FromRgb(140, 190, 110)))
        {
            Transform = group,
        };
    }

    private static GeometryModel3D AxisBar(double length, Vector3D direction, Color color)
    {
        var bar = new GeometryModel3D(
            Primitives.Box(4, 4, length), Primitives.Material(color));
        var group = new Transform3DGroup();
        if (direction.X != 0)
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90)));
        if (direction.Y != 0)
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90)));
        group.Children.Add(new TranslateTransform3D(
            direction.X * length * 0.5, direction.Y * length * 0.5, 2 + direction.Z * length * 0.5));
        bar.Transform = group;
        return bar;
    }
}
