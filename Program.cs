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

        VoxelWorld world = new VoxelWorld();

        // Spieler starten lassen (ein bisschen über Boden)
        PlayerController player = new PlayerController(new Vector3(10, 30, 8));

        // Day/Night
        DayNightCycle dayNight = new DayNightCycle
        {
            Center = new Vector3(8, 8, 8),
            DayLengthSeconds = 240f,
            OrbitRadius = 60f,
            DrawSunAndMoon = true
        };

        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();

            // Player bewegt sich + liefert Kamera
            Camera3D camera = player.Update(world, dt);

            // Day/Night Update (Speed: Z/U)
            dayNight.Update(dt);

            // Welt-Interaktion
            world.Update(camera);

            Raylib.BeginDrawing();

            // ✅ Himmel als Background (keine Overlays, keine Glitches)
            Raylib.ClearBackground(dayNight.SkyColor);

            Raylib.BeginMode3D(camera);

            GridRenderer.DrawFromOrigin(64, 1.0f);

            // Welt nutzt weiterhin Sonnenposition (dein Chunk nutzt sunPos.Y -> brightness)
            world.Draw(dayNight.SunPosition);

            // Sonne/Mond
            dayNight.Draw3D(camera);

            Raylib.EndMode3D();

            // UI
            Raylib.DrawFPS(10, 10);
            Raylib.DrawText("WASD move | Space jump | LMB remove | RMB place", 10, 40, 20, Color.Black);
            Raylib.DrawText("Z slower day | U faster day", 10, 65, 20, Color.Black);
            Raylib.DrawText(dayNight.SpeedLabel, 10, 90, 20, Color.Black);

            // Crosshair
            int cx = Raylib.GetScreenWidth() / 2;
            int cy = Raylib.GetScreenHeight() / 2;
            Raylib.DrawCircle(cx, cy, 4, Color.Black);

            Raylib.EndDrawing();
        }

        Raylib.CloseWindow();
    }
}
