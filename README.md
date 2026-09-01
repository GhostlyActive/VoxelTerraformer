# VoxelTerraformer

An experimental voxel engine written in C# with raylib. Build, dig and reshape an
endless world — and switch how that world looks and feels while you play.

![Rocket launch, impact crater, and the same crater in Blocks and Smooth mode](Screenshots/demo.gif)

---

## Three voxel modes, one world

Press **V** to cycle through them. The world data never changes, so switching is
free and you can go back and forth at any time.

| Mode | Tool | Look |
| --- | --- | --- |
| **Blocks** | place and remove whole blocks | classic voxel cubes |
| **Sculpt** | sphere brush, 8× finer than a block | carve holes and tunnels |
| **Smooth** | same sphere brush | rounded terrain via marching cubes |

Build a house as blocks, switch to Smooth, and it looks like it was shaped out of
clay. Switch back and every block is exactly where you left it.

---

## Features

- Endless world that streams in around you, with manual save and load
- Terrain meshed in the background — building and digging never stalls the frame
- Ambient occlusion, day/night cycle, distance fog, stars and drifting clouds
- Sub-voxel collision, so you can walk into the holes you drilled
- Live tuning menu for movement, gravity and brush settings

---

## Controls

| Key | Action |
| --- | --- |
| **W / A / S / D**, Mouse | Move and look |
| **Shift** | Sprint |
| **Space** | Jump |
| **Left / Right Mouse** | Remove / place (hold in Sculpt and Smooth mode) |
| **Mouse wheel** | Build distance |
| **Ctrl + Mouse wheel** | Brush size |
| **V** | Switch voxel mode |
| **Z / U** | Slower / faster day |
| **M** | Tuning menu |
| **F3** | Debug overlay |
| **ESC** | Pause menu — save, load, quit |

---

## Running it

```
dotnet run -c Release --project Terraformer.csproj
```

Release matters: the smooth mode does a lot of number crunching and is several
times slower in a debug build. Runs on Windows, Linux and macOS.

---

## Screenshots

The same spot, the same two carved spheres — once in Blocks mode, once in Smooth mode.

![Blocks mode](Screenshots/Image1.png)
![Smooth mode](Screenshots/Image2.png)
