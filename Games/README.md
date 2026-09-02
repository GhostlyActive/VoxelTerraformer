# Games

A game is a folder. The engine (`Engine/`, assembly `VoxelEngine`) knows about none of them — it
provides the world, rendering, controls, menus and audio, and the games build on top.

```
Games/<Id>/
  Scripts/          the game's C# files
  Assets/
    Sounds/         .wav, .ogg or .mp3, named after the sound name used in code
```

`<Id>` is also the folder name in the output directory: `Context.AssetPath("Sounds/boom.wav")`
points at `Games/<Id>/Assets/Sounds/boom.wav`.

## Adding a game

1. Create `Games/MyGame/Scripts/`.
2. Derive a class from `VoxelEngine.Core.Game` (`Load`, `Update`, `DrawWorld`, `Camera`).
3. Add one line to `Program.cs`:

```csharp
registry.Add("MyGame", "My Game", "Short description", () => new MyGame());
```

It then shows up in the ESC menu under **Games**.

## Sounds

Every game registers its sounds in `Load`:

```csharp
Context.Audio.Define("explosion", SfxShape.Explosion);
```

If `Assets/Sounds/explosion.wav` exists, the file is used. If it does not, the engine synthesizes a
stand-in from the `SfxShape` you passed — so a game has sound without shipping any audio files, and
dropping a file in later replaces the stand-in without a code change.

| Game | expected files |
| --- | --- |
| `RocketStorm` | `launch`, `explosion`, `hit` |
| `SolarSystem` | `shot`, `impact`, `bump` |
| `FreeWalk` | — |
