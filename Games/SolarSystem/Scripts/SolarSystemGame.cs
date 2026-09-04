using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Core;
using VoxelEngine.Effects;
using VoxelEngine.Input;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Games.SolarSystem;

/// <summary>
/// A solar system built from voxel spheres, at a scale where a planet fills the view when you get
/// close. You fly between the bodies under their gravity with all six degrees of freedom and no
/// up, and shoot them apart: every hit takes real voxels out, throws lasting rubble into orbit,
/// and eventually opens the crust down to the core. Two of the planets have a molten core that
/// does not appreciate being shot at.
///
/// Most planets have an atmosphere. Drop into one and the stars go, the sky takes its colour,
/// the air slows the ship, and coming in fast heats the hull. Slow down against the ground and
/// the ship parks there, carried along by the planet, until the next push of the throttle.
/// </summary>
[GameDefinition("SolarSystem", "Solar System", "Fly out and blast the planets back into voxels")]
public sealed class SolarSystemGame : Game
{
    private const float SunRadius = 4200f;
    private const float SunSurfaceGravity = 130f;

    private const double NearClipPlane = 2.0;
    private const double FarClipPlane = 260000.0;

    /// <summary>Thickness of the crust in voxels; below it sits the core material</summary>
    private const int CrustDepth = 9;

    /// <summary>
    /// Planets with a molten core get a thicker shell than a single full charge can punch through,
    /// so reaching the core takes a second shot into the same crater rather than one lucky hit.
    /// </summary>
    private const int VolatileCrustDepth = 40;

    /// <summary>The crust caves into the breached core before anything is thrown outwards</summary>
    private const float CollapseSeconds = 0.5f;

    private const float BlastSeconds = 1.5f;
    private const float DetonationSeconds = CollapseSeconds + BlastSeconds;

    /// <summary>The blast eats outwards in shells, because carving is a whole-volume sweep</summary>
    private const int DetonationSteps = 10;

    /// <summary>Roughly one chunk of rubble per this many destroyed voxels</summary>
    private const int VoxelsPerDebrisChunk = 420;

    /// <summary>Radius factor and share of the opacity per shell of an atmosphere, densest at the ground</summary>
    private static readonly (float Scale, float Share)[] _atmosphereShells = { (1.03f, 1f), (1.07f, 0.55f), (1.13f, 0.28f) };

    /// <summary>Radius factor and opacity of the fireball's shells, brightest at the heart</summary>
    private static readonly (float Scale, float Alpha)[] _detonationShells =
    {
        (0.45f, 200f), (0.75f, 90f), (1.05f, 35f),
    };

    /// <summary>The planet materials; registering a name twice hands back the same id, so this is safe per Load as well</summary>
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

    private const string SettingsKey = "SolarSystem/Ship";

    /// <summary>Up to this speed against a surface the ship parks instead of bouncing off</summary>
    private const float LandingSpeed = 90f;

    /// <summary>How far the hull reaches around the cockpit</summary>
    private const float ShipClearance = 6f;

    /// <summary>The ship's headlight, for the night side and the bottom of a crater</summary>
    private const float HeadlightRange = 320f;

    /// <summary>Share of the velocity the air takes per second at ground level</summary>
    private const float AirDrag = 0.45f;

    /// <summary>Speed through thick air from which the hull starts to glow, and the span up to a full glow</summary>
    private const float HeatSpeed = 320f;
    private const float HeatRange = 700f;

    private static readonly Vector3 SpaceColor = new Vector3(4f, 5f, 12f) / 255f;

    private enum Mode { Flying, Landed }

    private readonly List<CelestialBody> _bodies = new();
    private SolarSettings _settings = null!;
    private TuningSection? _tuning;

    private Mode _mode = Mode.Flying;
    private CelestialBody? _landedOn;

