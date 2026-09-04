using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Core;
using VoxelEngine.Scenes;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Games.RocketStorm;

/// <summary>
/// Survival under fire. The world runs in Smooth mode and every impact tears a round crater out
/// of the ground. The sphere brush stays live, so cover is something you dig for yourself:
/// sitting in a hole you can ride out a direct hit next to you. Later waves bring clusters,
/// seekers and busters, the flak on F takes them down for points, and the best runs are kept.
/// </summary>
[GameDefinition("RocketStorm", "Rocket Storm", "Survive the barrage - you dig your own cover")]
public sealed class RocketStormGame : Game
{
    private const float MaxHealth = 100f;
    private const string SettingsKey = "RocketStorm/Game";

    /// <summary>Fixed sun, slightly off the zenith so slopes and craters keep their contrast</summary>
    private const float SunAngle = 75f;

    private const float FlakReach = 160f;
    private const float FlakCooldown = 0.45f;
    private const int ScorePerSecond = 10;

    private VoxelTerrainScene _scene = null!;
    private RocketBarrage _barrage = null!;
    private RocketStormSettings _settings = null!;
    private Highscores _highscores = null!;
    private TuningSection? _tuning;

    private float _health = MaxHealth;
    private float _survived;
    private float _hurtFlash;
    private bool _dead;
    private float _score;
    private int _shotDown;
    private int _rank;
    private float _flakHeat;
    private float _tracerLife;
    private Vector3 _tracerFrom, _tracerTo;
    private float _hitMarker;
    private int _lastBonus;

    public override Camera3D Camera => _scene.Camera;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: run, Shift sprints, Space jumps",
        "LMB: dig, a hole in the ground is cover | RMB: place material",
        "F: flak - shoots down whatever you aim at, seekers only die this way",
        "Standard rockets arc in, clusters split, busters dig deep, seekers hunt you",
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

        _scene = new VoxelTerrainScene(Context, new VoxelTerrainOptions
        {
            SaveSlot = "rocket-storm",
            Spawn = new Vector3(128, 60, 128),
            Generator = terrain,

            // A flat arena needs no mountains, and a low world streams and meshes that much faster
            WorldHeight = 64,
            Mode = TerrainMode.Smooth,
            RunDayNight = false,
            SunAngleDegrees = SunAngle,
        });

        Context.Audio.Define("launch", SfxShape.Launch);
        Context.Audio.Define("explosion", SfxShape.Explosion);
        Context.Audio.Define("hit", SfxShape.Hit);
        Context.Audio.Define("flak", SfxShape.Shot);
        Context.Audio.Define("shotdown", new SfxShape(0.45f, 520f, 90f, 0.7f, 7f));

        _highscores = new Highscores(Context.Store);

        _settings = Context.Store.Load<RocketStormSettings>(SettingsKey);
        var defaults = new RocketStormSettings();
        _tuning = Context.Tuning.AddSection("ROCKET STORM", () => Context.Store.Save(SettingsKey, _settings))
            .Value("Wave pause s", () => _settings.WavePause, v => _settings.WavePause = v, defaults.WavePause, 0.5f, 1f, 20f, "0.0")
            .Value("Rockets in wave 1", () => _settings.RocketsInFirstWave, v => _settings.RocketsInFirstWave = (int)v, defaults.RocketsInFirstWave, 1f, 1f, 12f, "0")
            .Value("Crater radius", () => _settings.CraterRadius, v => _settings.CraterRadius = v, defaults.CraterRadius, 0.5f, 1.5f, 12f, "0.0")
            .Value("Damage radius", () => _settings.DamageRadius, v => _settings.DamageRadius = v, defaults.DamageRadius, 1f, 3f, 40f, "0");

        _barrage = new RocketBarrage(_scene, Context.Audio, _settings);
        _barrage.Impact += OnImpact;
        _barrage.ShotDown += OnShotDown;
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
        _score += ScorePerSecond * dt;
        _flakHeat = MathF.Max(0f, _flakHeat - dt);
        _tracerLife = MathF.Max(0f, _tracerLife - dt);
        _hitMarker = MathF.Max(0f, _hitMarker - dt);

        if (Raylib.IsKeyPressed(KeyboardKey.F) && _flakHeat <= 0f) FireFlak();

