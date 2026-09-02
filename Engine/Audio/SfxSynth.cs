using Raylib_cs;

namespace VoxelEngine.Audio;

/// <summary>
/// Description of a synthesized sound: a sine sweeping from <see cref="StartHz"/> to
/// <see cref="EndHz"/>, mixed with <see cref="Noise"/> of noise and shaped by a decaying envelope.
/// Enough to fake a shot, an impact, a thruster or a menu click without shipping a single audio
/// file.
/// </summary>
public readonly record struct SfxShape(
    float Seconds,
    float StartHz,
    float EndHz,
    float Noise,
    float Decay)
{
    public static SfxShape Shot => new(0.18f, 880f, 180f, 0.25f, 9f);
    public static SfxShape Explosion => new(0.9f, 220f, 40f, 0.9f, 4.5f);
    public static SfxShape Launch => new(0.7f, 120f, 320f, 0.7f, 2.2f);
    public static SfxShape Hit => new(0.25f, 400f, 90f, 0.6f, 8f);
    public static SfxShape Pickup => new(0.22f, 520f, 1180f, 0f, 5f);
}

/// <summary>Turns a <see cref="SfxShape"/> into a playable sound</summary>
public static class SfxSynth
{
    private const int SampleRate = 22050;

    public static Sound Create(SfxShape shape)
    {
        int frames = Math.Max(1, (int)(shape.Seconds * SampleRate));
        var samples = new short[frames];

        var random = new Random(unchecked((int)(shape.StartHz * 31 + shape.EndHz * 7 + shape.Seconds * 1000)));
        float phase = 0f;

        for (int i = 0; i < frames; i++)
        {
            float t = i / (float)frames;

            float frequency = shape.StartHz + (shape.EndHz - shape.StartHz) * t;
            phase += MathF.Tau * frequency / SampleRate;

            float tone = MathF.Sin(phase);
            float noise = random.NextSingle() * 2f - 1f;
            float mixed = tone * (1f - shape.Noise) + noise * shape.Noise;

            // Short attack at the start, so the first sample does not click
            float attack = MathF.Min(1f, i / (SampleRate * 0.004f));
            float envelope = attack * MathF.Exp(-shape.Decay * t);

            samples[i] = (short)Math.Clamp(mixed * envelope * short.MaxValue * 0.6f, short.MinValue, short.MaxValue);
        }

        return FromSamples(samples);
    }

    private static unsafe Sound FromSamples(short[] samples)
    {
        fixed (short* data = samples)
        {
            var wave = new Wave
            {
                SampleCount = (uint)samples.Length,
                SampleRate = SampleRate,
                SampleSize = 16,
                Channels = 1,
                Data = data,
            };

            // LoadSoundFromWave copies the samples into the audio buffer, so the pinned array may
            // move again afterwards. UnloadWave would even be wrong here: foreign allocator.
            return Raylib.LoadSoundFromWave(wave);
        }
    }
}
