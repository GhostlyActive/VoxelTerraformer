using Raylib_cs;
using System.Numerics;

namespace Terraformer.Rendering;

public static class GridRenderer
{
    /// <summary>
    /// Draws a grid starting at world origin (0,0,0) extending into +X and +Z.
    /// Independent from DrawGrid and does not rely on rlgl.
    /// </summary>
    public static void DrawFromOrigin(int cells, float spacing)
    {
        float size = cells * spacing;

        Color gridColor = new Color
        {
            R = 170,
            G = 170,
            B = 170,
            A = 255
        };

        for (int i = 0; i <= cells; i++)
        {
            float p = i * spacing;

            // Lines parallel to X axis
            Raylib.DrawLine3D(
                new Vector3(0, 0, p),
                new Vector3(size, 0, p),
                gridColor
            );

            // Lines parallel to Z axis
            Raylib.DrawLine3D(
                new Vector3(p, 0, 0),
                new Vector3(p, 0, size),
                gridColor
            );
        }
    }
}
