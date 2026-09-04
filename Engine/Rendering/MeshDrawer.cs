using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// Draws <see cref="GpuMesh"/> parts with the terrain shader bound once for the whole batch.
/// raylib's DrawMesh sets up shader, matrices, material maps and vertex state on every call, which
/// at a few thousand chunk sections per frame was a measurable share of the frame. Here a mesh
/// costs two matrix uniforms and one draw.
///
/// Matches DrawMesh's matrix conventions exactly: the model matrix is the caller's transform
/// times rlgl's current transform, so calls inside a PushMatrix block still work.
/// </summary>
public static class MeshDrawer
{
    private static int _locMvp;
    private static int _locModel;
    private static Matrix4x4 _viewProjection;
    private static Matrix4x4 _rlglTransform;
    private static bool _active;

    /// <summary>Call inside BeginMode3D, after the shader's frame uniforms are set</summary>
    public static unsafe void Begin(Shader shader)
    {
        // Anything raylib batched before us (spheres, lines) goes out first, so draw order holds
        Rlgl.DrawRenderBatchActive();

        Rlgl.EnableShader(shader.Id);

        _locMvp = shader.Locs[(int)ShaderLocationIndex.MatrixMvp];
        _locModel = shader.Locs[(int)ShaderLocationIndex.MatrixModel];

        _viewProjection = Raymath.MatrixMultiply(Rlgl.GetMatrixModelview(), Rlgl.GetMatrixProjection());
        _rlglTransform = Rlgl.GetMatrixTransform();
        _active = true;
    }

    public static void Draw(GpuMesh mesh, Matrix4x4 transform)
    {
        if (!_active) throw new InvalidOperationException("MeshDrawer.Begin has to run before Draw");

        Matrix4x4 model = Raymath.MatrixMultiply(transform, _rlglTransform);

        Rlgl.SetUniformMatrix(_locMvp, Raymath.MatrixMultiply(model, _viewProjection));
        if (_locModel >= 0) Rlgl.SetUniformMatrix(_locModel, model);

        mesh.Draw();
    }

    public static void End()
    {
        if (!_active) return;

        Rlgl.DisableVertexArray();
        Rlgl.DisableShader();
        _active = false;
    }
}
