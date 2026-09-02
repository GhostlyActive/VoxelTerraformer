using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// The camera's view frustum as 6 planes: chunks outside it never have to be drawn at all.
/// </summary>
public readonly struct Frustum
{
    private readonly Plane[] _planes;

    private Frustum(Plane[] planes) => _planes = planes;

    public static Frustum FromCamera(Camera3D camera, float aspect)
    {
        // Near and far match the rlgl defaults (0.01 / 1000) so culling and rendering agree
        Matrix4x4 view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, camera.Up);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            camera.FovY * (MathF.PI / 180f), aspect, 0.01f, 1000f);
        Matrix4x4 clip = view * projection;

        var planes = new Plane[6];
        planes[0] = Normalize(clip.M14 + clip.M11, clip.M24 + clip.M21, clip.M34 + clip.M31, clip.M44 + clip.M41); // links
        planes[1] = Normalize(clip.M14 - clip.M11, clip.M24 - clip.M21, clip.M34 - clip.M31, clip.M44 - clip.M41); // rechts
        planes[2] = Normalize(clip.M14 + clip.M12, clip.M24 + clip.M22, clip.M34 + clip.M32, clip.M44 + clip.M42); // unten
        planes[3] = Normalize(clip.M14 - clip.M12, clip.M24 - clip.M22, clip.M34 - clip.M32, clip.M44 - clip.M42); // oben
        planes[4] = Normalize(clip.M13, clip.M23, clip.M33, clip.M43);                                             // nah
        planes[5] = Normalize(clip.M14 - clip.M13, clip.M24 - clip.M23, clip.M34 - clip.M33, clip.M44 - clip.M43); // fern

        return new Frustum(planes);
    }

    private static Plane Normalize(float a, float b, float c, float d)
        => Plane.Normalize(new Plane(a, b, c, d));

    public bool Intersects(Vector3 min, Vector3 max)
    {
        foreach (Plane plane in _planes)
        {
            // Positive vertex: the corner of the box furthest along the plane normal
            var p = new Vector3(
                plane.Normal.X >= 0 ? max.X : min.X,
                plane.Normal.Y >= 0 ? max.Y : min.Y,
                plane.Normal.Z >= 0 ? max.Z : min.Z);

            if (Plane.DotCoordinate(plane, p) < 0f) return false;
        }

        return true;
    }
}
