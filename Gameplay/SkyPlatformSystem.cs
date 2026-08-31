using Raylib_cs;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Gameplay;

public enum PlatformMode
{
    Single, // 1 Block unter den Füßen
    Pad,    // 3x3-Platte für sichere Landungen
    Bridge, // 3 Blöcke als Steg in Blickrichtung
}

/// <summary>
/// Sky-Platform-Mechanik: Space in der Luft erzeugt eine Plattform unter den Füßen,
/// die nach kurzer Zeit zerbröselt. Ladungen füllen sich nur bei Bodenkontakt auf —
/// so wird aus dem Trick Skill-Movement statt Dauerflug. Q wechselt den Modus.
/// </summary>
public sealed class SkyPlatformSystem
{
    private sealed class PlatformGroup
    {
        public required List<(int X, int Y, int Z)> Cells { get; init; }
        public required float ExpiresAt { get; init; }
    }

    public const int MaxCharges = 3;

    private const float Lifetime = 3.0f;
    private const float WarnTime = 0.9f;       // Restzeit, ab der die Plattform sichtbar warnt
    private const float SpawnFlashTime = 0.15f;
    private const float SpawnCooldown = 0.15f;

    private readonly List<PlatformGroup> _groups = new();
    private float _time;
    private float _lastSpawnTime = -999f;

    public int Charges { get; private set; } = MaxCharges;
    public PlatformMode Mode { get; private set; } = PlatformMode.Single;

    public string ModeLabel => Mode switch
    {
        PlatformMode.Single => "Einzel",
        PlatformMode.Pad => "Platte 3x3",
        _ => "Steg",
    };

    /// <summary>Plattform erschienen: Zentrum + Farbe (für Partikel)</summary>
    public event Action<Vector3, Color>? PlatformCreated;

    /// <summary>Plattform zerbröselt: Zentrum + Farbe</summary>
    public event Action<Vector3, Color>? PlatformCrumbled;

    public void Update(VoxelWorld world, PlayerController player, Vector3 lookDirection, float dt)
    {
        _time += dt;

        if (Raylib.IsKeyPressed(KeyboardKey.Q))
            Mode = (PlatformMode)(((int)Mode + 1) % 3);

        if (player.IsGrounded) Charges = MaxCharges;

        // Space in der Luft — aber nur, wenn der Druck nicht schon einen (Coyote-)Sprung ausgelöst hat
        bool wantsPlatform =
            Raylib.IsKeyPressed(KeyboardKey.Space) &&
            !player.IsGrounded &&
            !player.JumpedThisFrame;

        if (wantsPlatform) TrySpawn(world, player, lookDirection);

        // Abgelaufene Plattformen zerbröseln lassen
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            PlatformGroup group = _groups[i];
            if (_time < group.ExpiresAt) continue;

            _groups.RemoveAt(i);

            foreach ((int x, int y, int z) in group.Cells)
            {
                // Nur entfernen, wenn dort wirklich noch die Plattform steht (Spieler kann sie abgebaut haben)
                if (world.GetBlock(x, y, z) != BlockRegistry.Platform) continue;

                world.SetBlock(x, y, z, BlockRegistry.Air);
                PlatformCrumbled?.Invoke(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), PlatformColor);
            }
        }
    }

    private static int ChargeCost(PlatformMode mode) => mode == PlatformMode.Single ? 1 : 2;

    private void TrySpawn(VoxelWorld world, PlayerController player, Vector3 lookDirection)
    {
        int cost = ChargeCost(Mode);
        if (Charges < cost) return;
        if (_time - _lastSpawnTime < SpawnCooldown) return;

        // Höchste Zelle, deren Oberkante nicht über den Füßen liegt
        int baseX = (int)MathF.Floor(player.Position.X);
        int baseY = (int)MathF.Floor(player.Position.Y - 1f);
        int baseZ = (int)MathF.Floor(player.Position.Z);

        if (baseY < 0 || baseY >= VoxelWorld.WorldHeight) return;
        if (world.GetBlock(baseX, baseY, baseZ) != BlockRegistry.Air) return; // Fußzelle muss frei sein

        var cells = new List<(int X, int Y, int Z)>();
        foreach ((int dx, int dz) in CellOffsets(lookDirection))
        {
            int x = baseX + dx;
            int z = baseZ + dz;
            if (world.GetBlock(x, baseY, z) != BlockRegistry.Air) continue;

            world.SetBlock(x, baseY, z, BlockRegistry.Platform);
            cells.Add((x, baseY, z));
        }

        _groups.Add(new PlatformGroup { Cells = cells, ExpiresAt = _time + Lifetime });
        Charges -= cost;
        _lastSpawnTime = _time;

        PlatformCreated?.Invoke(new Vector3(baseX + 0.5f, baseY + 0.5f, baseZ + 0.5f), PlatformColor);
    }

    private IEnumerable<(int Dx, int Dz)> CellOffsets(Vector3 lookDirection)
    {
        switch (Mode)
        {
            case PlatformMode.Single:
                yield return (0, 0);
                break;

            case PlatformMode.Pad:
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    yield return (dx, dz);
                break;

            default:
            {
                // Blickrichtung auf 8 Richtungen snappen (0.3827 = sin 22.5°)
                var flat = new Vector2(lookDirection.X, lookDirection.Z);
                if (flat.LengthSquared() < 1e-4f) flat = new Vector2(1, 0);
                flat = Vector2.Normalize(flat);

                int dx = MathF.Abs(flat.X) > 0.3827f ? MathF.Sign(flat.X) : 0;
                int dz = MathF.Abs(flat.Y) > 0.3827f ? MathF.Sign(flat.Y) : 0;

                yield return (0, 0);
                yield return (dx, dz);
                yield return (2 * dx, 2 * dz);
                break;
            }
        }
    }

    /// <summary>Spawn-Blitz und Ablauf-Warnung — innerhalb von BeginMode3D aufrufen</summary>
    public void Draw()
    {
        foreach (PlatformGroup group in _groups)
        {
            float remaining = group.ExpiresAt - _time;
            float age = Lifetime - remaining;

            foreach ((int x, int y, int z) in group.Cells)
            {
                var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

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
    }

    /// <summary>Ladungsanzeige + Modus unten mittig — nach EndMode3D aufrufen</summary>
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

        string label = $"{ModeLabel} ({ChargeCost(Mode)})";
        int labelWidth = Raylib.MeasureText(label, 16);
        Raylib.DrawText(label, (int)(screenWidth / 2f - labelWidth / 2f), (int)(y - 32f), 16, new Color(25, 35, 45, 230));
    }

    private static Color PlatformColor => BlockRegistry.Get(BlockRegistry.Platform).BaseColor;
}
