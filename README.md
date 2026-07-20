# VoxSlop

![VoxSlop screenshot](screen.png)

A voxel-based 3D FPS tech demo. Unlike Minecraft-style games where voxels are
metre-scale cubes, VoxSlop works at centimetre scale — currently 5 cm voxels —
so a modest landscape is made of *billions* of voxels. The whole world is drawn
by GPU raymarching, with no meshes involved.

## Characteristics

- **Tiny voxels** — 5 cm, giving a ~410 × 410 m landscape of ~21 billion voxel slots.
- **Meshless rendering** — one fullscreen fragment shader raymarches the world;
  render cost scales with screen resolution, not voxel count.
- **Sparse "brickmap" storage** — empty and uniform regions cost no memory, so the
  world fits in tens of MB rather than gigabytes.
- **Voxel-quantised shadows** — one shadow value per voxel face, cached on the GPU.
- **Animated day/night sun**, procedural terrain and grass, and a cached world that
  loads instantly on subsequent runs.
- **FPS controls** — walk with gravity/jump and step-up over terrain, plus a noclip fly mode.

## Requirements

- .NET 10 SDK
- A GPU supporting OpenGL 4.3 (shader storage buffers)

## Build & run

```sh
dotnet run --project VoxSlop.App
```

## Controls

| Key | Action |
| --- | --- |
| Mouse | Look |
| WASD | Move (Shift to sprint) |
| Space / Ctrl | Jump / fly up · fly down |
| F | Toggle walk / noclip |
| L | Toggle sun shadows |
| P | Pause / resume the sun |
| C | Toggle the per-voxel-face shadow cache |
| R | Reload shaders from disk |
| Esc | Release / recapture the cursor |

## Tech

Built on [.NET 10](https://dotnet.microsoft.com/) and
[Silk.NET](https://github.com/dotnet/Silk.NET) (windowing, input, OpenGL).
See [CLAUDE.md](CLAUDE.md) for architecture notes.
