using Raylib_cs;
using System.Numerics;
using Terraformer.Effects;
using Terraformer.Rendering;
using Terraformer.World;

namespace Terraformer.Demo;

/// <summary>
/// Gescripteter Ablauf für die Aufnahme (<c>--demo</c>): zwei Raketenstarts mit Einschlag,
/// danach der Wechsel von Blocks direkt auf Smooth (Sculpt wird übersprungen) und eine
/// fallende Bombe im Smooth-Modus. Die Kamera steht leicht erhöht auf dem Boden, damit die
/// einzelnen Voxel groß im Bild sind und der Blick geneigt in die Krater fällt.
/// Die Uhr steht beim Moduswechsel still, bis die Meshes fertig sind — sonst zeigt die
/// Aufnahme eine halb umgebaute Welt. Die Aufnahme läuft ohne Text.
/// </summary>
public sealed class DemoSequence
{
    // Zeitplan in Sekunden; die Aufnahme ist auf 15 s ausgelegt
    private const float Rocket1Launch = 1.0f;
    private const float FlightSeconds = 2.5f;
    private const float Rocket2Launch = 5.0f;
    private const float SwitchAfterImpact = 3.0f;
    private const float BombAfterSwitch = 0.5f;
    private const float BombFallSeconds = 2.0f;
    private const float Outro = 1.5f;

    private const float Impact2At = Rocket2Launch + FlightSeconds;
    private const float SwitchAt = Impact2At + SwitchAfterImpact;
    private const float BombAt = SwitchAt + BombAfterSwitch;
    private const float EndAt = BombAt + BombFallSeconds + Outro;

    // Aus Augenhöhe sieht man nur dann in die Kuhle, wenn die Sichtlinie zum Grund steiler
    // verläuft als die zum nahen Rand — das gilt für h < Tiefe * (Distanz - Radius) / Radius.
    private const float ImpactDistance = 10f;
    private const float CraterRadius = 5.5f;
    private const float PadDistance = 30f;
    private const float ApexHeight = 22f;
    private const float BombDropHeight = 34f;

    /// <summary>Höhe der Kamera über dem Gelände — nach oben begrenzt durch die Formel oben</summary>
    private const float StandHeight = 2f;

    /// <summary>Punkt, um den herum vor dem Bau der Sequenz geladen werden muss</summary>
    public static readonly Vector3 Anchor = new(48f, 40f, 208f);

    private readonly VoxelWorld _world;
    private readonly ParticleSystem _particles;
    private readonly ChunkMeshManager _meshes;

    private readonly Vector3 _viewDirection;
    private readonly Vector3 _side;
    private readonly Shot[] _shots;

    private float _time;
    private bool _switched;
    private bool _waitingForRemesh;
    private float _sway;

    /// <summary>Ein Flugkörper mit seinem Zeitfenster und dem Punkt, an dem er einschlägt</summary>
    private sealed class Shot
    {
        public required Rocket Rocket { get; init; }
        public required Vector3 Impact { get; init; }
        public required float LaunchAt { get; init; }
        public bool Exploded;
    }

    /// <summary>Fester Standpunkt der Kamera: Füße auf dem Gelände, leicht erhöht</summary>
    public Vector3 CameraPosition { get; }

    public bool Finished { get; private set; }
    public Vector3 LookTarget { get; private set; }

    public DemoSequence(VoxelWorld world, ParticleSystem particles, ChunkMeshManager meshes)
    {
        _world = world;
        _particles = particles;
        _meshes = meshes;

        (Vector3 stand, Vector3 direction) = FindViewpoint();
        CameraPosition = stand + Vector3.UnitY * StandHeight;
        _viewDirection = direction;
        _side = Vector3.Cross(Vector3.UnitY, direction);

        // Drei Einschläge nebeneinander statt übereinander — so entsteht eine Kraterlandschaft
        Vector3 impact1 = OnGround(stand + direction * ImpactDistance);
        Vector3 impact2 = OnGround(stand + direction * (ImpactDistance + 1f) + _side * 8f);
        Vector3 impact3 = OnGround(stand + direction * (ImpactDistance - 1f) - _side * 7f);

        _shots = new[]
        {
            Launched(stand, impact1, 0f, Rocket1Launch),
            Launched(stand, impact2, 7f, Rocket2Launch),
            Dropped(impact3, BombAt),
        };

        LookTarget = impact1 + Vector3.UnitY * 3f;
    }

    /// <summary>Rakete, die von einer Rampe im Hintergrund startet und im Bogen einschlägt</summary>
    private Shot Launched(Vector3 stand, Vector3 impact, float sideOffset, float launchAt)
    {
        Vector3 pad = OnGround(stand + _viewDirection * PadDistance + _side * sideOffset);
        Vector3 apexBase = (pad + impact) * 0.5f;
        var apex = new Vector3(apexBase.X, MathF.Max(pad.Y, impact.Y) + ApexHeight, apexBase.Z);

        return new Shot
        {
            Rocket = new Rocket(pad + Vector3.UnitY * 0.9f, apex, impact, FlightSeconds),
            Impact = impact,
            LaunchAt = launchAt,
        };
    }

    /// <summary>Bombe, die schräg von oben herunterkommt</summary>
    private Shot Dropped(Vector3 impact, float releaseAt)
    {
        Vector3 start = impact + Vector3.UnitY * BombDropHeight - _viewDirection * 10f;

        // Scheitel auf Starthöhe: die Bahn bleibt erst oben und fällt dann beschleunigt durch
        var apex = new Vector3((start.X + impact.X) * 0.5f, start.Y, (start.Z + impact.Z) * 0.5f);

        return new Shot
        {
            Rocket = new Rocket(start, apex, impact, BombFallSeconds),
            Impact = impact,
            LaunchAt = releaseAt,
        };
    }

