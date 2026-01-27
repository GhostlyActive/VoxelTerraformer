using Raylib_cs;
using System.Numerics;
using System.Collections.Generic;
using Terraformer.MathTools;

namespace Terraformer.World;

public class VoxelWorld
{
    public const int WorldHeight = 64;

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();

    // Optional debug info (for highlighting)
    private bool _hasHover;
    private Vector3 _hoverCenter;

    public VoxelWorld()
    {
        // Create a small start area (2x2 chunks).
        for (int cz = 0; cz < 2; cz++)
        for (int cx = 0; cx < 2; cx++)
            GetOrCreateChunk(new ChunkCoord(cx, cz));
    }

    public void Update(Camera3D camera)
    {
        // Inputs: Mouse + keyboard fallback
        bool remove = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.O);
        bool place  = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.P);

        // Always refresh hover (so you can see what you're aiming at)
        UpdateHover(camera);

        if (remove) TryRemove(camera);
        if (place)  TryPlace(camera);
    }

    public void Draw(Vector3 sunPos)
    {
        foreach (var chunk in _chunks.Values)
            chunk.Draw(sunPos, WorldHeight, GetBlock);

        // Optional: draw block highlight at the aimed block
        if (_hasHover)
        {
            Raylib.DrawCubeWires(_hoverCenter, 1.02f, 1.02f, 1.02f, Color.Yellow);
        }
    }

    // --- Interaction ---

    private void UpdateHover(Camera3D camera)
    {
        _hasHover = false;


        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );
        

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        _hoverCenter = new Vector3(hit.Block.X + 0.5f, hit.Block.Y + 0.5f, hit.Block.Z + 0.5f);
        _hasHover = true;
    }

    private void TryRemove(Camera3D camera)
    {
        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        SetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z, 0);
    }

    private void TryPlace(Camera3D camera)
    {
        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        // Place into neighbor cell (the face you are pointing at)
        int px = hit.PlaceBlock.X;
        int py = hit.PlaceBlock.Y;
        int pz = hit.PlaceBlock.Z;

        if (GetBlock(px, py, pz) != 0) return; // must be air
        SetBlock(px, py, pz, 1);
    }

    // --- World-level block access ---

    public int GetBlock(int wx, int wy, int wz)
    {
        if (wy < 0 || wy >= WorldHeight) return 0;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return 0;

        return chunk.GetLocal(lx, wy, lz, WorldHeight);
    }

    public void SetBlock(int wx, int wy, int wz, int id)
    {
        if (wy < 0 || wy >= WorldHeight) return;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        var chunk = GetOrCreateChunk(cc);

        chunk.SetLocal(lx, wy, lz, id, WorldHeight);

        // Neighbor chunks may need rebuild when editing on borders.
        if (lx == 0) MarkDirty(new ChunkCoord(cc.X - 1, cc.Z));
        if (lx == Chunk.Size - 1) MarkDirty(new ChunkCoord(cc.X + 1, cc.Z));
        if (lz == 0) MarkDirty(new ChunkCoord(cc.X, cc.Z - 1));
        if (lz == Chunk.Size - 1) MarkDirty(new ChunkCoord(cc.X, cc.Z + 1));
    }

    private void MarkDirty(ChunkCoord cc)
    {
        if (_chunks.TryGetValue(cc, out var chunk))
            chunk.MarkDirty();
    }

    private Chunk GetOrCreateChunk(ChunkCoord cc)
    {
        if (_chunks.TryGetValue(cc, out var existing))
            return existing;

        var created = new Chunk(cc, WorldHeight);
        _chunks.Add(cc, created);
        return created;
    }

    // --- Coordinate helpers ---

    private static (ChunkCoord cc, int lx, int lz) WorldToChunk(int wx, int wz)
    {
        int cx = FloorDiv(wx, Chunk.Size);
        int cz = FloorDiv(wz, Chunk.Size);

        int lx = wx - cx * Chunk.Size;
        int lz = wz - cz * Chunk.Size;

        return (new ChunkCoord(cx, cz), lx, lz);
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((r > 0) != (b > 0))) q--;
        return q;
    }
}
