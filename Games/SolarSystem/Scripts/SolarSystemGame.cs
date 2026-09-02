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
/// A solar system built from voxel spheres, at a scale where a planet fills the view when you get
/// close. You fly freely between the bodies under their gravity and shoot them apart: every hit
/// takes real voxels out, throws lasting rubble into orbit, and eventually opens the crust down to
/// the core. Two of the planets have a molten core that does not appreciate being shot at.
/// </summary>
public sealed class SolarSystemGame : Game
{
    private const float SunRadius = 1500f;
    private const float SunSurfaceGravity = 130f;

    // The system is tens of kilometres across, so the raylib defaults (0.01 / 1000) would clip
    // away everything but the body directly in front of the ship
    private const double NearClipPlane = 2.0;
    private const double FarClipPlane = 260000.0;

    /// <summary>Thickness of the crust in voxels; below it sits the core material</summary>
    private const int CrustDepth = 6;

    /// <summary>
    /// Planets with a molten core get a thicker shell than a single full charge can punch through,
    /// so reaching the core takes a second shot into the same crater rather than one lucky hit.
    /// </summary>
    private const int VolatileCrustDepth = 22;

    /// <summary>The crust caves into the breached core before anything is thrown outwards</summary>
    private const float CollapseSeconds = 0.5f;

    private const float BlastSeconds = 2.8f;
    private const float DetonationSeconds = CollapseSeconds + BlastSeconds;

    /// <summary>The blast eats outwards in shells, because carving is a whole-volume sweep</summary>
    private const int DetonationSteps = 16;

    /// <summary>Roughly one chunk of rubble per this many destroyed voxels</summary>
    private const int VoxelsPerDebrisChunk = 420;

