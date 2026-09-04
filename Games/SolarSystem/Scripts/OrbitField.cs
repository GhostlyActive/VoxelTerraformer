using Raylib_cs;
using System.Numerics;

namespace Games.SolarSystem;

/// <summary>
/// Rocks on circular orbits around one centre: the ring of a planet or the belt around the sun.
/// Nothing but positions on a clock, drawn as cubes; hundreds of them cost less than one chunk.
/// The inner ones run faster than the outer ones, the way a real ring shears.
/// </summary>
public sealed class OrbitField
{
    private readonly struct Rock
    {
        public required float Radius { get; init; }
        public required float Phase { get; init; }
        public required float AngularSpeed { get; init; }
        public required float Lift { get; init; }
        public required float Size { get; init; }
        public required Color Color { get; init; }
        public required Vector3 SpinAxis { get; init; }
        public required float SpinSpeed { get; init; }
    }

    private readonly Rock[] _rocks;
    private readonly float _tilt;

    /// <summary>The body the rocks circle, or null for the sun at the origin</summary>
    public CelestialBody? Around { get; }

    public OrbitField(CelestialBody? around, float innerRadius, float outerRadius, int count, float minSize, float maxSize,
        Color color, float tilt, float thickness, int seed)
    {
        Around = around;
        _tilt = tilt;
        _rocks = new Rock[count];

        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            float radius = innerRadius + (outerRadius - innerRadius) * random.NextSingle();
            float shade = 0.75f + random.NextSingle() * 0.4f;

            _rocks[i] = new Rock
            {
                Radius = radius,
                Phase = random.NextSingle() * MathF.Tau,
                // Kepler: the inner edge laps the outer one
                AngularSpeed = 0.05f * MathF.Sqrt(innerRadius / radius),
                Lift = (random.NextSingle() - 0.5f) * thickness,
                Size = minSize + (maxSize - minSize) * random.NextSingle() * random.NextSingle(),
                Color = new Color(
                    (byte)Math.Clamp(color.R * shade, 0f, 255f),
                    (byte)Math.Clamp(color.G * shade, 0f, 255f),
                    (byte)Math.Clamp(color.B * shade, 0f, 255f), (byte)255),
                SpinAxis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) + new Vector3(0.01f)),
                SpinSpeed = 5f + random.NextSingle() * 40f,
            };
        }
    }

    public void Draw(float time)
    {
        Vector3 centre = Around?.Body.Position ?? Vector3.Zero;
        float sinTilt = MathF.Sin(_tilt);
        float cosTilt = MathF.Cos(_tilt);

        foreach (Rock rock in _rocks)
        {
            float angle = rock.Phase + rock.AngularSpeed * time;
            float x = MathF.Cos(angle) * rock.Radius;
            float z = MathF.Sin(angle) * rock.Radius;

            var position = centre + new Vector3(x, z * sinTilt + rock.Lift, z * cosTilt);

            Rlgl.PushMatrix();
            Rlgl.Translatef(position.X, position.Y, position.Z);
            Rlgl.Rotatef(rock.SpinSpeed * time, rock.SpinAxis.X, rock.SpinAxis.Y, rock.SpinAxis.Z);
            Raylib.DrawCubeV(Vector3.Zero, new Vector3(rock.Size, rock.Size * 0.7f, rock.Size * 0.85f), rock.Color);
            Rlgl.PopMatrix();
        }
    }
}
