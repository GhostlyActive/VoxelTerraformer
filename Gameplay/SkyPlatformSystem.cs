using Raylib_cs;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Gameplay;

/// <summary>
/// Sky-Platform-Mechanik: Space in der Luft erzeugt eine Plattform unter den Füßen,
/// die nach kurzer Zeit zerbröselt. Ladungen füllen sich nur bei Bodenkontakt auf —
/// so wird aus dem Trick Skill-Movement statt Dauerflug.
/// </summary>
public sealed class SkyPlatformSystem
{
    private readonly record struct Platform(int X, int Y, int Z, float ExpiresAt);

    public const int MaxCharges = 3;

    private const float Lifetime = 3.0f;
    private const float WarnTime = 0.9f;       // Restzeit, ab der die Plattform sichtbar warnt
    private const float SpawnFlashTime = 0.15f;
    private const float SpawnCooldown = 0.15f;

    private readonly List<Platform> _platforms = new();
    private float _time;
    private float _lastSpawnTime = -999f;

    public int Charges { get; private set; } = MaxCharges;
    public int ActivePlatforms => _platforms.Count;

    /// <summary>Plattform erschienen: Zentrum + Farbe (für Partikel)</summary>
    public event Action<Vector3, Color>? PlatformCreated;

    /// <summary>Plattform zerbröselt: Zentrum + Farbe</summary>
    public event Action<Vector3, Color>? PlatformCrumbled;

    public void Update(VoxelWorld world, PlayerController player, float dt)
    {
        _time += dt;

        if (player.IsGrounded) Charges = MaxCharges;

        // Space in der Luft — aber nur, wenn der Druck nicht schon einen (Coyote-)Sprung ausgelöst hat
        bool wantsPlatform =
            Raylib.IsKeyPressed(KeyboardKey.Space) &&
            !player.IsGrounded &&
            !player.JumpedThisFrame;

        if (wantsPlatform) TrySpawn(world, player);

        // Abgelaufene Plattformen zerbröseln lassen
        for (int i = _platforms.Count - 1; i >= 0; i--)
        {
            Platform platform = _platforms[i];
            if (_time < platform.ExpiresAt) continue;

            _platforms.RemoveAt(i);

            // Nur entfernen, wenn dort wirklich noch die Plattform steht (Spieler kann sie abgebaut haben)
            if (world.GetBlock(platform.X, platform.Y, platform.Z) != BlockRegistry.Platform) continue;

            world.SetBlock(platform.X, platform.Y, platform.Z, BlockRegistry.Air);
            PlatformCrumbled?.Invoke(Center(platform), PlatformColor);
        }
    }

    private void TrySpawn(VoxelWorld world, PlayerController player)
    {
        if (Charges <= 0) return;
        if (_time - _lastSpawnTime < SpawnCooldown) return;

        // Höchste Zelle, deren Oberkante nicht über den Füßen liegt
        int x = (int)MathF.Floor(player.Position.X);
        int y = (int)MathF.Floor(player.Position.Y - 1f);
        int z = (int)MathF.Floor(player.Position.Z);

        if (y < 0 || y >= VoxelWorld.WorldHeight) return;
        if (world.GetBlock(x, y, z) != BlockRegistry.Air) return;

        world.SetBlock(x, y, z, BlockRegistry.Platform);
        _platforms.Add(new Platform(x, y, z, _time + Lifetime));
        Charges--;
        _lastSpawnTime = _time;

        PlatformCreated?.Invoke(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), PlatformColor);
    }

    /// <summary>Spawn-Blitz und Ablauf-Warnung — innerhalb von BeginMode3D aufrufen</summary>
    public void Draw()
    {
        foreach (Platform platform in _platforms)
        {
            float remaining = platform.ExpiresAt - _time;
            float age = Lifetime - remaining;
            Vector3 center = Center(platform);

            if (age < SpawnFlashTime)
            {
                // Materialisierung: Rahmen zieht sich auf die Blockgröße zusammen
                float t = age / SpawnFlashTime;
                float size = 1f + 0.6f * (1f - t);
                var flash = new Color((byte)200, (byte)255, (byte)255, (byte)(255 * (1f - t)));
                Raylib.DrawCubeWires(center, size, size, size, flash);
            }
            else if (remaining < WarnTime)
            {
                // Warn-Puls kurz vor dem Zerbröseln
                float pulse = 0.5f + 0.5f * MathF.Sin(_time * 18f);
                var warn = new Color((byte)255, (byte)130, (byte)80, (byte)(110 + 110 * pulse));
                Raylib.DrawCubeWires(center, 1.04f, 1.04f, 1.04f, warn);
            }
        }
    }

    /// <summary>Ladungsanzeige unten mittig — nach EndMode3D aufrufen</summary>
    public void DrawHud(int screenWidth, int screenHeight)
    {
        const float radius = 10f;
        const float spacing = 32f;

        float startX = screenWidth / 2f - (MaxCharges - 1) * spacing / 2f;
        float y = screenHeight - 36f;

        for (int i = 0; i < MaxCharges; i++)
        {
            var center = new Vector2(startX + i * spacing, y);
            bool filled = i < Charges;

            Color fill = filled ? PlatformColor : new Color(40, 45, 55, 150);
            Raylib.DrawPoly(center, 4, radius, 45f, fill);
            Raylib.DrawPolyLines(center, 4, radius, 45f, new Color(15, 20, 30, 220));
        }
    }

    private static Color PlatformColor => BlockRegistry.Get(BlockRegistry.Platform).BaseColor;

    private static Vector3 Center(Platform platform)
        => new(platform.X + 0.5f, platform.Y + 0.5f, platform.Z + 0.5f);
}
