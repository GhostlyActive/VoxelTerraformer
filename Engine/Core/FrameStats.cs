namespace VoxelEngine.Core;

/// <summary>
/// Rolling frame timing for the debug overlay. Frames per second is nonlinear and flattens out
/// near the frame cap, where it hides what a change actually cost, so the overlay leads with
/// milliseconds. The worst frame of the last second sits next to the average because a single
/// stall is what the player feels; the average never shows it.
/// </summary>
public sealed class FrameStats
{
    private const int Window = 60;

    private readonly float[] _milliseconds = new float[Window];
    private int _next;
    private int _count;

    private readonly List<float> _lifetime = new();

    public void Add(float deltaSeconds)
    {
        _milliseconds[_next] = deltaSeconds * 1000f;
        _next = (_next + 1) % Window;
        _count = Math.Min(Window, _count + 1);
        _lifetime.Add(deltaSeconds * 1000f);
    }

    /// <summary>Every frame since the start, for a report that does not stop at the last second</summary>
    public string LifetimeReport(float targetMs = 16.7f)
    {
        if (_lifetime.Count == 0) return "no frames";

        var sorted = _lifetime.ToArray();
        Array.Sort(sorted);

        float average = 0f;
        int over = 0;
        foreach (float ms in sorted)
        {
            average += ms;
            if (ms > targetMs) over++;
        }
        average /= sorted.Length;

        float p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99f))];
        return $"frames={sorted.Length} avg={average:F2}ms p99={p99:F2}ms max={sorted[^1]:F2}ms over{targetMs:0}ms={over}";
    }

    public float AverageMs
    {
        get
        {
            if (_count == 0) return 0f;

            float total = 0f;
            for (int i = 0; i < _count; i++) total += _milliseconds[i];

            return total / _count;
        }
    }

    public float PeakMs
    {
        get
        {
            float peak = 0f;
            for (int i = 0; i < _count; i++) peak = MathF.Max(peak, _milliseconds[i]);

            return peak;
        }
    }

    public int Fps
    {
        get
        {
            float average = AverageMs;
            return average > 0.001f ? (int)MathF.Round(1000f / average) : 0;
        }
    }
}
