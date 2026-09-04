using System.Diagnostics;

namespace VoxelEngine.Core;

/// <summary>Where the main thread spends a frame, by engine subsystem</summary>
public enum FrameSlot
{
    Stream,
    Snapshot,
    Upload,
    Draw,
}

/// <summary>
/// Per-frame timings of the engine's main-thread work, so a stall can be told apart from a slow
/// GPU or a garbage collection. The host resets it each frame; the scene and the mesh manager add
/// to it; the debug overlay and the smoke report read it.
/// </summary>
public sealed class FrameProfiler
{
    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly double[] _milliseconds = new double[Enum.GetValues<FrameSlot>().Length];
    private readonly int[] _counts = new int[Enum.GetValues<FrameSlot>().Length];

    private TimeSpan _gcPauseAtFrameStart;

    /// <summary>Milliseconds spent in a slot this frame</summary>
    public double Milliseconds(FrameSlot slot) => _milliseconds[(int)slot];

    /// <summary>How often a slot was entered this frame (chunks streamed, meshes uploaded, ...)</summary>
    public int Count(FrameSlot slot) => _counts[(int)slot];

    /// <summary>Garbage-collector pause time that landed in this frame</summary>
    public double GcPauseMilliseconds { get; private set; }

    public void BeginFrame()
    {
        Array.Clear(_milliseconds);
        Array.Clear(_counts);

        TimeSpan pause = GC.GetTotalPauseDuration();
        GcPauseMilliseconds = (pause - _gcPauseAtFrameStart).TotalMilliseconds;
        _gcPauseAtFrameStart = pause;
    }

    /// <summary>Books the time since <paramref name="startTimestamp"/> (from <see cref="Stopwatch.GetTimestamp"/>) into a slot</summary>
    public void Add(FrameSlot slot, long startTimestamp, int count = 1)
    {
        _milliseconds[(int)slot] += (Stopwatch.GetTimestamp() - startTimestamp) * TicksToMilliseconds;
        _counts[(int)slot] += count;
    }

    public string Summary()
        => $"stream {Milliseconds(FrameSlot.Stream):0.00} ({Count(FrameSlot.Stream)}) | " +
           $"snapshot {Milliseconds(FrameSlot.Snapshot):0.00} ({Count(FrameSlot.Snapshot)}) | " +
           $"upload {Milliseconds(FrameSlot.Upload):0.00} ({Count(FrameSlot.Upload)}) | " +
           $"draw {Milliseconds(FrameSlot.Draw):0.00} | gc {GcPauseMilliseconds:0.00}";
}