    /// <summary>Radius factor and opacity of the fireball's shells, brightest at the heart</summary>
    private static readonly (float Scale, float Alpha)[] _detonationShells =
    {
        (0.45f, 200f), (0.75f, 90f), (1.05f, 35f),
    };

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
        public static readonly byte Ash = BlockRegistry.Register("Ash", new Color(112, 100, 96, 255));
        public static readonly byte Sand = BlockRegistry.Register("Sand", new Color(206, 178, 118, 255));
        public static readonly byte Sandstone = BlockRegistry.Register("Sandstone", new Color(158, 126, 86, 255));
        public static readonly byte Magma = BlockRegistry.Register("Magma", new Color(232, 112, 48, 255), emissive: 0.7f);
        public static readonly byte MoonRock = BlockRegistry.Register("Moon rock", new Color(168, 166, 172, 255));
        public static readonly byte IceMoon = BlockRegistry.Register("Frozen moon", new Color(182, 206, 222, 255));
        public static readonly byte RustMoon = BlockRegistry.Register("Rust moon", new Color(160, 106, 78, 255));
    }

    private readonly List<CelestialBody> _bodies = new();

    private FreeFlyController _ship = null!;
    private VoxelCannon _cannon = null!;
    private DebrisField _debris = null!;
    private ShockwaveField _shockwaves = null!;
    private VoxelBodyRenderer _renderer = null!;
    private TerrainShader _shader = null!;
    private ParticleSystem _particles = null!;
    private StarField _stars = null!;

    private Camera3D _camera;
    private float _time;
    private int _hits;
    private int _voxelsDestroyed;
    private float _flash;

    public override Camera3D Camera => _camera;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: fly, Space/Ctrl: climb and descend",
        "Shift: afterburner. Nothing slows you down out here, so watch your speed",
        "LMB: hold to charge a round, release to fire. A full charge cracks a crust open",
        "F: full stop | F3: debug | ESC: menu",
    };

    public override void Load()
    {
        Rlgl.SetClipPlanes(NearClipPlane, FarClipPlane);

        _shader = new TerrainShader
        {
            // Fog makes no sense in vacuum, so its range sits far behind every orbit
            FogStart = 180000f,
            FogEnd = 250000f,
        };

        _renderer = new VoxelBodyRenderer();
        _particles = new ParticleSystem();
        _debris = new DebrisField(_particles);
        _shockwaves = new ShockwaveField();
        _stars = new StarField(fullSphere: true, distance: 160000f, starCount: 900);

        Context.Audio.Define("shot", SfxShape.Shot);
        Context.Audio.Define("impact", SfxShape.Explosion);
        Context.Audio.Define("bump", SfxShape.Hit);
        Context.Audio.Define("detonate", new SfxShape(2.2f, 150f, 25f, 0.95f, 1.8f));

        _cannon = new VoxelCannon(_particles, Context.Audio);

        BuildSystem();

        // Meshing every body up front: without it the system would visibly pop into existence
        foreach (CelestialBody body in _bodies)
            _renderer.Prewarm(body.Body);

        StartNearFirstPlanet();
    }

    private void BuildSystem()
    {
        CelestialBody ferra = Planet("Ferra", orbit: 7000f, grid: 64, radius: 29f, scale: 18f,
            speed: 0.055f, tilt: 0.04f, spin: 0.05f, gravity: 70f,
            crust: Materials.Iron, core: Materials.IronCore, seed: 11);

        CelestialBody verdis = Planet("Verdis", orbit: 11500f, grid: 96, radius: 44f, scale: 16f,
            speed: 0.038f, tilt: -0.08f, spin: 0.04f, gravity: 90f,
            crust: Materials.Grass, core: Materials.Soil, seed: 27);

        CelestialBody cryon = Planet("Cryon", orbit: 16500f, grid: 96, radius: 44f, scale: 14f,
            speed: 0.027f, tilt: 0.19f, spin: 0.03f, gravity: 80f,
            crust: Materials.Ice, core: Materials.IceCore, seed: 44);

        CelestialBody tharos = Planet("Tharos", orbit: 23000f, grid: 96, radius: 44f, scale: 22f,
            speed: 0.019f, tilt: -0.13f, spin: 0.025f, gravity: 110f,
            crust: Materials.Basalt, core: Materials.Magma, seed: 63, volatileCore: true);

        CelestialBody ashkar = Planet("Ashkar", orbit: 31000f, grid: 64, radius: 29f, scale: 20f,
            speed: 0.014f, tilt: 0.27f, spin: 0.06f, gravity: 75f,
            crust: Materials.Ash, core: Materials.Magma, seed: 81, volatileCore: true);

        CelestialBody nyx = Planet("Nyx", orbit: 40000f, grid: 96, radius: 44f, scale: 18f,
            speed: 0.010f, tilt: -0.22f, spin: 0.02f, gravity: 95f,
            crust: Materials.Sand, core: Materials.Sandstone, seed: 97);

        Moon("Kell", verdis, orbit: 2100f, grid: 32, radius: 14f, scale: 11f, speed: 0.20f, tilt: 0.32f,
            material: Materials.MoonRock, seed: 71);
        Moon("Dun", verdis, orbit: 3400f, grid: 32, radius: 12f, scale: 8f, speed: 0.13f, tilt: -0.44f,
            material: Materials.RustMoon, seed: 74);

        Moon("Sill", cryon, orbit: 2600f, grid: 32, radius: 14f, scale: 10f, speed: 0.16f, tilt: 0.51f,
            material: Materials.IceMoon, seed: 78);

        Moon("Orin", tharos, orbit: 3000f, grid: 64, radius: 27f, scale: 9f, speed: 0.14f, tilt: -0.36f,
            material: Materials.MoonRock, seed: 88);
        Moon("Vex", tharos, orbit: 4600f, grid: 32, radius: 13f, scale: 9f, speed: 0.09f, tilt: 0.58f,
            material: Materials.RustMoon, seed: 95);

        Moon("Ember", ashkar, orbit: 2200f, grid: 32, radius: 13f, scale: 8f, speed: 0.19f, tilt: -0.6f,
            material: Materials.MoonRock, seed: 102);

        Moon("Thale", nyx, orbit: 2800f, grid: 32, radius: 14f, scale: 12f, speed: 0.12f, tilt: 0.24f,
            material: Materials.IceMoon, seed: 109);
        Moon("Bram", nyx, orbit: 4200f, grid: 32, radius: 11f, scale: 9f, speed: 0.08f, tilt: -0.47f,
            material: Materials.MoonRock, seed: 115);

        Moon("Halo", ferra, orbit: 1600f, grid: 32, radius: 11f, scale: 7f, speed: 0.24f, tilt: 0.4f,
            material: Materials.IceMoon, seed: 121);
    }

    private CelestialBody Planet(string name, float orbit, int grid, float radius, float scale,
        float speed, float tilt, float spin, float gravity, byte crust, byte core, int seed,
        bool volatileCore = false)
    {
        var body = new VoxelBody(name, grid, scale, spin);
        body.FillSphere(radius, crust, core, seed, roughness: 0.10f,
            crustDepth: volatileCore ? VolatileCrustDepth : CrustDepth);

        var celestial = new CelestialBody
        {
            Body = body,
            OrbitRadius = orbit,
            OrbitSpeed = speed,
            OrbitTilt = tilt,
            OrbitPhase = seed * 0.37f,
            SurfaceGravity = gravity,
            VolatileCore = volatileCore ? core : BlockRegistry.Air,
        };

        celestial.TakeCensus();
        celestial.Advance(0f);
        _bodies.Add(celestial);

        return celestial;
    }

    private void Moon(string name, CelestialBody parent, float orbit, int grid, float radius, float scale,
        float speed, float tilt, byte material, int seed)
    {
        var body = new VoxelBody(name, grid, scale, 0.12f);
        body.FillSphere(radius, material, material, seed, roughness: 0.22f, crustDepth: grid);

        var celestial = new CelestialBody
        {
            Body = body,
            Parent = parent,
            OrbitRadius = orbit,
            OrbitSpeed = speed,
            OrbitTilt = tilt,
            OrbitPhase = seed * 0.61f,
            SurfaceGravity = 25f,
        };

        celestial.TakeCensus();
        celestial.Advance(0f);
        _bodies.Add(celestial);
    }

    /// <summary>Start next to a planet rather than in empty space, so the scale reads immediately</summary>
    private void StartNearFirstPlanet()
    {
        CelestialBody first = _bodies[0];
        float radius = first.Body.SurfaceRadius;

        _ship = new FreeFlyController(
            first.Body.Position + new Vector3(radius * 1.4f, radius * 0.9f, radius * 3.6f),
            Context.Settings)
        {
            Thrust = 260f,
            BoostMultiplier = 5f,
            MaxSpeed = 1400f,

            // A vacuum does not slow you down, and an orbit only survives without damping
            Damping = 0f,
        };

        _ship.PointAt(first.Body.Position);
        _camera = _ship.Update(0f);
    }

    public override void Update(float dt)
    {
        _time += dt;
        _flash = MathF.Max(0f, _flash - dt * 1.4f);

        // Planets before their moons: a moon's orbit hangs off its planet's current position
        foreach (CelestialBody body in _bodies)
            body.Advance(dt);

        UpdateDetonations(dt);

        _ship.ExternalAcceleration = GravityAt(_ship.Position);
        _camera = _ship.Update(dt);

        if (Raylib.IsKeyPressed(KeyboardKey.F)) _ship.Halt();

        if (Raylib.IsMouseButtonDown(MouseButton.Left)) _cannon.Hold(dt);
        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            _cannon.Release(_ship.Position, _ship.Forward, _ship.Velocity);

        _cannon.Update(dt, TryHit);
        _debris.Update(dt, GravityAt, IsInsideSolid);
        _shockwaves.Update(dt);
        _particles.Update(null, dt);

        KeepShipOutOfSolids();
    }

    /// <summary>Everything a round can run into: loose rubble first, then the bodies themselves</summary>
    private bool TryHit(Vector3 point, float blastRadius, float shotSize)
    {
        int chunk = _debris.FindHit(point, shotSize * 0.5f);
        if (chunk >= 0)
        {
            (Vector3 position, Color color, float size) = _debris.Shatter(chunk);
            _particles.SpawnExplosion(position, color, size * 0.08f);
            _hits++;
            return true;
        }

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed) continue;

            // Cheap bounding-sphere test before converting the point into the voxel grid
            float reach = celestial.Body.BoundingRadius;
            if (Vector3.DistanceSquared(point, celestial.Body.Position) > reach * reach) continue;
            if (!celestial.Body.IsSolidAt(point)) continue;

            HitBody(celestial, point, blastRadius);
            return true;
        }

        return false;
    }

    private void HitBody(CelestialBody target, Vector3 point, float blastRadius)
    {
        Vector3 local = target.Body.ToLocal(point);
        Color debris = TerrainColors.ForBlock(
            target.Body.Get((int)local.X, (int)local.Y, (int)local.Z),
            (int)local.X, (int)local.Y, (int)local.Z);

        int removed = target.Body.Carve(point, blastRadius, target.VolatileCore, out int coreHits);
        target.RegisterCarve(removed);

        _hits++;
        _voxelsDestroyed += removed;

        _cannon.SpawnImpact(point, debris, blastRadius);

        // Rubble flies off along the surface normal, carried along by the body's own motion
        Vector3 outward = Vector3.Normalize(point - target.Body.Position);
        _debris.Spawn(
            point + outward * blastRadius * 0.3f,
            target.Velocity,
            debris,
            target.Body.VoxelScale * 1.4f,
            blastRadius * 0.5f,
            Math.Clamp(removed / 60, 2, 14));

        if (coreHits > 0) BeginDetonation(target);
    }

    private void BeginDetonation(CelestialBody target)
    {
        if (target.Detonating) return;

        target.Detonate();
        Context.Audio.Play("detonate", 1f, 0.9f);
        _flash = 1f;
    }

    /// <summary>
    /// A breached core takes the planet apart from the inside. First the crust falls inwards for
    /// half a second, then a shell eats its way out: every step carves a larger sphere and hands
    /// back a sample of the voxels it removed, which become the rubble flying away. The material
    /// you see leaving is the material that was actually there.
    ///
    /// Carving is a sweep over the whole grid, so it runs in a handful of steps rather than every
    /// frame.
    /// </summary>
    private void UpdateDetonations(float dt)
    {
        foreach (CelestialBody celestial in _bodies)
        {
            if (!celestial.Detonating || celestial.Destroyed) continue;

            celestial.AdvanceDetonation(dt);

            if (celestial.DetonationTime < CollapseSeconds)
            {
                CollapseInward(celestial, dt);
                continue;
            }

            float progress = Math.Clamp((celestial.DetonationTime - CollapseSeconds) / BlastSeconds, 0f, 1f);
            int step = (int)(progress * DetonationSteps);

            while (celestial.DetonationStep < step)
            {
                celestial.DetonationStep++;

                // The front leaves with the first shell and then outruns everything it threw
                if (celestial.DetonationStep == 1)
                    _shockwaves.Spawn(celestial.Body.Position, celestial.Velocity, celestial.Body.SurfaceRadius);

                BlastShell(celestial, celestial.DetonationStep);
            }

            if (progress >= 1f) celestial.MarkDestroyed();
        }
    }

    /// <summary>The implosion: crust breaks off the surface and falls towards the core</summary>
    private void CollapseInward(CelestialBody celestial, float dt)
    {
        VoxelBody body = celestial.Body;

        int count = (int)MathF.Ceiling(dt * 50f);

        for (int i = 0; i < count; i++)
        {
            Vector3 direction = RandomDirection();

            // Just below the surface, or the rough terrain leaves the sample in empty space
            Vector3 position = body.Position + direction * (body.SurfaceRadius * 0.96f);
            Vector3 local = body.ToLocal(position);

            int block = body.Get((int)local.X, (int)local.Y, (int)local.Z);
            if (!BlockRegistry.IsSolid(block)) continue;

            Color color = TerrainColors.ForBlock(block, (int)local.X, (int)local.Y, (int)local.Z);

            _debris.SpawnAt(
                position,
                celestial.Velocity - direction * (130f + Random.Shared.NextSingle() * 140f),
                color,
                body.VoxelScale * 1.1f,
                grace: CollapseSeconds + 0.6f);
        }
    }

    /// <summary>One shell of the outward blast: carve it away and throw what was in it</summary>
    private void BlastShell(CelestialBody celestial, int step)
    {
        VoxelBody body = celestial.Body;

        // Measured against the visible surface, not the bounding sphere: scaled off the latter the
        // planet is gone halfway through and the rest of the blast throws nothing
        float fraction = step / (float)DetonationSteps;
        float radius = body.SurfaceRadius * 1.08f * fraction;

        // Material from deep down is thrown hardest; the outer crust is only shouldered aside
        float speed = 900f - 520f * fraction;

        Vector3 centre = body.Position;
        Vector3 inherited = celestial.Velocity;

        int removed = body.Carve(centre, radius, BlockRegistry.Air, out _, (position, block) =>
        {
            Vector3 outward = position - centre;
            float distance = outward.Length();
            outward = distance < 1e-3f ? RandomDirection() : outward / distance;

            Vector3 local = body.ToLocal(position);
            Color color = TerrainColors.ForBlock(block, (int)local.X, (int)local.Y, (int)local.Z);

            _debris.SpawnAt(
                position,
                inherited + outward * (speed * (0.75f + Random.Shared.NextSingle() * 0.5f)),
                color,
                body.VoxelScale * 1.3f,
                grace: 1.2f);
        }, VoxelsPerDebrisChunk);

        celestial.RegisterCarve(removed);
        _voxelsDestroyed += removed;

        _particles.SpawnExplosion(centre + RandomDirection() * radius * 0.8f,
            new Color(255, 150, 60, 255), radius / 30f);
    }

    /// <summary>The pull of every body plus the sun, which is what makes an orbit possible</summary>
    private Vector3 GravityAt(Vector3 point)
    {
        Vector3 sum = Vector3.Zero;

        foreach (CelestialBody celestial in _bodies)
            sum += celestial.GravityAt(point);

        float distance = point.Length();
        if (distance > 1e-3f)
        {
            float effective = MathF.Max(distance, SunRadius);
            sum -= point / distance * (SunSurfaceGravity * SunRadius * SunRadius / (effective * effective));
        }

        return sum;
    }

    private bool IsInsideSolid(Vector3 point)
    {
        if (point.LengthSquared() < SunRadius * SunRadius) return true;

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed) continue;

            float reach = celestial.Body.BoundingRadius;
            if (Vector3.DistanceSquared(point, celestial.Body.Position) > reach * reach) continue;
            if (celestial.Body.IsSolidAt(point)) return true;
        }

        return false;
    }

    /// <summary>
    /// Collision with the bodies: the ship is pushed straight outwards until it is clear again.
    /// A real physics body would be overkill here; the point is only that you cannot end up
    /// stuck inside a planet.
    /// </summary>
    private void KeepShipOutOfSolids()
    {
        float sunDistance = _ship.Position.Length();
        if (sunDistance < SunRadius + 40f)
        {
            Vector3 outward = sunDistance < 1e-3f ? Vector3.UnitY : _ship.Position / sunDistance;
            _ship.Teleport(outward * (SunRadius + 40f));
            Context.Audio.Play("bump", 0.5f);
            return;
        }

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed) continue;

            VoxelBody body = celestial.Body;

            float reach = body.BoundingRadius;
            if (Vector3.DistanceSquared(_ship.Position, body.Position) > reach * reach) continue;
            if (!body.IsSolidAt(_ship.Position)) continue;

            Vector3 delta = _ship.Position - body.Position;
            Vector3 outward = delta.LengthSquared() < 1e-3f ? Vector3.UnitY : Vector3.Normalize(delta);

            // Step outwards until the spot is free, at most as far as the bounding sphere
            Vector3 position = _ship.Position;
            for (int step = 0; step < 96 && body.IsSolidAt(position); step++)
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

        var sunColor = new Vector3(1.35f, 1.24f, 1.05f);
        var ambient = new Vector3(0.07f, 0.08f, 0.12f);

        Span<Vector4> casters = stackalloc Vector4[TerrainShader.MaxShadowCasters];

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed) continue;

            int count = CollectShadowCasters(celestial, casters);
            _renderer.Draw(celestial.Body, _shader, Vector3.Zero, sunColor, ambient, _camera.Position, casters[..count]);
        }

        DrawDetonations();

        _cannon.Draw();
        _debris.Draw();
        _shockwaves.Draw();
        _particles.Draw();
    }

    /// <summary>
    /// The bodies that can put a shadow on this one: near neighbours that sit on its sunward side.
    /// In practice that means a planet and its moons, which is exactly where a shadow shows.
    /// </summary>
    private int CollectShadowCasters(CelestialBody target, Span<Vector4> buffer)
    {
        Vector3 position = target.Body.Position;
        float distanceToSun = position.Length();
        if (distanceToSun < 1e-3f) return 0;

        Vector3 toSun = -position / distanceToSun;
        float range = target.Body.SurfaceRadius * 16f;

        int count = 0;

        foreach (CelestialBody other in _bodies)
        {
            if (ReferenceEquals(other, target) || other.Destroyed) continue;

            Vector3 relative = other.Body.Position - position;
            if (relative.LengthSquared() > range * range) continue;

            // Anything behind the target cannot stand between it and the sun
            if (Vector3.Dot(relative, toSun) <= 0f) continue;

            buffer[count++] = new Vector4(other.Body.Position, other.Body.SurfaceRadius);
            if (count == buffer.Length) break;
        }

        return count;
    }

    private void DrawDetonations()
    {
        Raylib.BeginBlendMode(BlendMode.Additive);

        foreach (CelestialBody celestial in _bodies)
        {
            if (!celestial.Detonating || celestial.Destroyed) continue;

            if (celestial.DetonationTime < CollapseSeconds)
            {
                // Light building up in the breached core while the planet is still whole
                float glow = celestial.DetonationTime / CollapseSeconds;

                Raylib.DrawSphereEx(celestial.Body.Position, celestial.Body.CoreRadius * (0.25f + glow * 0.6f), 12, 12,
                    new Color((byte)255, (byte)220, (byte)140, (byte)(140 * glow)));
                continue;
            }

            // Only the bright heart of the blast lives here; the front that races outwards is the
            // ShockwaveField's job, and one big sphere on top of it would just wash the rubble out
            float progress = Math.Clamp((celestial.DetonationTime - CollapseSeconds) / BlastSeconds, 0f, 1f);
            float fade = (1f - progress) * (1f - progress);
            float radius = celestial.Body.SurfaceRadius * (0.2f + progress * 0.45f);

            foreach ((float scale, float alpha) in _detonationShells)
                Raylib.DrawSphereEx(celestial.Body.Position, radius * scale, 14, 14,
                    new Color((byte)255, (byte)(200 - 90 * progress), (byte)90, (byte)(alpha * fade)));
        }

        Raylib.EndBlendMode();
    }

    /// <summary>
    /// Sun with a corona. A single translucent shell would read as a ring; only several shells
    /// with falling opacity give a soft outward falloff.
    /// </summary>
    private void DrawSun()
    {
        Raylib.DrawSphereEx(Vector3.Zero, SunRadius, 32, 32, new Color(255, 232, 150, 255));

        Raylib.BeginBlendMode(BlendMode.Additive);

        ReadOnlySpan<(float Scale, byte Alpha)> corona = stackalloc (float, byte)[]
        {
            (1.03f, 90), (1.08f, 55), (1.15f, 32), (1.26f, 16), (1.42f, 8),
        };

        foreach ((float scale, byte alpha) in corona)
            Raylib.DrawSphereEx(Vector3.Zero, SunRadius * scale, 20, 20, new Color((byte)255, (byte)178, (byte)70, alpha));

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

        if (_flash > 0f)
            Raylib.DrawRectangle(0, 0, width, height, new Color((byte)255, (byte)220, (byte)170, (byte)(150 * _flash)));

        Hud.Crosshair(width, height, new Color(255, 220, 120, 255));
        DrawChargeMeter(width, height);

        CelestialBody? target = FindTarget();
        if (target != null)
        {
            float altitude = Vector3.Distance(_camera.Position, target.Body.Position) - target.Body.SurfaceRadius;

            Hud.Text(target.Name, 16, 40, 26, Hud.Accent);
            Hud.Text($"{MathF.Max(0f, altitude):0} m", 16, 72, 20);

            Color integrityColor = target.Integrity > 0.6f
                ? new Color(120, 220, 140, 255)
                : target.Integrity > 0.25f ? new Color(240, 200, 90, 255) : new Color(230, 90, 80, 255);

            Hud.Bar(16, 98, 220, 22, target.Integrity, integrityColor, $"{target.Integrity * 100f:0}% intact");

            if (target.Detonating) Hud.Text("CORE BREACH", 16, 126, 22, Hud.Warning);
            else if (target.HasVolatileCore) DrawCoreProgress(target);
        }

        float gravity = GravityAt(_ship.Position).Length();

        Hud.Text($"{_ship.Speed:0} m/s{(_ship.Boosting ? "  BOOST" : "")}", 16, height - 98, 20,
            _ship.Boosting ? Hud.Warning : Hud.Ink);
        Hud.Text($"gravity {gravity:0.0} m/s2", 16, height - 72, 18,
            gravity > 20f ? Hud.Warning : Hud.Ink);
        Hud.Text($"Hits {_hits} | Voxels blasted {_voxelsDestroyed}", 16, height - 46, 18);

        Hud.Centered("Hold LMB to charge | Shift boost | F full stop | ESC menu", width / 2, height - 40, 16);

        if (!Context.DebugOverlay) return;

        Hud.Text($"Shots {_cannon.ActiveShots} | Debris {_debris.Count} | " +
                 $"Waves {_shockwaves.Count} | Particles {_particles.ActiveParticles}",
            16, 184, 18, Color.SkyBlue);
    }

    /// <summary>
    /// How far the crater you are aiming into has already eaten through the crust. This is the only
    /// cue a player gets that a planet with a molten core is worth digging at the same spot twice:
    /// the core itself goes off the moment a round touches it, so it is never seen beforehand.
    /// </summary>
    private void DrawCoreProgress(CelestialBody target)
    {
        Hud.Text("MOLTEN CORE", 16, 126, 20, new Color(255, 170, 70, 255));

        float progress = CrustProgress(target);
        Hud.Bar(16, 152, 220, 18, progress, new Color((byte)230, (byte)(140 - 60 * progress), (byte)60, (byte)255),
            $"crust {progress * 100f:0}%");
    }

    /// <summary>Depth of the first solid point along the line of sight, as a fraction of the crust</summary>
    private float CrustProgress(CelestialBody target)
    {
        VoxelBody body = target.Body;

        float crust = body.SurfaceRadius - body.CoreRadius;
        if (crust <= 0f) return 0f;

        // Only walk the stretch that can possibly be inside the body
        float toCentre = Vector3.Distance(_camera.Position, body.Position);
        float start = MathF.Max(0f, toCentre - body.BoundingRadius);
        float end = toCentre + body.BoundingRadius;

        Vector3 forward = _ship.Forward;

        for (float travelled = start; travelled < end; travelled += 8f)
        {
            Vector3 point = _camera.Position + forward * travelled;
            if (!body.IsSolidAt(point)) continue;

            float depth = body.SurfaceRadius - Vector3.Distance(point, body.Position);
            return Math.Clamp(depth / crust, 0f, 1f);
        }

        return 0f;
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
            if (celestial.Destroyed) continue;

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
            float slack = celestial.Body.SurfaceRadius / distance * 0.8f;

            if (alignment + slack <= bestAlignment) continue;

            bestAlignment = alignment + slack;
            aimed = celestial;
        }

        return aimed ?? nearest;
    }

    private static Vector3 RandomDirection()
    {
        while (true)
        {
            var candidate = new Vector3(
                Random.Shared.NextSingle() * 2f - 1f,
                Random.Shared.NextSingle() * 2f - 1f,
                Random.Shared.NextSingle() * 2f - 1f);

            float lengthSquared = candidate.LengthSquared();
            if (lengthSquared is > 0.0001f and <= 1f) return candidate / MathF.Sqrt(lengthSquared);
        }
    }

    public override void Unload()
    {
        _renderer.Dispose();
        _shader.Unload();

        Rlgl.SetClipPlanes(0.01, 1000.0); // back to the raylib default, or the next game inherits this
    }
}