        _barrage.Update(dt, _scene.Player.Position);
        _scene.Update(dt);
    }

    /// <summary>The flak: a hit-scan along the crosshair, with a tracer so a miss still shows where it went</summary>
    private void FireFlak()
    {
        _flakHeat = FlakCooldown;

        Ray aim = Raylib.GetScreenToWorldRay(new Vector2(Context.ScreenWidth / 2, Context.ScreenHeight / 2), _scene.Camera);
        Vector3 direction = Vector3.Normalize(aim.Direction);
        Vector3? hit = _barrage.ShootDown(aim, FlakReach);

        _tracerFrom = aim.Position + direction * 0.6f - Vector3.UnitY * 0.25f;
        _tracerTo = hit ?? aim.Position + direction * FlakReach;
        _tracerLife = 0.18f;

        _scene.AddLight(_tracerFrom + direction * 1.5f, 10f, new Vector3(1.8f, 1.4f, 0.8f), 0.12f);
        _scene.Particles.SpawnTrail(_tracerFrom + direction * 1.2f, direction * 6f, new Color(255, 220, 150, 255), 0.35f, 6);
        _scene.Shake(0.12f);
        Context.Audio.Play("flak", 0.5f, 1.3f);
    }

    private void OnShotDown(Vector3 position, RocketKind kind)
    {
        int bonus = kind switch { RocketKind.Buster => 300, RocketKind.Seeker => 200, RocketKind.Cluster => 150, _ => 100 };
        _score += bonus;
        _shotDown++;
        _lastBonus = bonus;
        _hitMarker = 0.8f;

        _scene.Particles.SpawnExplosion(position, new Color(255, 200, 120, 255), 0.9f);
        _scene.AddLight(position, 14f, new Vector3(1.6f, 1.0f, 0.5f), 0.4f);
        Context.Audio.PlayAt("shotdown", Vector3.Distance(_scene.Player.Position, position), 200f, 0.8f);
    }

    private void OnImpact(Vector3 impact, float craterRadius, float damageScale)
    {
        _scene.Explode(impact, craterRadius);

        float distance = Vector3.Distance(_scene.Player.Position, impact);
        _scene.Shake(MathF.Max(0f, 1.6f - distance / 18f) * damageScale);
        Context.Audio.PlayAt("explosion", distance, 90f, 0.9f, damageScale > 1.5f ? 0.7f : 1f);

        float damageRadius = _settings.DamageRadius * MathF.Sqrt(damageScale);
        if (distance >= damageRadius) return;

        // Close in it hurts badly, further out the damage falls off fast
        float falloff = 1f - distance / damageRadius;
        Damage(70f * damageScale * falloff * falloff);
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

        _rank = _highscores.Record((int)_score, Math.Max(1, _barrage.Wave), _survived);
    }

    private void Restart()
    {
        _health = MaxHealth;
        _survived = 0f;
        _score = 0f;
        _shotDown = 0;
        _rank = 0;
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

        if (_tracerLife > 0f)
        {
            // A glowing rod rather than a hairline, so a miss still shows where the shot went
            byte alpha = (byte)(255 * _tracerLife / 0.18f);
            Raylib.DrawCylinderEx(_tracerFrom, _tracerTo, 0.09f, 0.04f, 6, new Color((byte)255, (byte)235, (byte)170, alpha));
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCylinderEx(_tracerFrom, _tracerTo, 0.25f, 0.12f, 6, new Color((byte)255, (byte)160, (byte)60, (byte)(alpha / 3)));
            Raylib.EndBlendMode();
        }
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
        Hud.Text($"Score {(int)_score}  (best {_highscores.Best})", 16, 70, 20);
        Hud.Text($"Survived {_survived:0.0}s | shot down {_shotDown}", 16, 96, 18);

        if (_barrage.InFlight > 0)
            Hud.Text($"Incoming: {_barrage.InFlight}", 16, 122, 20, Hud.Warning);
        else if (_barrage.WaveBreak > 0f && !_dead)
            Hud.Text($"Next wave in {_barrage.WaveBreak:0.0}s", 16, 122, 20, Hud.Accent);

        Hud.Crosshair(width, height, _flakHeat > 0f ? new Color(255, 150, 90, 255) : null);
        Hud.Bar(width / 2 - 40, height / 2 + 24, 80, 6, 1f - _flakHeat / FlakCooldown, new Color(255, 200, 120, 255));
        Hud.Centered("F: FLAK", width / 2, height / 2 + 34, 14, new Color(255, 200, 120, 220));

        if (_hitMarker > 0f)
        {
            float rise = (0.8f - _hitMarker) * 40f;
            Hud.Centered($"+{_lastBonus}  shot down", width / 2, height / 2 - 40 - (int)rise, 22,
                new Color((byte)255, (byte)230, (byte)150, (byte)(255 * MathF.Min(1f, _hitMarker / 0.3f))));
        }

        Hud.Centered("LMB dig for cover | RMB build | F: flak shoots down the rocket under the crosshair | ESC menu", width / 2, height - 40, 16);

        if (!_dead) return;

        const int panelWidth = 460;
        int panelHeight = 150 + Math.Min(5, _highscores.Entries.Count) * 22;
        int x = (width - panelWidth) / 2;
        int y = (height - panelHeight) / 2;

        Hud.Panel(x, y, panelWidth, panelHeight);
        Hud.Centered("YOU DIDN'T MAKE IT", width / 2, y + 22, 30, Hud.Warning);
        Hud.Centered($"{(int)_score} points | {_survived:0.0} seconds, wave {Math.Max(1, _barrage.Wave)}" + (_rank > 0 ? $" | rank {_rank}" : ""), width / 2, y + 62, 20);

        int line = y + 96;
        for (int i = 0; i < Math.Min(5, _highscores.Entries.Count); i++)
        {
            HighscoreEntry entry = _highscores.Entries[i];
            bool mine = _rank == i + 1;
            Hud.Text($"{i + 1}.  {entry.Score,6}   wave {entry.Wave,2}   {entry.Seconds,5:0}s   {entry.Date}", x + 24, line, 18, mine ? Hud.Accent : Hud.Ink);
            line += 22;
        }

        Hud.Centered("R: try again | ESC: menu", width / 2, y + panelHeight - 34, 18, Hud.Accent);
    }

    public override void Unload()
    {
        if (_tuning != null) Context.Tuning.RemoveSection(_tuning);
        _scene.Dispose();
    }
}
