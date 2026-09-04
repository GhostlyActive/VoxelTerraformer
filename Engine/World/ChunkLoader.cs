using System.Collections.Concurrent;

namespace VoxelEngine.World;

/// <summary>
/// Builds chunks on worker threads: terrain generation, or the save file when the world was
/// loaded from disk. The main thread asks for coordinates and adopts the finished chunks a few
/// per frame, so a heavy generator costs streaming throughput rather than frame time.
///
/// Generators run concurrently and therefore have to be pure per call; the default generator is.
/// A world generation number rides along so a result from before "Load world" can be told apart
/// and dropped.
/// </summary>
internal sealed class ChunkLoader : IDisposable
{
    private readonly record struct LoadRequest(ChunkCoord Coord, bool FromDisk, int WorldGeneration);

    public readonly record struct Result(ChunkCoord Coord, Chunk Chunk, int WorldGeneration);

    private readonly WorldStorage? _storage;
    private readonly ITerrainGenerator _generator;
    private readonly int _worldHeight;

    private readonly BlockingCollection<LoadRequest> _requests = new();
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly Thread[] _workers;

    private volatile bool _disposing;
    private int _pending;

    /// <summary>Requested and not yet handed back</summary>
    public int Pending => _pending;

    public ChunkLoader(WorldStorage? storage, ITerrainGenerator generator, int worldHeight, int workerCount)
    {
        _storage = storage;
        _generator = generator;
        _worldHeight = worldHeight;

        _workers = new Thread[Math.Max(1, workerCount)];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ChunkLoader{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            _workers[i].Start();
        }
    }

    public void Request(ChunkCoord coord, bool fromDisk, int worldGeneration)
    {
        Interlocked.Increment(ref _pending);
        _requests.Add(new LoadRequest(coord, fromDisk, worldGeneration));
    }

    public bool TryTake(out Result result)
    {
        if (!_results.TryDequeue(out result)) return false;

        Interlocked.Decrement(ref _pending);
        return true;
    }

    private void WorkerLoop()
    {
        foreach (LoadRequest request in _requests.GetConsumingEnumerable())
        {
            if (_disposing) continue;

            Chunk chunk = Build(request);
            _results.Enqueue(new Result(request.Coord, chunk, request.WorldGeneration));
        }
    }

    private Chunk Build(LoadRequest request)
    {
        if (request.FromDisk && _storage != null &&
            _storage.TryLoad(request.Coord, Chunk.Size * _worldHeight * Chunk.Size, out byte[]? blocks, out Dictionary<int, byte[]>? refinements))
        {
            return new Chunk(request.Coord, blocks!, refinements!);
        }

        return new Chunk(request.Coord, _worldHeight, _generator);
    }

    public void Dispose()
    {
        _disposing = true;
        _requests.CompleteAdding();

        foreach (Thread worker in _workers)
            worker.Join();
    }
}
