# VoxelTerraformer

A small voxel engine written in C# with raylib — and three games built on top of it. The engine
owns the world, the rendering, the controls and the menus; a game is just a folder that plugs
into it. Switch between games at any time from the pause menu.

![Rocket launch, impact crater, and the same crater in Blocks and Smooth mode](Screenshots/demo.gif)

---

## The games

Press **ESC → Games** to switch. Every game runs in the same window and shares the tuning menu.

| Game | What you do |
| --- | --- |
| **Free Walk** | The sandbox. Endless world, no goal: build, dig, cycle the three voxel modes, turn the dials. This is where the project starts. |
| **Rocket Storm** | Survive waves of incoming rockets in Smooth mode. Every impact tears a round crater out of the ground — and since the sphere brush stays live, you can dig yourself a hole and ride it out. Fixed sun, no night. |
| **Solar System** | Six voxel planets over a kilometre across, ten moons, real gravity. Fly between them, hold the trigger to pack a bigger round, and blast craters that stay: rubble keeps flying and falls back down. Moons throw shadows across their planets, and two of the planets have a molten core that ends them when you dig deep enough. |

---

## Three voxel modes, one world

Press **V** in Free Walk to cycle through them. The world data never changes, so switching is
free and you can go back and forth at any time.

| Mode | Tool | Look |
| --- | --- | --- |
| **Blocks** | place and remove whole blocks | classic voxel cubes |
| **Sculpt** | sphere brush, 8× finer than a block | carve holes and tunnels |
| **Smooth** | same sphere brush | rounded terrain via marching cubes |

Build a house as blocks, switch to Smooth, and it looks like it was shaped out of clay. Switch
back and every block is exactly where you left it.

---

## Layout

```
Engine/            VoxelEngine.dll — knows nothing about any game
  Core/            Game base class, host loop, registry, per-game context
  World/           streamed VoxelWorld, chunks, sub-voxels, VoxelBody, terrain generators
  Rendering/       block and marching-cubes meshers, terrain shader, sky, stars, clouds
  Scenes/          VoxelTerrainScene: a ready-wired world with player, light and particles
  Input/ UI/ Audio/ Effects/ MathTools/ Config/

Games/             one folder per game
  FreeWalk/        Scripts/ + Assets/
  RocketStorm/
  SolarSystem/

Program.cs         registers the games and starts the host
```

The direction is enforced by the compiler: games reference the engine, never the other way
round, and no game can reach into another.

## What a game gets from the engine

- **`VoxelTerrainScene`** — an endless streamed world with background meshing, day/night cycle,
  sky, clouds, particles and a player with sub-voxel collision, in one object. Set spawn, terrain
  and voxel mode; call `Update` and `Draw`.
- **World building** — plug in an `ITerrainGenerator` (or tune `DefaultTerrainGenerator`, which
  warps its sample positions and switches between plains, mountains, stepped mesas and canyons as
  you travel), register your own block materials, cut spheres out of the terrain with `Explode`.
- **Light** — run the day/night cycle or pin the sun, and set its angle in degrees: 0 is sunrise,
  90 the highest point, 180 sunset. Free Walk and Rocket Storm both use a fixed sun.
- **`VoxelBody`** — a free-standing voxel object with its own position, scale and spin, for
  planets and asteroids. The grid is split into sub-chunks, so carving a sphere out of a body a
  hundred voxels across only remeshes what actually changed.
- **Shadows** — the terrain shader takes up to eight occluder spheres and darkens whatever they
  hide from the sun. That is what puts a moon's shadow on its planet, per fragment and soft-edged.
- **Controls** — `PlayerController` (walk, jump, sub-voxel collision) and `FreeFlyController`
  (6-DOF flight with momentum and an external acceleration input, which is how orbits work).
- **Menus and HUD** — pause menu with the game list, the shared tuning menu on **M**, and small
  HUD helpers for text, bars and crosshairs.
- **Audio** — name a sound and it plays a file from `Assets/Sounds/` if you shipped one, or a
  synthesized stand-in if you didn't.

## Adding a game

Create `Games/MyGame/Scripts/`, derive from `VoxelEngine.Core.Game`, and add one line to
`Program.cs`:

```csharp
registry.Add("MyGame", "My Game", "One-line description", () => new MyGame());
```

It shows up under **ESC → Games**. See [Games/README.md](Games/README.md) for the details.

---

## Controls

| Key | Action |
| --- | --- |
| **W / A / S / D**, Mouse | Move and look (fly, in Solar System) |
| **Shift** | Sprint / afterburner |
| **Space** | Jump — climb, in Solar System |
| **Left / Right Mouse** | Remove / place (hold in Sculpt and Smooth); in Solar System, hold to charge a round and release to fire |
| **F** | Full stop (Solar System) |
| **Mouse wheel** | Build distance |
| **Ctrl + Mouse wheel** | Brush size |
| **V** | Switch voxel mode |
| **Z / U** | Move the sun (or change the clock speed in a game that runs one) |
| **M** | Tuning menu |
| **F3** | Debug overlay |
| **ESC** | Pause menu — games, save, load, quit |

The pause menu lists the controls of whichever game is running.

---

## Running it

```
dotnet run -c Release --project Terraformer.csproj
```

Release matters: the smooth mode does a lot of number crunching and is several times slower in a
debug build. Runs on Windows, Linux and macOS.

Start straight into a game with `--game RocketStorm` (or `FreeWalk`, `SolarSystem`).
`--smoke` renders a few seconds, writes `smoke.png` and exits — handy for checking a build.

---

## Screenshots

The same spot, the same two carved spheres — once in Blocks mode, once in Smooth mode.

![Blocks mode](Screenshots/Image1.png)
![Smooth mode](Screenshots/Image2.png)
