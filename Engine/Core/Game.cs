using Raylib_cs;

namespace VoxelEngine.Core;

/// <summary>
/// Base class for a game. The <see cref="GameHost"/> owns the window, the loop and the menus, and
/// calls <see cref="Load"/> once, then <see cref="Update"/> and the draw methods every frame, and
/// <see cref="Unload"/> at the end.
///
/// The split between the draw methods follows raylib's modes: <see cref="DrawBackground"/> runs in
/// 2D before the scene, <see cref="DrawWorld"/> inside BeginMode3D, <see cref="DrawHud"/> in 2D on
/// top again. Menus are drawn by the host above all of it.
///
/// Keep the constructor cheap and put everything expensive into <see cref="Load"/>: an instance
/// exists from the moment the host switches to the game, and the world it builds should be
/// released again in <see cref="Unload"/>.
/// </summary>
public abstract class Game
{
    /// <summary>Access to the engine services; available from <see cref="Load"/> on</summary>
    protected GameContext Context { get; private set; } = null!;

    /// <summary>The camera the scene is drawn from</summary>
    public abstract Camera3D Camera { get; }

    /// <summary>Shows "Save world" and "Load world" in the pause menu</summary>
    public virtual bool SupportsSaving => false;

    /// <summary>Controls listed in the pause menu, one line per entry</summary>
    public virtual IReadOnlyList<string> ControlHints => Array.Empty<string>();

    /// <summary>
    /// Near and far clip plane in metres. The engine defaults suit a terrain world; a game that
    /// spans kilometres overrides them and the host applies them while the game runs.
    /// </summary>
    public virtual (double Near, double Far) ClipPlanes => (Rendering.Frustum.NearPlane, Rendering.Frustum.FarPlane);

    internal void Attach(GameContext context) => Context = context;

    /// <summary>Build the world, load assets. Runs once when switching into this game.</summary>
    public abstract void Load();

    /// <summary>One step of the game. Does not run while a menu is open.</summary>
    public abstract void Update(float dt);

    /// <summary>
    /// Runs every frame after <see cref="Update"/>, and also while a menu is open. For work that
    /// must not stall and wants this frame's changes: uploading finished chunk meshes and queueing
    /// the ones that just became dirty.
    /// </summary>
    public virtual void UpdateAlways() { }

    /// <summary>Sky or background, drawn before the scene</summary>
    public virtual void DrawBackground() => Raylib.ClearBackground(Color.Black);

    /// <summary>The 3D scene</summary>
    public abstract void DrawWorld();

    /// <summary>Readouts on top of the scene</summary>
    public virtual void DrawHud() { }

    /// <summary>Write a save; only called when <see cref="SupportsSaving"/> holds</summary>
    public virtual bool SaveGame() => false;

    /// <summary>Load a save; only called when <see cref="SupportsSaving"/> holds</summary>
    public virtual bool LoadGame() => false;

    /// <summary>A few lines about the game's state, printed at the end of a smoke or benchmark run</summary>
    public virtual string DebugReport() => "";

    /// <summary>Release everything <see cref="Load"/> created (meshes, shaders, sounds)</summary>
    public virtual void Unload() { }
}
