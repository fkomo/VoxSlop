# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

VoxSlop is a voxel-based 3D FPS tech demo built on **.NET 10** and **Silk.NET** (windowing + input + OpenGL 4.3). Voxels are small — currently 5 cm — so the world holds billions of voxel *slots*. It is **not** Minecraft-style: there are no meshes and no per-block geometry. The entire world is drawn by one fullscreen fragment shader that raymarches a sparse voxel structure on the GPU.

Single project: `VoxSlop.App` (see `VoxSlop.slnx`). No test project exists.

## Commands

```sh
dotnet build VoxSlop.slnx           # build
dotnet run --project VoxSlop.App    # build + run
```

- **Running the app locks `VoxSlop.App.exe`.** A `dotnet build` while it is running succeeds at compiling the DLL but fails to copy the exe (MSB3026). Close the app before rebuilding, or ignore the copy warning if you only need the DLL/shaders refreshed.
- There is **no test framework** and no lint step configured.

## Rendering architecture (the core of the project)

Understanding these three files together is essential: `Voxels/VoxelWorld.cs`, `Render/RaymarchRenderer.cs`, `Render/Shaders/raymarch.frag`.

**Brickmap, not meshes.** The world is a sparse voxel store in "brickmap" form (`VoxelWorld`):
- `Index[]` — one `uint` per brick, dense over the whole 3D volume. A brick is 8×8×8 voxels.
- `Pool[]` — packed voxel payloads (`UintsPerBrick` = 128 uints, i.e. 4 voxels/uint) for *partial* bricks only.
- Index entry encoding: `0` = empty (`EntryEmpty`), high bit set (`UniformFlag`) = whole brick is the material in the low byte, otherwise the value is `poolSlot + 1`. Empty and uniform bricks cost **no** pool storage — that is what keeps a billion-voxel world in tens of MB.

**The single most important invariant:** `Index[]` and `Pool[]` are uploaded verbatim as SSBOs and traversed by `raymarch.frag`. **The C# memory layout and the GLSL decoding must stay in lockstep** — the same brick edge (8), the same `128u` stride, the same entry encoding, the same index formula `x + dimX*(y + dimY*z)`. Change one side and you must change the other.

**How a frame is drawn** (`RaymarchRenderer.Render`): no vertex buffers, no view/projection matrices. The vertex shader synthesises a fullscreen triangle from `gl_VertexID`; the fragment shader casts one primary ray per pixel and does a **two-level DDA** — march bricks, and descend into an 8³ voxel DDA only inside partial bricks. Cost scales with **screen resolution, not voxel count**, which is why the voxels can be this small.

**Units convention (easy to get wrong):** gameplay code (`Camera`, `PlayerController`, collision) works in **metres**. The shader and DDA work in **voxel units**. The renderer bridges them by dividing the camera position by `VoxelWorld.VoxelSize` before uploading. `VoxelSize` is the master scale constant — changing it rescales the whole physical world.

## Shaders

- Shaders live in `Render/Shaders/` and are **copied to the output dir** (not embedded), so they can be **hot-reloaded with `R`** at runtime. `R`-reload reads from `bin/.../Render/Shaders/`, so to iterate without a full rebuild either edit the copy in `bin` or rebuild (which re-copies) then press `R`.
- **Startup shader compile failure is fatal** — it throws from the `RaymarchRenderer` constructor and is not caught. Only `TryReloadShaders` (the `R` path) catches errors and keeps the previous working shader.
- **No `glslangValidator`/`glslc` is installed**, so GLSL is only validated by the driver at runtime. `dotnet build` will not catch shader errors. Two recurring footguns: **non-ASCII bytes** (e.g. an em-dash in a comment) crash some drivers even inside comments — keep shader files ASCII-only; and GLSL **reserved words** (`patch` is reserved for tessellation in GLSL 4.x) produce confusing syntax errors.

## Shadows and the voxel-face shadow cache

Shadows are quantised to **one value per voxel face**, not per pixel: `voxelFaceLight` in `raymarch.frag` samples an N×N grid across the face (derived from `hit.voxel` + `hit.normal`, never the per-pixel hit point) so every pixel on a face gets the identical coverage ratio.

To avoid recomputing that per pixel, `faceShadow` uses a **lock-free GPU shadow cache** (SSBO binding 2, `ShadowCache`): a face hashes to a slot holding `(faceKey, epoch, lightValue)`. Correctness does not depend on synchronisation — a shadow is a deterministic function of `(face, sun)`, so racing writers produce identical values, and any miss/stale-epoch/hash-collision simply recomputes. A memory barrier after each draw makes writes visible to the next frame. The **epoch** (`RaymarchRenderer.ShadowEpoch`) is bumped by `Game.UpdateSun` only after the sun moves `ShadowEpochStep`, so a paused/slow sun reuses cached shadows across frames.

SSBO bindings are fixed: `0` = brick index, `1` = voxel pool, `2` = shadow cache.

## World generation and caching

`WorldGen.Generate` is a **multi-pass** builder — classify every brick, prefix-sum the ones needing storage, fill only those, then a compaction pass reclaims bricks that turned out uniform. It never samples all ~billions of voxel slots directly (that would take minutes); it reasons at brick granularity from the heightfield and only samples partial bricks. Parallelised with `Parallel.For`.

`WorldStore` persists the generated world to `world.voxcache` next to the exe and loads it on startup. It is a **cache, not a save format**: the header records every geometry input (grid dims, seed, `VoxelSize`, brick edge, layout version) and **any mismatch triggers regeneration**. This means changing a generation constant in code is safe — the stale cache is rejected automatically. Bump `LayoutVersion` if the byte meaning changes in a way the header fields don't capture. The cache lives in `bin/`, so a clean rebuild discards it and the next run regenerates once.

## Gameplay layout

- `Game` owns the window and the Load/Update/Render loop, input, the animated sun, and the runtime toggles (see the controls printed to the console on start).
- `PlayerController` — metric AABB collision against the voxel volume with axis-separated move-and-slide **plus step-up**: at cm-scale voxels every slope is a staircase of tiny ledges, so horizontal motion must climb small obstructions or the player wedges instantly. Also has fly/noclip.
- `Camera` — free-look; exposes only an origin + orthonormal basis + FOV (no matrices), matching what the raymarcher consumes.

## When changing key constants

- **World dimensions** live in `Game` (`BrickDimX/Y/Z`). The `Index[]` is dense over the volume at 4 bytes/brick, so Y height directly drives VRAM — keep Y just tall enough for the terrain plus headroom.
- **`RaymarchRenderer.MaxBrickSteps`** must cover the grid's brick diagonal (~`dimX+dimY+dimZ`) or distant terrain is clipped mid-ray.
- **Sun direction must be normalised** before upload — the shader uses it as a shadow-ray direction and `SHADOW_RANGE` assumes unit length.
