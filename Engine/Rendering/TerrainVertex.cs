using System.Numerics;
using System.Runtime.InteropServices;

namespace VoxelEngine.Rendering;

/// <summary>
/// One vertex of a terrain mesh as it sits in the GPU buffer: a float position in chunk-local
/// metres, the normal packed into three signed bytes and the colour into four unsigned ones, with
/// emissive in the alpha channel. Twenty bytes instead of the twenty-eight of three float
/// triples, and the same layout <see cref="GpuMesh"/> describes to the vertex array.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TerrainVertex
{
    public const int Size = 20;

    public float X;
    public float Y;
    public float Z;

    public sbyte NormalX;
    public sbyte NormalY;
    public sbyte NormalZ;
    public byte Padding;

    public byte R;
    public byte G;
    public byte B;
    public byte Emissive;

    public Vector3 Position => new(X, Y, Z);

    public Vector3 Normal => new(NormalX / 127f, NormalY / 127f, NormalZ / 127f);

    public static sbyte PackNormal(float component)
        => (sbyte)Math.Clamp(MathF.Round(component * 127f), -127f, 127f);
}
