# Games

A game is a folder with a project of its own. The engine (`Engine/`, assembly `VoxelEngine`)
knows about none of them — it provides the world, rendering, controls, menus and audio, and the
games build on top. The launcher (`Terraformer.csproj`) references every project under `Games/`
and finds the games inside them by attribute, so nothing has to be registered by hand.

```
Games/<Id>/
  <Id>.csproj       empty: everything comes from Games/Directory.Build.props
  Scripts/          the game's C# files, namespace Games.<Id>
  Assets/
    Sounds/         .wav, .ogg or .mp3, named after the sound name used in code
```

`<Id>` is also the folder name in the output directory: `Context.AssetPath("Sounds/boom.wav")`
points at `Games/<Id>/Assets/Sounds/boom.wav`. The direction is enforced by the compiler: a game
references only the engine, never the launcher or another game.

## Adding a game

1. Create `Games/MyGame/MyGame.csproj` containing just `<Project Sdk="Microsoft.NET.Sdk"></Project>`.
2. Add `Games/MyGame/Scripts/MyGame.cs`:

```csharp
using VoxelEngine.Core;

namespace Games.MyGame;

[GameDefinition("MyGame", "My Game", "One-line description for the menu")]
public sealed class MyGame : Game
{
    public override Camera3D Camera => ...;
    public override void Load() { ... }
    public override void Update(float dt) { ... }
    public override void DrawWorld() { ... }
}
```

3. `dotnet sln Terraformer.sln add Games/MyGame/MyGame.csproj` (the launcher picks the project
   up by its folder either way; the solution entry is for IDEs and CI).

It then shows up in the ESC menu under **Games**. Use raylib for drawing and input, the way a
Unity game uses `UnityEngine`: `Camera3D`, `Color`, `Raylib.Draw*`. What the engine wraps is the
world (`VoxelTerrainScene`, `VoxelBody`), the paths (`Context.Paths`, `Context.OpenStorage`), the
tuning values (`Context.Settings`), sounds (`Context.Audio`) and the host (`Context.RequestGame`,
`Context.RequestQuit`). A game that spans more than a couple of kilometres overrides
`Game.ClipPlanes`.

## What a terrain game gets

`new VoxelTerrainScene(Context, new VoxelTerrainOptions { ... })` is a complete world: streamed
chunks built on worker threads, three levels of detail, the three voxel modes with the switch
wave, a player with sub-voxel collision and an optional jetpack, day cycle, sky, clouds and
particles. Set the spawn, the generator, the mode, the save slot and the material the player
builds with, then call `Update`, `PumpMeshUploads`, `DrawBackground` and `Draw`.

Materials: `BlockRegistry.Register(name, colour, ...)` in `Load`. Registering the same name again
returns the same id, so a game can register every time it starts.

Dials: `Context.Tuning.AddSection("MY GAME", save)` puts the game's own values into the menu on
**M**, next to the scene's; `Context.Store.Load<T>(key)` / `Save` keep them (or anything else,
such as a leaderboard) in the product's settings file. Remove the section in `Unload`. Cave Dive,
Rocket Storm and Solar System show the pattern. Display, mouse and view distance apply to every
game and live in the pause menu under **Settings**, owned by the host.

## Sounds

Every game registers its sounds in `Load`:

```csharp
Context.Audio.Define("explosion", SfxShape.Explosion);
```

If `Assets/Sounds/explosion.wav` exists, the file is used. If it does not, the engine synthesizes a
stand-in from the `SfxShape` you passed — so a game has sound without shipping any audio files, and
dropping a file in later replaces the stand-in without a code change.

The terrain scene defines `engine.dig`, `engine.place`, `engine.mode` and `engine.jet` the same
way; a game can ship files under those names too.

| Game | expected files |
| --- | --- |
| `RocketStorm` | `launch`, `explosion`, `hit`, `flak`, `shotdown` |
| `SolarSystem` | `shot`, `impact`, `bump`, `detonate`, `entry` |
| `CaveDive` | `crystal` |
| `FreeWalk` | — |
