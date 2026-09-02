using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Core;
using VoxelEngine.Effects;
using VoxelEngine.Input;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// A small solar system built from voxel spheres. You fly freely between planets and moons and
/// shoot them apart: every hit takes real voxels out of a body, until what is left of it carries
/// on around its orbit as a hollowed-out lump.
/// </summary>
public sealed class SolarSystemGame : Game
{
    private const float SunRadius = 90f;

    /// <summary>The default far plane of 1000 would cut away half the system</summary>
    private const double FarClipPlane = 20000.0;

    /// <summary>
    /// Materials are registered once per process: <see cref="Game.Load"/> runs again on every
    /// switch into this game, and each registration burns a block id.
    /// </summary>
    private static class Materials
    {
        public static readonly byte Iron = BlockRegistry.Register("Iron crust", new Color(150, 122, 104, 255));
        public static readonly byte IronCore = BlockRegistry.Register("Iron core", new Color(96, 74, 66, 255));
        public static readonly byte Grass = BlockRegistry.Register("Green crust", new Color(84, 158, 92, 255));
        public static readonly byte Soil = BlockRegistry.Register("Soil", new Color(122, 92, 62, 255));
        public static readonly byte Ice = BlockRegistry.Register("Ice", new Color(196, 224, 240, 255));
        public static readonly byte IceCore = BlockRegistry.Register("Deep ice", new Color(120, 168, 204, 255));
        public static readonly byte Basalt = BlockRegistry.Register("Basalt", new Color(84, 82, 96, 255));
        public static readonly byte Magma = BlockRegistry.Register("Magma", new Color(226, 110, 54, 255), emissive: 0.55f);
        public static readonly byte MoonRock = BlockRegistry.Register("Moon rock", new Color(168, 166, 172, 255));
    }

    private readonly List<CelestialBody> _bodies = new();

    private FreeFlyController _ship = null!;
    private VoxelCannon _cannon = null!;
    private VoxelBodyRenderer _renderer = null!;
    private TerrainShader _shader = null!;
    private ParticleSystem _particles = null!;
    private StarField _stars = null!;

    private Camera3D _camera;
    private float _time;
    private int _hits;
    private int _voxelsDestroyed;