    public void Update(float dt)
    {
        // Der Moduswechsel mesht die ganze Welt neu — Uhr so lange anhalten, aber nur dann:
        // die Explosionen danach machen ebenfalls Chunks dirty und dürfen nicht einfrieren
        if (_waitingForRemesh)
        {
            if (_meshes.PendingChunks > 0) return;
            _waitingForRemesh = false;
        }

        _time += dt;

        if (_time >= SwitchAt && !_switched)
        {
            // Sculpt wird übersprungen: direkt von den Würfeln auf Marching Cubes
            _switched = true;
            _waitingForRemesh = true;
            _world.Mode = TerrainMode.Smooth;
            _meshes.SetSmoothRendering(true);
        }

        foreach (Shot shot in _shots)
        {
            if (_time < shot.LaunchAt) continue;

            if (!shot.Rocket.Landed)
            {
                shot.Rocket.Update(dt, _particles);
                continue;
            }

            if (shot.Exploded) continue;

            shot.Exploded = true;
            Explode(shot.Impact);
        }

        LookTarget = CurrentLookTarget();

        if (_time >= EndAt) Finished = true;
    }

    public void Draw3D()
    {
        foreach (Shot shot in _shots)
            if (_time >= shot.LaunchAt) shot.Rocket.Draw();
    }

    private Vector3 CurrentLookTarget()
    {
        // Solange etwas fliegt, hängt die Kamera daran
        foreach (Shot shot in _shots)
            if (_time >= shot.LaunchAt && !shot.Rocket.Landed)
                return shot.Rocket.Position;

        if (_time < Rocket1Launch) return _shots[0].Impact + Vector3.UnitY * 3f;
        if (_time < Rocket2Launch) return _shots[0].Impact - Vector3.UnitY * 0.8f;
        if (_time < SwitchAt) return _shots[1].Impact - Vector3.UnitY * 0.8f;

        // Nach dem Wechsel über das Kraterfeld schwenken — dieselbe Geometrie, andere Darstellung
        _sway += 0.006f;
        Vector3 field = (_shots[0].Impact + _shots[1].Impact + _shots[2].Impact) / 3f;

        return field + _side * (MathF.Sin(_sway) * 3f) - Vector3.UnitY * 1.2f;
    }

    private void Explode(Vector3 impact)
    {
        Color debris = TerrainColors.ForBlock(
            BlockRegistry.Terrain, (int)impact.X, (int)impact.Y, (int)impact.Z);

        _particles.SpawnExplosion(impact, debris, 1.35f);

        // Krater in die Welt schneiden — weiche Flanke, damit er auch im Smooth-Modus rund ist
        var noBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MaxValue));
        _world.SculptBlob(impact, CraterRadius, add: false, edge: 2f, BlockRegistry.Stone, noBounds);
    }

    /// <summary>
    /// Standort und Blickrichtung fürs Bild suchen. Zwei Bedingungen: das Vorfeld muss auf
    /// Kamerahöhe liegen, sonst verschwinden die Krater hinter der nächsten Geländekante,
    /// und dahinter soll es abfallen, damit statt einer Wand ein Horizont im Bild steht.
    /// </summary>
    private (Vector3 Position, Vector3 Direction) FindViewpoint()
    {
        Vector3 bestPosition = OnGround(Anchor);
        Vector3 bestDirection = Vector3.UnitX;
        float bestScore = float.MaxValue;

        for (float offsetZ = -18f; offsetZ <= 18f; offsetZ += 6f)
        for (float offsetX = -18f; offsetX <= 18f; offsetX += 6f)
        {
            Vector3 stand = OnGround(Anchor + new Vector3(offsetX, 0f, offsetZ));

            for (int i = 0; i < 16; i++)
            {
                float angle = i / 16f * MathF.Tau;
                var direction = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
                Vector3 side = Vector3.Cross(Vector3.UnitY, direction);

                // Vorfeld: alle drei Einschlagstellen und der Weg dorthin möglichst auf Kamerahöhe
                float unevenness = 0f;
                for (float distance = 3f; distance <= ImpactDistance + CraterRadius; distance += 3f)
                for (int lateral = -1; lateral <= 1; lateral++)
                    unevenness = MathF.Max(unevenness, MathF.Abs(
                        OnGround(stand + direction * distance + side * (lateral * 7f)).Y - stand.Y));

                if (unevenness > 2f) continue;

                // Hintergrund: was darüber hinausragt, verstellt die Sicht
                float blocking = 0f;
                for (float distance = 20f; distance <= 60f; distance += 10f)
                    blocking += MathF.Max(0f, OnGround(stand + direction * distance).Y - stand.Y);

                float score = unevenness * 4f + blocking;
                if (score >= bestScore) continue;

                bestScore = score;
                bestPosition = stand;
                bestDirection = direction;
            }
        }

        return (bestPosition, bestDirection);
    }

    /// <summary>Setzt einen Punkt auf die Geländeoberfläche an dieser XZ-Position</summary>
    private Vector3 OnGround(Vector3 position)
    {
        int x = (int)MathF.Floor(position.X);
        int z = (int)MathF.Floor(position.Z);

        for (int y = VoxelWorld.WorldHeight - 1; y > 0; y--)
            if (BlockRegistry.IsSolid(_world.GetBlock(x, y, z)))
                return new Vector3(position.X, y + 1f, position.Z);

        return new Vector3(position.X, 1f, position.Z);
    }
}