    // The atmosphere the ship is in, if any: how much air there is, what the sky looks like from
    // here, and how hot the hull is getting
    private CelestialBody? _airBody;
    private float _air;
    private float _skyMix;
    private Vector3 _skyColor;
    private float _heat;
    private float _heatSoundCooldown;
    private Vector3 _shipLocal;
    private Vector3 _shipPreviousPosition;
    private OrbitField _rings = null!;
    private OrbitField _belt = null!;

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
        "WASD + mouse: fly, Space/Ctrl: up and down, Q/E: roll. There is no horizon: pull up long enough and you loop",
        "Shift: afterburner. Nothing slows you down in vacuum, so watch your speed",
        "LMB: hold to charge a round, release to fire. A full charge cracks a crust open",
        "Drop into an atmosphere slowly: the air brakes the ship, and a fast entry burns",
        "Come in slowly against the ground and the ship parks there; any thrust takes off again",
        "F: full stop | F3: debug | ESC: menu",
    };

    // The system is tens of kilometres across, so the engine's terrain range (0.1 / 1900) would
    // clip away everything but the body directly in front of the ship
    public override (double Near, double Far) ClipPlanes => (NearClipPlane, FarClipPlane);

    public override void Load()
    {
        _settings = Context.Store.Load<SolarSettings>(SettingsKey);
        var defaults = new SolarSettings();
        _tuning = Context.Tuning.AddSection("SHIP", () => Context.Store.Save(SettingsKey, _settings))
            .Value("Thrust", () => _settings.Thrust, v => _settings.Thrust = v, defaults.Thrust, 20f, 50f, 2000f, "0")
            .Value("Boost multiplier", () => _settings.BoostMultiplier, v => _settings.BoostMultiplier = v, defaults.BoostMultiplier, 0.5f, 1f, 12f, "0.0")
            .Value("Max speed", () => _settings.MaxSpeed, v => _settings.MaxSpeed = v, defaults.MaxSpeed, 100f, 200f, 8000f, "0")
            .Value("Full charge s", () => _settings.FullChargeSeconds, v => _settings.FullChargeSeconds = v, defaults.FullChargeSeconds, 0.1f, 0.3f, 5f, "0.0");

        // The fog range is set every frame: far behind every orbit in vacuum, close in an atmosphere
        _shader = new TerrainShader();

        _renderer = new VoxelBodyRenderer();
        _particles = new ParticleSystem();
        _debris = new DebrisField(_particles);
        _shockwaves = new ShockwaveField();
        _stars = new StarField(fullSphere: true, distance: 160000f, starCount: 900);

        Context.Audio.Define("shot", SfxShape.Shot);
        Context.Audio.Define("impact", SfxShape.Explosion);
        Context.Audio.Define("bump", SfxShape.Hit);
        Context.Audio.Define("detonate", new SfxShape(2.2f, 150f, 25f, 0.95f, 1.8f));
        Context.Audio.Define("entry", new SfxShape(0.5f, 110f, 70f, 1f, 1.3f));

        _cannon = new VoxelCannon(_particles, Context.Audio, _settings);

        BuildSystem();

        // Meshing every body up front: without it the system would visibly pop into existence
        foreach (CelestialBody body in _bodies)
            _renderer.Prewarm(body.Body);

        StartNearFirstPlanet();
    }

    /// <summary>
    /// Planets of two to four kilometres across, built from grids of up to 256 voxels a side so
    /// they keep their detail when a crater the size of a town opens in them. Everything scales
    /// with them: the sun, the orbits, and the moons that circle at several planet radii.
    /// </summary>
    private void BuildSystem()
    {
        CelestialBody ferra = Planet("Ferra", orbit: 10000f, grid: 160, radius: 74f, scale: 12f,
            speed: 0.045f, tilt: 0.04f, spin: 0.05f, gravity: 70f,
            crust: Materials.Iron, core: Materials.IronCore, seed: 11,
            atmosphere: new Color(255, 170, 110, 14));

        CelestialBody verdis = Planet("Verdis", orbit: 16000f, grid: 224, radius: 104f, scale: 14f,
            speed: 0.030f, tilt: -0.08f, spin: 0.04f, gravity: 90f,
            crust: Materials.Grass, core: Materials.Soil, seed: 27,
            atmosphere: new Color(120, 180, 255, 20));

        CelestialBody cryon = Planet("Cryon", orbit: 23000f, grid: 224, radius: 104f, scale: 12f,
            speed: 0.022f, tilt: 0.19f, spin: 0.03f, gravity: 80f,
            crust: Materials.Ice, core: Materials.IceCore, seed: 44,
            atmosphere: new Color(190, 225, 255, 16));

        CelestialBody tharos = Planet("Tharos", orbit: 32000f, grid: 256, radius: 120f, scale: 16f,
            speed: 0.015f, tilt: -0.13f, spin: 0.025f, gravity: 110f,
            crust: Materials.Basalt, core: Materials.Magma, seed: 63, volatileCore: true,
            atmosphere: new Color(255, 120, 70, 14));

        CelestialBody ashkar = Planet("Ashkar", orbit: 42000f, grid: 192, radius: 88f, scale: 14f,
            speed: 0.011f, tilt: 0.27f, spin: 0.06f, gravity: 75f,
            crust: Materials.Ash, core: Materials.Magma, seed: 81, volatileCore: true);

        CelestialBody nyx = Planet("Nyx", orbit: 54000f, grid: 256, radius: 120f, scale: 14f,
            speed: 0.008f, tilt: -0.22f, spin: 0.02f, gravity: 95f,
            crust: Materials.Sand, core: Materials.Sandstone, seed: 97,
            atmosphere: new Color(255, 220, 160, 16));

        Moon("Kell", verdis, orbit: 4600f, grid: 64, radius: 28f, scale: 9f, speed: 0.20f, tilt: 0.32f,
            material: Materials.MoonRock, seed: 71);
        Moon("Dun", verdis, orbit: 7400f, grid: 64, radius: 24f, scale: 7f, speed: 0.13f, tilt: -0.44f,
            material: Materials.RustMoon, seed: 74);

        Moon("Sill", cryon, orbit: 5200f, grid: 64, radius: 28f, scale: 8f, speed: 0.16f, tilt: 0.51f,
            material: Materials.IceMoon, seed: 78);

        Moon("Orin", tharos, orbit: 6800f, grid: 96, radius: 44f, scale: 8f, speed: 0.14f, tilt: -0.36f,
            material: Materials.MoonRock, seed: 88);
        Moon("Vex", tharos, orbit: 10500f, grid: 64, radius: 26f, scale: 8f, speed: 0.09f, tilt: 0.58f,
            material: Materials.RustMoon, seed: 95);

        Moon("Ember", ashkar, orbit: 4800f, grid: 64, radius: 26f, scale: 7f, speed: 0.19f, tilt: -0.6f,
            material: Materials.MoonRock, seed: 102);

        Moon("Thale", nyx, orbit: 6200f, grid: 64, radius: 28f, scale: 10f, speed: 0.12f, tilt: 0.24f,
            material: Materials.IceMoon, seed: 109);
        Moon("Bram", nyx, orbit: 9600f, grid: 64, radius: 22f, scale: 8f, speed: 0.08f, tilt: -0.47f,
            material: Materials.MoonRock, seed: 115);

        Moon("Halo", ferra, orbit: 3600f, grid: 64, radius: 22f, scale: 6f, speed: 0.24f, tilt: 0.4f,
            material: Materials.IceMoon, seed: 121);

        // A ring around the giant, and a belt of rubble between the ice world and the giant
        float tharosRadius = tharos.Body.SurfaceRadius;
        _rings = new OrbitField(tharos, tharosRadius * 1.55f, tharosRadius * 2.6f, 900, 8f, 42f,
            new Color(150, 140, 130, 255), tilt: 0.38f, thickness: 40f, seed: 5);
        _belt = new OrbitField(null, 26500f, 29500f, 600, 40f, 170f,
            new Color(120, 100, 85, 255), tilt: 0.04f, thickness: 1200f, seed: 6);
    }

    private CelestialBody Planet(string name, float orbit, int grid, float radius, float scale,
        float speed, float tilt, float spin, float gravity, byte crust, byte core, int seed,
        bool volatileCore = false, Color atmosphere = default)
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
            Atmosphere = atmosphere,
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

    /// <summary>
    /// Start close over the second planet rather than in empty space: at a little over two radii
    /// it fills the view, and the sun and the inner planet hang behind it for scale.
    /// </summary>
    private void StartNearFirstPlanet()
    {
        CelestialBody first = _bodies[1];
        float radius = first.Body.SurfaceRadius;

        _ship = new FreeFlyController(
            first.Body.Position + new Vector3(radius * 1.1f, radius * 0.6f, radius * 1.9f),
            Context.Settings)
        {
            // A vacuum does not slow you down, and an orbit only survives without damping; the
            // air of an atmosphere sets this per frame
            Damping = 0f,
        };
        ApplyShipSettings();

        _ship.PointAt(first.Body.Position);
        _camera = _ship.Update(0f);
    }

    private void ApplyShipSettings()
    {
        _ship.Thrust = _settings.Thrust;
        _ship.BoostMultiplier = _settings.BoostMultiplier;
        _ship.MaxSpeed = _settings.MaxSpeed;
    }

    public override void Update(float dt)
    {
        _time += dt;
        _flash = MathF.Max(0f, _flash - dt * 1.4f);
        ApplyShipSettings();

        // Planets before their moons: a moon's orbit hangs off its planet's current position
        foreach (CelestialBody body in _bodies)
            body.Advance(dt);

        UpdateDetonations(dt);
        UpdateAtmosphere(dt);

        // The ground under the parked ship is going: off, whether you like it or not
        if (_mode == Mode.Landed && _landedOn is { } gone && (gone.Detonating || gone.Destroyed))
            Fling(gone);

        if (_mode == Mode.Flying) UpdateFlying(dt);
        else UpdateLanded(dt);

        _cannon.Update(dt, TryHit);
        _debris.Update(dt, GravityAt, IsInsideSolid);
        _shockwaves.Update(dt);
        _particles.Update(null, dt);
    }

    private void UpdateFlying(float dt)
    {
        _shipPreviousPosition = _ship.Position;
        _ship.ExternalAcceleration = GravityAt(_ship.Position);
        _camera = _ship.Update(dt);

        // A hull in the fire shakes; the ship itself stays where it is
        if (_heat > 0.05f)
        {
            Vector3 jitter = RandomDirection() * (_heat * 2.5f);
            _camera.Position += jitter;
            _camera.Target += jitter;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.F)) _ship.Halt();
        UpdateCannonInput(dt);

        KeepShipOutOfSolids();
        TryLand();
    }

    private void UpdateLanded(float dt)
    {
        CelestialBody body = _landedOn!;
        Vector3 anchor = body.Body.ToWorld(_shipLocal);

        // Mouse look keeps working; any thrust is the take-off
        _ship.ExternalAcceleration = Vector3.Zero;
        _ship.Teleport(anchor);
        _camera = _ship.Update(dt);

        if (_ship.Speed > 0.5f)
        {
            TakeOff(body);
            return;
        }

        _ship.Teleport(anchor);
        UpdateCannonInput(dt);
    }

    private void UpdateCannonInput(float dt)
    {
        if (Raylib.IsMouseButtonDown(MouseButton.Left)) _cannon.Hold(dt);
        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            _cannon.Release(_ship.Position, _ship.Forward, _ship.Velocity);
    }

    /// <summary>
    /// Entering an atmosphere is the arrival: the stars go, the sky takes the haze's colour lit by
    /// the sun, the far bodies fade into it, and the air slows the ship. Come in fast and the hull
    /// heats up.
    /// </summary>
    private void UpdateAtmosphere(float dt)
    {
        _airBody = null;
        float depth = 0f;

        foreach (CelestialBody celestial in _bodies)
        {
            float candidate = celestial.DepthAt(_ship.Position);
            if (candidate <= depth) continue;

            depth = candidate;
            _airBody = celestial;
        }

        // The sky builds quickly from the top of the atmosphere, so crossing into it is a moment;
        // the air itself only gets thick towards the ground
        _skyMix = 1f - (1f - depth) * (1f - depth);
        _air = depth * depth;
        _ship.Damping = _air * AirDrag;

        float relativeSpeed = 0f;
        Vector3 travel = Vector3.Zero;

        if (_airBody != null)
        {
            Vector3 up = Vector3.Normalize(_ship.Position - _airBody.Body.Position);
            Vector3 toSun = -Vector3.Normalize(_airBody.Body.Position);
            float daylight = Math.Clamp(Vector3.Dot(up, toSun) * 1.3f + 0.5f, 0.05f, 1f);

            Color tint = _airBody.Atmosphere;
            _skyColor = new Vector3(tint.R, tint.G, tint.B) / 255f * daylight;

            travel = _ship.Velocity - _airBody.Velocity;
            relativeSpeed = travel.Length();
        }

        float targetHeat = _air * Math.Clamp((relativeSpeed - HeatSpeed) / HeatRange, 0f, 1f);
        _heat += (targetHeat - _heat) * MathF.Min(1f, 3f * dt);

        _heatSoundCooldown = MathF.Max(0f, _heatSoundCooldown - dt);
        if (_heat < 0.08f || _airBody == null || relativeSpeed < 1f) return;

        // Embers come off the hull ahead of the cockpit and streak back past it, off to the
        // side so none of them ends up as a block in front of the eye
        Vector3 heading = travel / relativeSpeed;
        Vector3 side = Vector3.Cross(heading, RandomDirection());
        if (side.LengthSquared() < 1e-4f) side = _ship.Right;
        side = Vector3.Normalize(side) * (10f + Random.Shared.NextSingle() * 8f);

        _particles.SpawnTrail(
            _ship.Position + heading * 30f + side,
            _airBody.Velocity - heading * relativeSpeed * 0.6f,
            new Color((byte)255, (byte)(120 + 80 * _heat), (byte)60, (byte)255),
            0.8f + 1.2f * _heat,
            count: 3);

        if (_heatSoundCooldown > 0f) return;

        Context.Audio.Play("entry", 0.25f + 0.6f * _heat, 0.7f + 0.4f * _heat);
        _heatSoundCooldown = 0.35f;
    }

    /// <summary>
    /// Slow against solid ground, in whatever direction the hull touches it: the ship parks, and
    /// from here on the planet carries it. Works on the open surface and in a crater alike.
    /// </summary>
    private void TryLand()
    {
        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed || celestial.Detonating) continue;

            VoxelBody body = celestial.Body;
            float reach = body.BoundingRadius + ShipClearance * 2f;
            if (Vector3.DistanceSquared(_ship.Position, body.Position) > reach * reach) continue;

            Vector3 relative = _ship.Velocity - celestial.Velocity;
            float relativeSpeed = relative.Length();
            if (relativeSpeed < 0.5f || relativeSpeed > LandingSpeed) continue;

            // Touching means solid within the hull's reach along the way the ship moves
            Vector3 heading = relative / relativeSpeed;
            if (!body.IsSolidAt(_ship.Position + heading * ShipClearance)) continue;

            _shipLocal = body.ToLocal(_ship.Position);
            _landedOn = celestial;
            _mode = Mode.Landed;
            _ship.Halt();

            _particles.SpawnSmoke(_ship.Position + heading * ShipClearance, -heading * 6f, 24, 2.5f);
            Context.Audio.Play("bump", 0.45f, 0.6f);
            return;
        }
    }

    private void TakeOff(CelestialBody body)
    {
        _ship.SetVelocity(body.Velocity + _ship.Velocity);
        _particles.SpawnSmoke(_ship.Position, -_ship.Forward * 4f, 30, 3f);
        Context.Audio.Play("shot", 0.3f, 0.5f);

        _mode = Mode.Flying;
        _landedOn = null;
    }

    /// <summary>The planet under the parked ship is coming apart: off and away</summary>
    private void Fling(CelestialBody body)
    {
        Vector3 up = body.Body.UpAt(_ship.Position);

        _landedOn = null;
        _mode = Mode.Flying;

        _ship.Teleport(_ship.Position + up * 20f);
        _ship.SetVelocity(body.Velocity + up * 400f);
        _flash = MathF.Max(_flash, 0.6f);
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
        // planet is gone halfway through and the rest of the blast throws nothing.
        // The front eases out, so most of the planet leaves in the first moment instead of the
        // last shells crumbling away one by one.
        float linear = step / (float)DetonationSteps;
        float fraction = 1f - (1f - linear) * (1f - linear);
        float radius = body.SurfaceRadius * 1.08f * fraction;

        // Material from deep down is thrown hardest, but even the outer crust leaves in a hurry
        float speed = 1600f - 500f * fraction;

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
    /// Collision with the bodies: a ship that ends up in rock is set back to the last free spot
    /// along the way it came, which works in a crater as well as on the open surface. A real
    /// physics body would be overkill here; the point is only that you cannot end up stuck
    /// inside a wall.
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

            Vector3 velocity = _ship.Velocity;
            Vector3 position = _ship.Position;

            if (!body.IsSolidAt(_shipPreviousPosition))
            {
                // Back along this frame's move until the hull is clear, then stop against the wall
                Vector3 back = _shipPreviousPosition - position;
                for (int step = 1; step <= 16 && body.IsSolidAt(position); step++)
                    position = _ship.Position + back * (step / 16f);
            }
            else
            {
                // No free spot behind us (a wall closed in): push straight outwards as a last resort
                Vector3 delta = position - body.Position;
                Vector3 outward = delta.LengthSquared() < 1e-3f ? Vector3.UnitY : Vector3.Normalize(delta);
                for (int step = 0; step < 96 && body.IsSolidAt(position); step++)
                    position += outward * body.VoxelScale;
            }

            _ship.Teleport(position);
            _ship.SetVelocity(celestial.Velocity + (velocity - celestial.Velocity) * 0.1f);
            Context.Audio.Play("bump", Math.Clamp(velocity.Length() / 400f, 0.2f, 0.8f));
            return;
        }
    }

    /// <summary>The colour of the sky from here: black space, or the atmosphere lit by the sun</summary>
    private Color SkyColor => ToColor(Vector3.Lerp(SpaceColor, _skyColor, _skyMix));

    public override void DrawBackground() => Raylib.ClearBackground(SkyColor);

    public override void DrawWorld()
    {
        // A daytime sky hides the stars long before it is fully there
        _stars.Draw(_camera, Math.Clamp(1f - _skyMix * 2f, 0f, 1f), _time);

        DrawOrbits();
        DrawSun();

        _rings.Draw(_time);
        _belt.Draw(_time);

        _renderer.BeginFrame();

        var sunColor = new Vector3(1.35f, 1.24f, 1.05f);

        // The sky lights the ground from every side; in vacuum only the sun does
        Vector3 ambient = new Vector3(0.07f, 0.08f, 0.12f) + _skyColor * (0.35f * _skyMix);

        // In vacuum the fog sits far behind every orbit. In an atmosphere it closes in and takes
        // the sky's colour, so the horizon and the other bodies fade into the haze.
        float clear = MathF.Pow(1f - _skyMix, 6f);
        _shader.FogEnd = 3500f + (250000f - 3500f) * clear;
        _shader.FogStart = _shader.FogEnd * 0.12f;
        Color fog = SkyColor;

        Span<Vector4> casters = stackalloc Vector4[TerrainShader.MaxShadowCasters];

        // The headlight: on the night side the sun is no help, and a crater is black without it
        ReadOnlySpan<PointLight> headlight = stackalloc PointLight[]
        {
            new PointLight(_camera.Position, HeadlightRange, new Vector3(1.0f, 0.96f, 0.86f)),
        };

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed) continue;

            int count = CollectShadowCasters(celestial, casters);
            _renderer.Draw(celestial.Body, _shader, Vector3.Zero, sunColor, ambient, _camera.Position, casters[..count], headlight, fog);
        }

        DrawDetonations();
        DrawAtmospheres();

        _cannon.Draw();
        _debris.Draw();
        _shockwaves.Draw();
        _particles.Draw();
    }

    /// <summary>
    /// A haze in nested additive shells, brightest at the surface. Drawn without back-face
    /// culling so that from the ground the same shells become the sky's tint overhead.
    /// </summary>
    private void DrawAtmospheres()
    {
        Raylib.BeginBlendMode(BlendMode.Additive);

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Destroyed || celestial.Atmosphere.A == 0) continue;

            Color tint = celestial.Atmosphere;
            float radius = celestial.Body.SurfaceRadius;

            // From inside the haze only the back faces are left to see, so they get drawn there
            bool inside = Vector3.Distance(_camera.Position, celestial.Body.Position) < radius * 1.15f;
            if (inside) Rlgl.DisableBackfaceCulling();

            foreach ((float scale, float share) in _atmosphereShells)
                Raylib.DrawSphereEx(celestial.Body.Position, radius * scale, 24, 24,
                    new Color(tint.R, tint.G, tint.B, (byte)(tint.A * share * (inside ? 1.5f : 1f))));

            if (inside) Rlgl.EnableBackfaceCulling();
        }

        Raylib.EndBlendMode();
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

    /// <summary>Orbits as thin rings: without them you lose all sense of place out here. Gone under a sky.</summary>
    private void DrawOrbits()
    {
        var color = new Color((byte)70, (byte)110, (byte)150, (byte)(90 * (1f - _skyMix)));

        foreach (CelestialBody celestial in _bodies)
        {
            if (celestial.Parent != null) continue;

            float tiltDegrees = celestial.OrbitTilt * (180f / MathF.PI);
            Raylib.DrawCircle3D(Vector3.Zero, celestial.OrbitRadius, Vector3.UnitX, 90f - tiltDegrees, color);
        }
    }

    public override void DrawHud()
    {
        int width = Context.ScreenWidth;
        int height = Context.ScreenHeight;

        if (_flash > 0f)
            Raylib.DrawRectangle(0, 0, width, height, new Color((byte)255, (byte)220, (byte)170, (byte)(150 * _flash)));

        if (_heat > 0.02f)
            Raylib.DrawRectangle(0, 0, width, height, new Color((byte)255, (byte)120, (byte)40, (byte)(120 * _heat)));

        Hud.Crosshair(width, height, new Color(255, 220, 120, 255));
        DrawChargeMeter(width, height);
        DrawAtmosphereReadout(width);

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

        switch (_mode)
        {
            case Mode.Flying:
                Hud.Text($"{_ship.Speed:0} m/s{(_ship.Boosting ? "  BOOST" : "")}", 16, height - 98, 20,
                    _ship.Boosting ? Hud.Warning : Hud.Ink);
                Hud.Text($"gravity {gravity:0.0} m/s2", 16, height - 72, 18,
                    gravity > 20f ? Hud.Warning : Hud.Ink);
                Hud.Centered("Hold LMB to charge | Q/E roll | Shift boost | F full stop | ESC menu", width / 2, height - 40, 16);
                break;

            case Mode.Landed:
                Hud.Text($"PARKED on {_landedOn!.Name}", 16, height - 98, 20, Hud.Accent);
                Hud.Text("the planet carries the ship along", 16, height - 72, 18);
                Hud.Centered("Any thrust takes off | Hold LMB to charge | ESC menu", width / 2, height - 40, 16);
                break;
        }

        Hud.Text($"Hits {_hits} | Voxels blasted {_voxelsDestroyed}", 16, height - 46, 18);

        if (!Context.DebugOverlay) return;

        Hud.Text($"Shots {_cannon.ActiveShots} | Debris {_debris.Count} | " +
                 $"Waves {_shockwaves.Count} | Particles {_particles.ActiveParticles}",
            16, 184, 18, Color.SkyBlue);
    }

    /// <summary>Where the ship is in an atmosphere, top centre, and the warning when the hull burns</summary>
    private void DrawAtmosphereReadout(int width)
    {
        if (_airBody == null || _skyMix < 0.02f) return;

        Color tint = _airBody.Atmosphere;
        var air = new Color(tint.R, tint.G, tint.B, (byte)255);

        Hud.Centered($"ATMOSPHERE OF {_airBody.Name.ToUpperInvariant()}", width / 2, 40, 22, air);
        Hud.Bar(width / 2 - 90, 68, 180, 12, _air, air);

        if (_heat < 0.15f) return;

        // Blinks faster the hotter it gets
        if (MathF.Sin(_time * (8f + 12f * _heat)) < -0.2f) return;
        Hud.Centered("HULL HEATING - SLOW DOWN", width / 2, 90, 20, Hud.Warning);
    }

    private static Color ToColor(Vector3 color) => new(
        (byte)Math.Clamp(color.X * 255f, 0f, 255f),
        (byte)Math.Clamp(color.Y * 255f, 0f, 255f),
        (byte)Math.Clamp(color.Z * 255f, 0f, 255f),
        (byte)255);

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
        if (_tuning != null) Context.Tuning.RemoveSection(_tuning);
        _renderer.Dispose();
        _shader.Unload();
    }
}
