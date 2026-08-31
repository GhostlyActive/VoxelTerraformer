using Raylib_cs;
using System.Numerics;
using Terraformer.Config;
using Terraformer.Effects;
using Terraformer.Rendering;
using Terraformer.UI;
using Terraformer.World;

namespace Terraformer;

public static class Program
{
    public static void Main(string[] args)
    {
        const int screenWidth = 1280;
        const int screenHeight = 720;

        // Mini-Testmodus: rendert ein paar Sekunden, legt einen Screenshot ab und beendet sich
        bool smokeTest = Array.Exists(args, argument => argument == "--smoke");

        Raylib.InitWindow(screenWidth, screenHeight, "Terraformer");
        Raylib.SetTargetFPS(60);
        Raylib.SetExitKey(KeyboardKey.Null); // ESC gehört dem Pause-Menü, nicht dem Fenster
        if (!smokeTest) Raylib.DisableCursor();
        else Raylib.SetMousePosition(screenWidth / 2, screenHeight / 2); // sonst verdreht das erste Maus-Delta die Testkamera

        DebugSettings settings = DebugSettings.Load();

        WorldStorage storage = new WorldStorage();
        VoxelWorld world = new VoxelWorld(storage);

        // Spawn ist ein fester Punkt im (per Seed deterministischen) Terrain;
        // im Testlauf stattdessen ein Aussichtspunkt mit Blick quer über die Welt
        PlayerController player = smokeTest
            ? new PlayerController(new Vector3(40, 58, 220), settings)
            : new PlayerController(new Vector3(128, 60, 128), settings);

        // Startbereich sofort laden, damit der Spieler auf festem Boden landet
        world.EnsureAround(player.Position, 3);

        DayNightCycle dayNight = new DayNightCycle
        {
            Center = player.Position,
            DayLengthSeconds = 240f,
            OrbitRadius = 300f,
            DrawSunAndMoon = true
        };

        // Testlauf zur Mittagszeit, damit auf dem Screenshot etwas zu erkennen ist
        if (smokeTest) dayNight.AdvanceTime(60f);

        TerrainShader terrainShader = new TerrainShader();
        ChunkMeshManager meshManager = new ChunkMeshManager(world);
        meshManager.BuildAllNow();

        ParticleSystem particles = new ParticleSystem();
        world.BlockBroken += particles.SpawnBlockBreak;
        world.BlockPlaced += particles.SpawnBlockPlace;

        DebugMenu debugMenu = new DebugMenu(settings);
        PauseMenu pauseMenu = new PauseMenu();

        StarField stars = new StarField();
        CloudLayer clouds = new CloudLayer();

        int smokeFrames = 0;
        bool debugOverlay = smokeTest; // im Testlauf direkt an, damit die Stats auf dem Screenshot stehen
        float elapsedTime = 0f;
        bool quitRequested = false;
        bool cursorFree = smokeTest;
        Camera3D camera = default;

        while (!Raylib.WindowShouldClose() && !quitRequested)
        {
            float dt = Raylib.GetFrameTime();

            PauseMenuAction menuAction = pauseMenu.Update();
            switch (menuAction)
            {
                case PauseMenuAction.Save:
                    world.SaveWorld();
                    storage.SaveMeta(player.Position, dayNight.TimeSeconds);
                    pauseMenu.Close();
                    pauseMenu.ShowStatus("World saved");
                    break;

                case PauseMenuAction.Load:
                    if (world.LoadWorld())
                    {
                        if (storage.TryLoadMeta(out Vector3 savedPosition, out float savedTime))
                        {
                            player.Teleport(savedPosition);
                            dayNight.TimeSeconds = savedTime;
                        }
                        world.EnsureAround(player.Position, 3);
                        pauseMenu.Close();
                        pauseMenu.ShowStatus("World loaded");
                    }
                    else
                    {
                        pauseMenu.ShowStatus("No save found");
                    }
                    break;

                case PauseMenuAction.Quit:
                    quitRequested = true;
                    break;
            }

            bool paused = pauseMenu.IsOpen;

            // Cursor freigeben, solange das Pause-Menü offen ist
            if (!smokeTest)
            {
                if (paused && !cursorFree)
                {
                    Raylib.EnableCursor();
                    cursorFree = true;
                }
                else if (!paused && cursorFree)
                {
                    Raylib.DisableCursor();
                    cursorFree = false;
                }
            }

            if (!paused)
            {
                elapsedTime += dt;

                if (Raylib.IsKeyPressed(KeyboardKey.F3)) debugOverlay = !debugOverlay;

                debugMenu.Update();

                // Player bewegt sich + liefert Kamera
                camera = player.Update(world, dt);

                // Welt um den Spieler streamen (Budget: max. 2 neue Chunks pro Frame)
                world.UpdateStreaming(player.Position, 2);

                // Sonne/Mond wandern mit dem Spieler mit — wirken dadurch unendlich fern
                dayNight.Center = player.Position;

                // Day/Night Update (Speed: Z/U)
                dayNight.Update(dt);

                // V wechselt zwischen Block-Modus und Sculpt-Modus (Feinverformung)
                if (Raylib.IsKeyPressed(KeyboardKey.V)) world.SculptMode = !world.SculptMode;

                // Mausrad: Bau-Reichweite | Ctrl+Mausrad: Sculpt-Brushgröße
                float wheel = Raylib.GetMouseWheelMove();
                if (wheel != 0f)
                {
                    if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
                        settings.SculptRadius = Math.Clamp(settings.SculptRadius + wheel * 0.1f, 0.25f, 2.5f);
                    else
                        settings.BuildReach = Math.Clamp(settings.BuildReach + wheel, 2f, 60f);
                }

                // Welt-Interaktion
                world.Update(camera, player.Bounds, settings.BuildReach, settings.SculptRadius);

                // Partikel laufen über den unbeleuchteten Default-Shader → Weltlicht beim Spawn einbacken
                Vector3 light = dayNight.AmbientColor + dayNight.SunlightColor * 0.8f;
                particles.LightScale = Math.Clamp((light.X + light.Y + light.Z) / 3f, 0.15f, 1.1f);
                particles.Update(world, dt);
            }

            // Fertige Meshes auch im Pausenzustand hochladen (Worker-Ergebnisse)
            meshManager.Update();

            Raylib.BeginDrawing();

            // Himmel als vertikaler Verlauf: Zenit dunkler, Horizont heller (= Fog-Farbe)
            Raylib.ClearBackground(dayNight.SkyZenithColor);
            Raylib.DrawRectangleGradientV(
                0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(),
                dayNight.SkyZenithColor, dayNight.SkyColor);

            Raylib.BeginMode3D(camera);

            terrainShader.SetFrame(dayNight, camera.Position);

            Frustum frustum = Frustum.FromCamera(
                camera, Raylib.GetScreenWidth() / (float)Raylib.GetScreenHeight());
            meshManager.Draw(terrainShader.Material, frustum);

            world.DrawHover();
            particles.Draw();
            dayNight.Draw3D(camera);
            stars.Draw(camera, 1f - dayNight.Daylight01, elapsedTime);
            clouds.Draw(elapsedTime, dayNight.Daylight01, player.Position);

            if (debugOverlay)
            {
                GridRenderer.DrawFromOrigin(256, 1.0f);
                meshManager.DrawChunkBounds();
            }

            Raylib.EndMode3D();

            // UI
            Raylib.DrawFPS(10, 10);
            Raylib.DrawText("WASD move | Shift sprint | Space jump | LMB remove | RMB place | V sculpt", 10, 40, 20, Color.Black);
            Raylib.DrawText("Wheel: reach | Ctrl+Wheel: brush | Z/U day | F3 debug | M tuning | ESC menu", 10, 65, 20, Color.Black);
            Raylib.DrawText(dayNight.SpeedLabel, 10, 90, 20, Color.Black);

            if (debugOverlay)
            {
                string stats =
                    $"Chunks {meshManager.VisibleChunks}/{meshManager.MeshedChunks} | " +
                    $"Loaded {world.LoadedChunkCount} | " +
                    $"Verts {meshManager.TotalVertices / 1000}k | " +
                    $"Queue {meshManager.PendingChunks} | " +
                    $"Particles {particles.ActiveParticles}";
                Raylib.DrawText(stats, 10, 115, 20, Color.DarkBlue);
            }

            // Crosshair
            int cx = Raylib.GetScreenWidth() / 2;
            int cy = Raylib.GetScreenHeight() / 2;
            Raylib.DrawCircle(cx, cy, 4, Color.Black);

            // Aktueller Modus + Bau-Reichweite unten mittig
            string reachLabel = world.SculptMode
                ? $"Sculpt r={settings.SculptRadius:0.0} | Reach {settings.BuildReach:0}"
                : $"Reach: {settings.BuildReach:0}";
            int reachWidth = Raylib.MeasureText(reachLabel, 16);
            int reachY = Raylib.GetScreenHeight() - 40;
            Raylib.DrawText(reachLabel, cx - reachWidth / 2 + 1, reachY + 1, 16, new Color(10, 15, 25, 200));
            Raylib.DrawText(reachLabel, cx - reachWidth / 2, reachY, 16, new Color(220, 245, 250, 240));

            debugMenu.Draw(Raylib.GetScreenWidth());
            pauseMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());

            Raylib.EndDrawing();

            if (smokeTest && ++smokeFrames >= 150)
            {
                Raylib.TakeScreenshot("smoke.png");
                break;
            }
        }

        settings.Save();
        meshManager.Dispose();
        terrainShader.Unload();
        Raylib.CloseWindow();
    }
}
