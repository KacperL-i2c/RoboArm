using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace RoboArm.App.Views.D3;

/// <summary>Hand-rolled mesh primitives (no external 3D toolkit; docs/04 no-dep rule).</summary>
internal static class Primitives
{
    /// <summary>Cylinder along +Z from 0 to <paramref name="height"/>, flat-shaded sides.</summary>
    public static MeshGeometry3D Cylinder(double radius, double height, int segments = 12)
    {
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        for (var i = 0; i < segments; i++)
        {
            var a0 = 2 * Math.PI * i / segments;
            var a1 = 2 * Math.PI * (i + 1) / segments;
            var (x0, y0) = (radius * Math.Cos(a0), radius * Math.Sin(a0));
            var (x1, y1) = (radius * Math.Cos(a1), radius * Math.Sin(a1));

            var n0 = new Vector3D(Math.Cos((a0 + a1) / 2), Math.Sin((a0 + a1) / 2), 0);
            var base0 = positions.Count;
            positions.Add(new Point3D(x0, y0, 0));
            positions.Add(new Point3D(x1, y1, 0));
            positions.Add(new Point3D(x0, y0, height));
            positions.Add(new Point3D(x1, y1, height));
            for (var k = 0; k < 4; k++)
                normals.Add(n0);
            indices.Add(base0); indices.Add(base0 + 1); indices.Add(base0 + 2);
            indices.Add(base0 + 1); indices.Add(base0 + 3); indices.Add(base0 + 2);
        }

        Cap(radius, 0, false, positions, normals, indices, segments);
        Cap(radius, height, true, positions, normals, indices, segments);

        return Freeze(new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        });
    }

    /// <summary>Sphere at origin with smooth normals.</summary>
    public static MeshGeometry3D Sphere(double radius, int slices = 10, int stacks = 8)
    {
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        for (var stack = 0; stack <= stacks; stack++)
        {
            var phi = Math.PI * stack / stacks;
            var z = radius * Math.Cos(phi);
            var r = radius * Math.Sin(phi);
            for (var slice = 0; slice <= slices; slice++)
            {
                var theta = 2 * Math.PI * slice / slices;
                var x = r * Math.Cos(theta);
                var y = r * Math.Sin(theta);
                positions.Add(new Point3D(x, y, z));
                normals.Add(new Vector3D(x, y, z));
            }
        }

        var row = slices + 1;
        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var a = stack * row + slice;
                var b = a + row;
                indices.Add(a); indices.Add(b); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
            }
        }

        return Freeze(new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        });
    }

    /// <summary>Axis-aligned box centered at origin.</summary>
    public static MeshGeometry3D Box(double sx, double sy, double sz)
    {
        var p = new Point3D[8];
        for (var zi = -1; zi <= 1; zi += 2)
            for (var yi = -1; yi <= 1; yi += 2)
                for (var xi = -1; xi <= 1; xi += 2)
                    p[(zi > 0 ? 4 : 0) | (yi > 0 ? 2 : 0) | (xi > 0 ? 1 : 0)] =
                        new Point3D(xi * sx * 0.5, yi * sy * 0.5, zi * sz * 0.5);
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        int I(int x, int y, int z) => (z > 0 ? 4 : 0) | (y > 0 ? 2 : 0) | (x > 0 ? 1 : 0);

        void Quad(int ax, int ay, int az, int bx, int by, int bz, int cx, int cy, int cz,
            int dx, int dy, int dz, Vector3D n)
        {
            var b0 = positions.Count;
            positions.Add(p[I(ax, ay, az)]);
            positions.Add(p[I(bx, by, bz)]);
            positions.Add(p[I(cx, cy, cz)]);
            positions.Add(p[I(dx, dy, dz)]);
            for (var k = 0; k < 4; k++)
                normals.Add(n);
            indices.Add(b0); indices.Add(b0 + 1); indices.Add(b0 + 2);
            indices.Add(b0); indices.Add(b0 + 2); indices.Add(b0 + 3);
        }

        Quad(1, 1, 1, 1, -1, 1, 1, -1, -1, 1, 1, -1, new Vector3D(1, 0, 0));
        Quad(-1, 1, -1, -1, -1, -1, -1, -1, 1, -1, 1, 1, new Vector3D(-1, 0, 0));
        Quad(-1, 1, 1, -1, -1, 1, 1, -1, 1, 1, 1, 1, new Vector3D(0, 0, 1));
        Quad(-1, 1, -1, 1, 1, -1, 1, -1, -1, -1, -1, -1, new Vector3D(0, 0, -1));
        Quad(-1, 1, -1, -1, 1, 1, 1, 1, 1, 1, 1, -1, new Vector3D(0, 1, 0));
        Quad(-1, -1, 1, -1, -1, -1, 1, -1, -1, 1, -1, 1, new Vector3D(0, -1, 0));

        return Freeze(new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        });
    }

    private static void Cap(double radius, double z, bool up,
        Point3DCollection positions, Vector3DCollection normals,
        Int32Collection indices, int segments)
    {
        var center = positions.Count;
        positions.Add(new Point3D(0, 0, z));
        normals.Add(new Vector3D(0, 0, up ? 1 : -1));
        for (var i = 0; i <= segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            positions.Add(new Point3D(radius * Math.Cos(a), radius * Math.Sin(a), z));
            normals.Add(new Vector3D(0, 0, up ? 1 : -1));
        }
        for (var i = 0; i < segments; i++)
        {
            // Counter-clockwise seen from the cap's outward normal direction.
            if (up)
            {
                indices.Add(center);
                indices.Add(center + 1 + i);
                indices.Add(center + 1 + i + 1);
            }
            else
            {
                indices.Add(center);
                indices.Add(center + 1 + i + 1);
                indices.Add(center + 1 + i);
            }
        }
    }

    private static MeshGeometry3D Freeze(MeshGeometry3D mesh)
    {
        mesh.Freeze();
        return mesh;
    }

    public static DiffuseMaterial Material(Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        material.Freeze();
        return material;
    }
}