    public override Camera3D Camera => _camera;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: fly, Space/Ctrl: climb and descend",
        "Shift: afterburner, nothing slows you down out here",
        "LMB: hold to charge the round, release to fire",
        "F: full stop | F3: debug | ESC: menu",
    };

    public override void Load()
    {
        Rlgl.SetClipPlanes(0.5, FarClipPlane);

        _shader = new TerrainShader
        {
            // Fog makes no sense in vacuum, so its range sits far behind every orbit
            FogStart = 12000f,
            FogEnd = 19000f,
        };

        _renderer = new VoxelBodyRenderer();
        _particles = new ParticleSystem();
        _stars = new StarField(fullSphere: true, distance: 9000f, starCount: 700);

        Context.Audio.Define("shot", SfxShape.Shot);
        Context.Audio.Define("impact", SfxShape.Explosion);
        Context.Audio.Define("bump", SfxShape.Hit);

        _cannon = new VoxelCannon(_particles, Context.Audio);
        _ship = new FreeFlyController(new Vector3(0f, 70f, 340f), Context.Settings, yaw: 180f, pitch: -8f);
        _camera = _ship.Update(0f);

        BuildSystem();
    }

    private void BuildSystem()
    {
        CelestialBody ferra = Planet("Ferra", orbit: 300f, scale: 1.6f, speed: 0.100f, tilt: 0.05f,
            radius: 14f, crust: Materials.Iron, core: Materials.IronCore, seed: 11, spin: 0.25f);

        CelestialBody verdis = Planet("Verdis", orbit: 560f, scale: 2.4f, speed: 0.062f, tilt: -0.09f,
            radius: 15f, crust: Materials.Grass, core: Materials.Soil, seed: 27, spin: 0.18f);

        CelestialBody cryon = Planet("Cryon", orbit: 820f, scale: 2.1f, speed: 0.041f, tilt: 0.21f,
            radius: 15f, crust: Materials.Ice, core: Materials.IceCore, seed: 44, spin: 0.12f);

        CelestialBody tharos = Planet("Tharos", orbit: 1180f, scale: 3.0f, speed: 0.027f, tilt: -0.16f,
            radius: 15f, crust: Materials.Basalt, core: Materials.Magma, seed: 63, spin: 0.09f);

        Moon("Kell", verdis, orbit: 120f, scale: 0.75f, speed: 0.42f, tilt: 0.35f, radius: 11f, seed: 71);
        Moon("Orin", tharos, orbit: 165f, scale: 0.9f, speed: 0.30f, tilt: -0.5f, radius: 12f, seed: 88);
        Moon("Vex", tharos, orbit: 240f, scale: 0.6f, speed: 0.21f, tilt: 0.62f, radius: 10f, seed: 95);
    }

    private CelestialBody Planet(string name, float orbit, float scale, float speed, float tilt,
        float radius, byte crust, byte core, int seed, float spin)
    {
        var body = new VoxelBody { Name = name, VoxelScale = scale, SpinSpeed = spin };
        body.FillSphere(radius, crust, core, seed, roughness: 0.14f, coreDepth: 4);

        var celestial = new CelestialBody
        {
            Body = body,
            OrbitRadius = orbit,
            OrbitSpeed = speed,
            OrbitTilt = tilt,
            OrbitPhase = seed * 0.37f,
        };

        celestial.TakeCensus();
        celestial.Advance(0f);
        _bodies.Add(celestial);

        return celestial;
    }

    private void Moon(string name, CelestialBody parent, float orbit, float scale, float speed, float tilt,
        float radius, int seed)
    {
        var body = new VoxelBody { Name = name, VoxelScale = scale, SpinSpeed = 0.4f };
        body.FillSphere(radius, Materials.MoonRock, Materials.MoonRock, seed, roughness: 0.26f, coreDepth: 32);

        var celestial = new CelestialBody
        {
            Body = body,
            Parent = parent,
            OrbitRadius = orbit,
            OrbitSpeed = speed,
            OrbitTilt = tilt,
            OrbitPhase = seed * 0.61f,
        };

        celestial.TakeCensus();
        celestial.Advance(0f);
        _bodies.Add(celestial);
    }

    public override void Update(float dt)
    {
        _time += dt;

        // Planets before their moons: a moon's orbit hangs off its planet's current position
        foreach (CelestialBody body in _bodies)
            body.Advance(dt);

        _camera = _ship.Update(dt);

        if (Raylib.IsKeyPressed(KeyboardKey.F)) _ship.Halt();

        if (Raylib.IsMouseButtonDown(MouseButton.Left)) _cannon.Hold(dt);
        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            _cannon.Release(_ship.Position, _ship.Forward, _ship.Velocity);

        _cannon.Update(dt, _bodies, OnShotHit);
        _particles.Update(null, dt);

        KeepShipOutOfSolids();
    }

    private void OnShotHit(CelestialBody target, Vector3 point, float blastRadius)
    {
        int removed = target.Body.Carve(point, blastRadius);
        target.RegisterCarve(removed);

        _hits++;
        _voxelsDestroyed += removed;

        Vector3 local = target.Body.ToLocal(point);
        Color debris = TerrainColors.ForBlock(
            target.Body.Get((int)local.X, (int)local.Y, (int)local.Z),
            (int)local.X, (int)local.Y, (int)local.Z);

        _cannon.SpawnImpact(point, debris, blastRadius);
    }

    /// <summary>
    /// Collision with the bodies: the ship is pushed straight outwards until it is clear again.
    /// A real physics body would be overkill here; the point is only that you cannot end up
    /// stuck inside a planet.
    /// </summary>
    private void KeepShipOutOfSolids()
    {
        float sunDistance = _ship.Position.Length();
        if (sunDistance < SunRadius + 4f)
        {
            Vector3 outward = sunDistance < 1e-3f ? Vector3.UnitY : _ship.Position / sunDistance;
            _ship.Teleport(outward * (SunRadius + 4f));
            Context.Audio.Play("bump", 0.5f);
            return;
        }

        foreach (CelestialBody celestial in _bodies)
        {
            VoxelBody body = celestial.Body;

            float reach = body.BoundingRadius;
            if (Vector3.DistanceSquared(_ship.Position, body.Position) > reach * reach) continue;
            if (!body.IsSolidAt(_ship.Position)) continue;

            Vector3 delta = _ship.Position - body.Position;
            Vector3 outward = delta.LengthSquared() < 1e-3f ? Vector3.UnitY : Vector3.Normalize(delta);

            // Step outwards until the spot is free, at most as far as the bounding sphere
            Vector3 position = _ship.Position;
            for (int step = 0; step < 64 && body.IsSolidAt(position); step++)
                position += outward * body.VoxelScale;

            _ship.Teleport(position);
            Context.Audio.Play("bump", 0.6f);
            return;
        }
    }

    public override void DrawBackground() => Raylib.ClearBackground(new Color(4, 5, 12, 255));

    public override void DrawWorld()
    {
        _stars.Draw(_camera, 1f, _time);

        DrawOrbits();
        DrawSun();

        _renderer.BeginFrame();

        var sunColor = new Vector3(1.30f, 1.20f, 1.02f);
        var ambient = new Vector3(0.10f, 0.11f, 0.15f);

        foreach (CelestialBody celestial in _bodies)
            _renderer.Draw(celestial.Body, _shader, Vector3.Zero, sunColor, ambient, _camera.Position);

        // Muzzle down and to the right rather than dead centre, or the growing ball hides the target
        _cannon.DrawCharge(_ship.Position + _ship.Forward * 6f + _ship.Right * 1.5f - Vector3.UnitY * 1.1f);
        _cannon.Draw();
        _particles.Draw();
    }

    /// <summary>
    /// Sun with a corona. A single translucent shell would read as a ring; only several shells
    /// with falling opacity give a soft outward falloff.
    /// </summary>
    private void DrawSun()
    {
        Raylib.DrawSphereEx(Vector3.Zero, SunRadius, 24, 24, new Color(255, 232, 150, 255));

        Raylib.BeginBlendMode(BlendMode.Additive);

        ReadOnlySpan<(float Scale, byte Alpha)> corona = stackalloc (float, byte)[]
        {
            (1.03f, 90), (1.08f, 55), (1.15f, 32), (1.26f, 16), (1.42f, 8),
        };

        foreach ((float scale, byte alpha) in corona)
            Raylib.DrawSphereEx(Vector3.Zero, SunRadius * scale, 16, 16, new Color((byte)255, (byte)178, (byte)70, alpha));

        Raylib.EndBlendMode();
    }

    /// <summary>Orbits as thin rings: without them you lose all sense of place out here</summary>
    private void DrawOrbits()
    {
        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Parent != null) continue;

            float tiltDegrees = celestial.OrbitTilt * (180f / MathF.PI);
            Raylib.DrawCircle3D(Vector3.Zero, celestial.OrbitRadius, Vector3.UnitX, 90f - tiltDegrees,
                new Color(70, 110, 150, 90));
        }
    }

    public override void DrawHud()
    {
        int width = Context.ScreenWidth;
        int height = Context.ScreenHeight;

        Hud.Crosshair(width, height, new Color(255, 220, 120, 255));
        DrawChargeMeter(width, height);

        CelestialBody? target = FindTarget();
        if (target != null)
        {
            float distance = Vector3.Distance(_camera.Position, target.Body.Position) - target.Body.BoundingRadius;

            Hud.Text(target.Name, 16, 40, 26, Hud.Accent);
            Hud.Text($"{MathF.Max(0f, distance):0} m", 16, 72, 20);

            Color integrityColor = target.Integrity > 0.6f
                ? new Color(120, 220, 140, 255)
                : target.Integrity > 0.25f ? new Color(240, 200, 90, 255) : new Color(230, 90, 80, 255);

            Hud.Bar(16, 98, 220, 22, target.Integrity, integrityColor, $"{target.Integrity * 100f:0}% intact");
        }

        Hud.Text($"{_ship.Speed:0} m/s{(_ship.Boosting ? "  BOOST" : "")}", 16, height - 74, 20,
            _ship.Boosting ? Hud.Warning : Hud.Ink);
        Hud.Text($"Hits {_hits} | Voxels blasted {_voxelsDestroyed}", 16, height - 48, 18);

        Hud.Centered("Hold LMB to charge | Shift boost | F brake | ESC menu", width / 2, height - 40, 16);

        if (!Context.DebugOverlay) return;

        Hud.Text($"Shots {_cannon.ActiveShots} | Particles {_particles.ActiveParticles}", 16, 130, 18, Color.SkyBlue);
    }

    /// <summary>Charge bar right under the crosshair, where the eye already is while aiming</summary>
    private void DrawChargeMeter(int width, int height)
    {
        if (!_cannon.Charging) return;

        float charge = _cannon.Charge01;
        Color color = charge >= 0.999f
            ? new Color(255, 240, 160, 255)
            : new Color((byte)255, (byte)(150 + 90 * charge), (byte)70, (byte)255);

        Hud.Bar(width / 2 - 70, height / 2 + 26, 140, 10, charge, color);
    }

    /// <summary>
    /// The body being aimed at: the one closest to the line of sight, as long as it is in front
    /// of the ship at all. With no candidate, the nearest one instead — the readout then works as
    /// a signpost rather than a target lock.
    /// </summary>
    private CelestialBody? FindTarget()
    {
        Vector3 forward = _ship.Forward;

        CelestialBody? aimed = null;
        float bestAlignment = 0.985f;

        CelestialBody? nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (CelestialBody celestial in _bodies)
        {
            Vector3 delta = celestial.Body.Position - _camera.Position;
            float distance = delta.Length();
            if (distance < 1e-3f) continue;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = celestial;
            }

            // Big bodies may sit further off the axis and still count as the target
            float alignment = Vector3.Dot(delta / distance, forward);
            float slack = celestial.Body.BoundingRadius / distance * 0.6f;

            if (alignment + slack <= bestAlignment) continue;

            bestAlignment = alignment + slack;
            aimed = celestial;
        }

        return aimed ?? nearest;
    }

    public override void Unload()
    {
        _renderer.Dispose();
        _shader.Unload();

        Rlgl.SetClipPlanes(0.01, 1000.0); // back to the raylib default, or the next game inherits this
    }
}
