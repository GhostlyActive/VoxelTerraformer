using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>Why a chunk needs a new mesh; lower values go first</summary>
public enum MeshReason : byte
{
    /// <summary>The player changed something: nothing may queue ahead of it</summary>
    Edit = 0,

    /// <summary>A chunk column got all its neighbours and can be meshed for the first time</summary>
    Stream = 1,

    /// <summary>The column crossed a level-of-detail border</summary>
    Lod = 2,

    /// <summary>The presentation switched between cubes and smooth</summary>
    Mode = 3,
}

/// <summary>
/// The bookkeeping of what needs meshing, kept apart from the GPU work so it can be tested without
/// a window. Every dirty chunk carries the sections it needs and the most urgent reason it was
/// marked for; each frame the caller takes the best few by (reason, distance from the camera).
///
/// A chunk in flight can be taken again for an edit, so a running stroke never waits for the
/// previous mesh to land; anything else waits until its job returns.
/// </summary>
public sealed class MeshScheduler
{
    private struct Dirty
    {
        public int Sections;
        public MeshReason Reason;
    }

    private readonly Dictionary<ChunkCoord, Dirty> _dirty = new();
    private readonly Dictionary<ChunkCoord, int> _inFlight = new();

    private readonly List<(float Key, ChunkCoord Coord)> _candidates = new();

    public int DirtyCount => _dirty.Count;

    public int InFlightCount { get; private set; }

    public void Mark(ChunkCoord coord, int sections, MeshReason reason)
    {
        if (sections == 0) return;

        if (_dirty.TryGetValue(coord, out Dirty dirty))
        {
            dirty.Sections |= sections;
            if (reason < dirty.Reason) dirty.Reason = reason;
        }
        else
        {
            dirty = new Dirty { Sections = sections, Reason = reason };
        }

        _dirty[coord] = dirty;
    }

    public bool IsDirty(ChunkCoord coord) => _dirty.ContainsKey(coord);

    /// <summary>Dirty chunks that can actually be meshed now; the rest wait for a neighbour at the far ring</summary>
    public int CountReady(Func<ChunkCoord, bool> ready)
    {
        int count = 0;
        foreach (ChunkCoord coord in _dirty.Keys)
            if (ready(coord)) count++;
        return count;
    }

    public void Forget(ChunkCoord coord) => _dirty.Remove(coord);

    public void ForgetAll() => _dirty.Clear();

    public int InFlight(ChunkCoord coord) => _inFlight.TryGetValue(coord, out int count) ? count : 0;

    /// <summary>
    /// Picks up to <paramref name="maxCount"/> chunks to mesh now, most urgent and nearest first,
    /// and removes them from the dirty set. <paramref name="ready"/> says whether a chunk can be
    /// meshed at all (loaded, neighbours present); <paramref name="distance"/> is its distance from
    /// the camera in chunks.
    /// </summary>
    public int Take(int maxCount, Func<ChunkCoord, bool> ready, Func<ChunkCoord, float> distance,
        Span<(ChunkCoord Coord, int Sections, MeshReason Reason)> output)
    {
        if (maxCount <= 0 || _dirty.Count == 0) return 0;

        _candidates.Clear();

        foreach ((ChunkCoord coord, Dirty dirty) in _dirty)
        {
            if (InFlight(coord) >= InFlightLimit(dirty.Reason)) continue;
            if (!ready(coord)) continue;

            // Reason dominates; within a reason the nearest chunk goes first. The world is well
            // under 100 chunks across, so a reason band of 1000 keeps the bands apart.
            _candidates.Add(((int)dirty.Reason * 1000f + distance(coord), coord));
        }

        int taken = 0;
        while (taken < maxCount && taken < output.Length && _candidates.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < _candidates.Count; i++)
                if (_candidates[i].Key < _candidates[best].Key) best = i;

            ChunkCoord coord = _candidates[best].Coord;
            _candidates[best] = _candidates[^1];
            _candidates.RemoveAt(_candidates.Count - 1);

            Dirty dirty = _dirty[coord];
            _dirty.Remove(coord);

            output[taken++] = (coord, dirty.Sections, dirty.Reason);
        }

        return taken;
    }

    /// <summary>An edit may overlap the job already running for its chunk; everything else waits</summary>
    private static int InFlightLimit(MeshReason reason) => reason == MeshReason.Edit ? 2 : 1;

    public void BeginJob(ChunkCoord coord)
    {
        _inFlight[coord] = InFlight(coord) + 1;
        InFlightCount++;
    }

    public void EndJob(ChunkCoord coord)
    {
        int count = InFlight(coord) - 1;
        if (count <= 0) _inFlight.Remove(coord);
        else _inFlight[coord] = count;

        InFlightCount = Math.Max(0, InFlightCount - 1);
    }
}
