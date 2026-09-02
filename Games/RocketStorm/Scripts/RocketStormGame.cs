using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Core;
using VoxelEngine.Scenes;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Terraformer.Games.RocketStorm;

/// <summary>
/// Survival under fire. The world runs in Smooth mode and every impact tears a round crater out
/// of the ground. The sphere brush stays live, so cover is something you dig for yourself:
/// sitting in a hole you can ride out a direct hit next to you.
/// </summary>
public sealed class RocketStormGame : Game
{
    private const float MaxHealth = 100f;

    /// <summary>Distance beyond which an impact does no damage at all</summary>
    private const float DamageRadius = 13f;

    /// <summary>Fixed sun, slightly off the zenith so slopes and craters keep their contrast</summary>
    private const float SunAngle = 75f;

    private VoxelTerrainScene _scene = null!;
    private RocketBarrage _barrage = null!;

    private float _health = MaxHealth;
    private float _survived;
    private float _hurtFlash;
    private bool _dead;

    public override Camera3D Camera => _scene.Camera;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: run, Shift sprints, Space jumps",
        "LMB: dig, a hole in the ground is cover",
        "RMB: place material",
        "R: start over once they got you",
    };

    public override void Load()
    {
        // Flatter than the sandbox: the craters should shape the landscape instead of
        // disappearing into a mountainside
        var terrain = new DefaultTerrainGenerator
        {
            Seed = 7331,
            BaseHeight = 14,
            ContinentAmplitude = 10f,
            MountainAmplitude = 7f,
            DetailAmplitude = 3f,
        };

        _scene = new VoxelTerrainScene(Context.Settings, new VoxelTerrainOptions
        {
            SaveSlot = "rocket-storm",
            Spawn = new Vector3(128, 60, 128),
            Generator = terrain,
            Mode = TerrainMode.Smooth,
            RunDayNight = false,
            SunAngleDegrees = SunAngle,
        });

        _scene.Player.Teleport(new Vector3(128, _scene.SurfaceHeight(128, 128) + 1f, 128));

        Context.Audio.Define("launch", SfxShape.Launch);
        Context.Audio.Define("explosion", SfxShape.Explosion);
        Context.Audio.Define("hit", SfxShape.Hit);

        _barrage = new RocketBarrage(_scene, Context.Audio);
    }

    public override void Update(float dt)
    {
        _scene.ShowDebugGeometry = Context.DebugOverlay;
        _hurtFlash = MathF.Max(0f, _hurtFlash - dt * 1.6f);

        if (_dead)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.R)) Restart();

            // The world keeps running so the picture does not freeze, but the player is out:
            // the scene ignores movement and mouse look while AllowPlayerControl is off
            _scene.Update(dt);
            return;
        }

        _survived += dt;
        _barrage.Update(dt, _scene.Player.Position, OnImpact);
        _scene.Update(dt);
    }

    private void OnImpact(Vector3 impact)
    {
        _scene.Explode(impact, RocketBarrage.CraterRadius);

        float distance = Vector3.Distance(_scene.Player.Position, impact);
        _scene.Shake(MathF.Max(0f, 1.6f - distance / 18f));
        Context.Audio.PlayAt("explosion", distance, 90f, 0.9f);

        if (distance >= DamageRadius) return;

        // Close in it hurts badly, further out the damage falls off fast
        float falloff = 1f - distance / DamageRadius;
        Damage(70f * falloff * falloff);
    }

    private void Damage(float amount)
    {
        if (amount <= 0.5f) return;

        _health -= amount;
        _hurtFlash = Math.Clamp(amount / 45f, 0.25f, 1f);
        Context.Audio.Play("hit", 0.6f);

        if (_health > 0f) return;

        _health = 0f;
        _dead = true;
        _scene.AllowPlayerControl = false;
        _scene.Particles.SpawnExplosion(_scene.Player.Position + Vector3.UnitY, new Color(200, 60, 60, 255), 1.1f);
    }

    private void Restart()
    {
        _health = MaxHealth;
        _survived = 0f;
        _dead = false;
        _hurtFlash = 0f;
        _scene.AllowPlayerControl = true;
        _barrage.Reset();
    }

    public override void UpdateAlways() => _scene.PumpMeshUploads();

    public override void DrawBackground() => _scene.DrawBackground();

    public override void DrawWorld()
    {
        _scene.Draw();
        if (!_dead) _barrage.Draw3D();
    }

    public override void DrawHud()
    {
        int width = Context.ScreenWidth;
        int height = Context.ScreenHeight;

        if (_hurtFlash > 0f)
            Raylib.DrawRectangle(0, 0, width, height, new Color((byte)200, (byte)30, (byte)30, (byte)(90 * _hurtFlash)));

        Color healthColor = _health > 50f
            ? new Color(120, 220, 140, 255)
            : _health > 20f ? new Color(240, 200, 90, 255) : new Color(230, 90, 80, 255);

        Hud.Bar(16, height - 46, 260, 26, _health / MaxHealth, healthColor, $"{_health:0} HP");

        Hud.Text($"Wave {Math.Max(1, _barrage.Wave)}", 16, 40, 24, Hud.Accent);
        Hud.Text($"Survived {_survived:0.0}s", 16, 70, 20);

        if (_barrage.InFlight > 0)
            Hud.Text($"Incoming: {_barrage.InFlight}", 16, 96, 20, Hud.Warning);
        else if (_barrage.WaveBreak > 0f && !_dead)
            Hud.Text($"Next wave in {_barrage.WaveBreak:0.0}s", 16, 96, 20, Hud.Accent);

        Hud.Crosshair(width, height);
        Hud.Centered("LMB dig for cover | RMB build | ESC menu", width / 2, height - 40, 16);

        if (!_dead) return;

        const int panelWidth = 420;
        const int panelHeight = 130;
        int x = (width - panelWidth) / 2;
        int y = (height - panelHeight) / 2;

        Hud.Panel(x, y, panelWidth, panelHeight);
        Hud.Centered("YOU DIDN'T MAKE IT", width / 2, y + 22, 30, Hud.Warning);
        Hud.Centered($"{_survived:0.0} seconds, wave {Math.Max(1, _barrage.Wave)}", width / 2, y + 62, 20);
        Hud.Centered("R: try again | ESC: menu", width / 2, y + 92, 18, Hud.Accent);
    }

    public override void Unload() => _scene.Dispose();
}
