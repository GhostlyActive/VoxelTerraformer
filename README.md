# VoxelTerraformer

A voxel engine in C# on raylib, and four games built on it. The engine streams the world,
meshes it in the background, draws it and runs the menus; a game is a project under `Games/`
that references the engine and nothing else.

![Rocket launch, impact crater, and the same crater in Blocks and Smooth mode](Screenshots/demo.gif)

## Games

Switch at any time with **ESC → Games**.

| Game | What you do |
| --- | --- |
| **Free Walk** | The sandbox: build, dig, cycle the three voxel modes, turn the dials. Mountains and caves 256 blocks tall, and a jetpack to get up them. |
| **Rocket Storm** | Survive rocket waves in Smooth mode: standard, cluster, buster and seeker rockets. Dig in, shoot them down with the flak, climb the local leaderboard. |
| **Solar System** | Planets two to four kilometres across with atmospheres, rings, an asteroid belt and real gravity. Fly with all six degrees of freedom, drop into an atmosphere and watch the sky close over you, park on the ground, blast craters that stay; two cores are molten. |
| **Cave Dive** | Underground in the dark with a lantern, Smooth mode only. Dig through, leave lamps to light the way back, dig crystals out of the walls. |

## Three voxel modes, one world

**V** cycles Blocks (place and remove whole blocks), Sculpt (sphere brush, 8× finer than a
block) and Smooth (same brush, rounded terrain via marching cubes). The world data never
changes, so switching is lossless. A switch spreads over the ground as a ring in the colour of
the new mode, nearest terrain first, and the ring never runs ahead of what has actually been
rebuilt.

The brush is always visible where the next stroke bites. A stroke sticks to the surface it
started on, so a sweep lays down a continuous tube instead of chasing the material it just
built; carving follows the surface into the ground.

## How it scales

Chunks are columns 32 blocks across and up to 256 blocks tall, cut into slabs that cost nothing
while they hold only sky or solid rock. They stream in a kilometre-wide circle, generated on
worker threads and meshed once all their neighbours are present. Near the player a chunk is four sections of
full-detail mesh and a stroke rebuilds only the sections it touched, ahead of everything else
in the queue; further out a column is one mesh from a downsampled grid (2 m, then 4 m blocks).
Meshes are indexed, 20 bytes per vertex, and drawn straight through rlgl.

## Layout

```
Engine/        VoxelEngine.dll: Core, World, Rendering, Scenes, Input, UI, Audio, Effects, MathTools, Config
Games/<Id>/    one project per game: <Id>.csproj, Scripts/, Assets/
Program.cs     the launcher; finds the games by attribute
Tests/         xunit tests over the engine's pure logic
```

Games reference only the engine and cannot see each other; the launcher holds no game code.
To add one, create `Games/MyGame/MyGame.csproj` (`<Project Sdk="Microsoft.NET.Sdk"></Project>`),
derive a class from `VoxelEngine.Core.Game` and tag it:

```csharp
[GameDefinition("MyGame", "My Game", "One-line description")]
public sealed class MyGame : Game { ... }
```

See [Games/README.md](Games/README.md) for what a game gets from the engine.

## Controls

| Key | Action |
| --- | --- |
| **W A S D**, mouse | Move and look (fly in Solar System, where there is no up: pull round and you loop) |
| **Shift** / **Space** | Sprint / jump, hold Space in the air for the jetpack (boost / up in Solar System) |
| **Q** / **E** | Roll the ship in Solar System |
| **LMB** / **RMB** | Remove / place, hold in Sculpt and Smooth; charge and fire in Solar System |
| **Wheel**, **Ctrl + Wheel** | Build distance, brush size |
| **V** | Switch voxel mode |
| **F** | Flak in Rocket Storm, full stop in Solar System |
| **Z** / **U** | Move the sun or change the clock speed |
| **M**, **F3**, **ESC** | Tuning menu (scene and game dials), debug overlay, pause menu with display and control settings |

## Running it

Builds from [Releases](../../releases) are self-contained. From source:

```
dotnet run -c Release --project Terraformer.csproj
```

![Blocks mode](Screenshots/Image1.png)
![Smooth mode](Screenshots/Image2.png)
