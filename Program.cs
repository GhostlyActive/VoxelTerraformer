using Raylib_cs;
using System.Numerics;
using Terraformer.Rendering;
using Terraformer.World;

namespace Terraformer;

public static class Program
{
    public static void Main()
    {
        const int screenWidth = 1280;
        const int screenHeight = 720;

        Raylib.InitWindow(screenWidth, screenHeight, "Terraformer");
        Raylib.SetTargetFPS(60);
        Raylib.DisableCursor();

        Camera3D camera = new Camera3D
        {
            Position = new Vector3(18, 16, 18),
            Target = new Vector3(8, 4, 8),
            Up = Vector3.UnitY,
            FovY = 60f,
            Projection = CameraProjection.Perspective
        };

        VoxelWorld world = new VoxelWorld();
        float sunTimer = 0.0f;

        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            Raylib.UpdateCamera(ref camera, CameraMode.Free);

            sunTimer += dt * 0.35f;
            Vector3 sunPos = new Vector3(
                MathF.Cos(sunTimer) * 30 + 8,
                MathF.Sin(sunTimer) * 20 + 18,
                MathF.Sin(sunTimer * 0.5f) * 30 + 8
            );

            world.Update(camera);

            Raylib.BeginDrawing();

            Raylib.ClearBackground(Color.SkyBlue);

            Raylib.BeginMode3D(camera);

            GridRenderer.DrawFromOrigin(64, 1.0f);
            world.Draw(sunPos);

            Raylib.EndMode3D();

            // UI
            Raylib.DrawFPS(10, 10);
            Raylib.DrawText("LMB/O: remove | RMB/P: place", 10, 40, 20, Color.Black);

            // Crosshair
            int cx = Raylib.GetScreenWidth() / 2;
            int cy = Raylib.GetScreenHeight() / 2;
            Raylib.DrawCircle(cx, cy, 4, Color.Black);

            Raylib.EndDrawing();
        }

        Raylib.CloseWindow();
    }
}
