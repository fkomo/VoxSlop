# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

VoxSlop is a voxel-based 3D FPS tech demo built on **.NET 10** and **Silk.NET** (windowing + input + OpenGL 4.3). Voxels are small — currently **5 cm** (`VoxelWorld.VoxelSize`) — so even a ~100 m world holds billions of voxel *slots*. It is **not** Minecraft-style: there are no meshes and no per-block geometry. The world is drawn by fragment shaders that raymarch a sparse voxel structure on the GPU.

Single project: `VoxSlop.App` (see `VoxSlop.slnx`). No test project exists.

## Commands

```sh
dotnet build VoxSlop.slnx           # build
dotnet run --project VoxSlop.App    # build + run
```

- **Running the app locks `VoxSlop.App.exe`.** A `dotnet build` while it is running compiles the DLL but fails to copy the exe (MSB3026). Close the app before rebuilding, or ignore the copy warning if you only need the DLL/shaders refreshed.
- There is **no test framework** and no lint step configured.

## Brickmap storage (the core data structure)

Understanding these together is essential: `Voxels/VoxelWorld.cs`, `Render/RaymarchRenderer.cs`, `Render/Shaders/raymarch.frag`.

The world is a sparse voxel store in "brickmap" form (`VoxelWorld`):
- `Index[]` — one `uint` per brick, dense over the whole 3D volume. A brick is 8×8×8 voxels.
- `Pool[]` — packed voxel payloads (`UintsPerBrick` = 128 uints, 4 voxels/uint) for *partial* bricks only.
- `MipPool[]` — a 4³ downsample (`MipUintsPerBrick` = 16 uints) of each stored brick, same slot indexing as `Pool`. **Derived** from the pool by `VoxelWorld.BuildMips`, called after both generation and cache load — it is *not* serialized. Used for distance LOD.
- Index entry encoding: `0` = empty (`EntryEmpty`), high bit set (`UniformFlag`) = whole brick is the material in the low byte, otherwise the value is `poolSlot + 1`. Empty and uniform bricks cost **no** pool storage.

**The single most important invariant:** `Index[]`, `Pool[]`, `MipPool[]` are uploaded verbatim as SSBOs and decoded by `raymarch.frag`. **The C# memory layout and the GLSL decoding must stay in lockstep** — same brick edge (8), same `128u`/`16u` strides, same entry encoding, same index formula `x + dimX*(y + dimY*z)`. Change one side and you must change the other.

**Fixed SSBO bindings:** `0` = brick index, `1` = voxel pool, `2` = shadow cache, `3` = dynamic shapes, `4` = mip pool.

**Units convention (easy to get wrong):** gameplay code (`Camera`, `PlayerController`, collision) works in **metres**. The shader/DDA work in **voxel units**. The renderer bridges by dividing positions by `VoxelWorld.VoxelSize` before uploading. `VoxelSize` is the master scale constant — changing it rescales the whole physical world (and see "When changing key constants" — several shader values are per-voxel and must be retuned).

## The render pipeline (multi-pass, in `RaymarchRenderer.Render`)

No vertex buffers, no view/projection matrices. `raymarch.vert` synthesises a fullscreen triangle from `gl_VertexID`; the fragment shaders do the work. Three fragment shaders in `Render/Shaders/` (all share the vertex shader):

1. **`raymarch.frag` → offscreen `sceneTex`** (RGBA16F: rgb = tonemapped colour, **a = scene depth** in voxel units, −1 for sky). Casts one primary ray per pixel and does a **two-level DDA** — march bricks, descend into an 8³ voxel DDA only inside partial bricks. Cost scales with **screen resolution, not voxel count**. Applies the sub-pixel `uJitter` for TAA.
2. **`taa.frag` → ping-pong history** (only when TAA on). Temporal anti-aliasing.
3. **`present.frag` → screen.** Applies the retro palette/dither filter and draws the crosshair. **Both** the TAA and non-TAA paths render to `sceneTex` then go through present — the crosshair and retro filter live here so TAA can't smear/average them.

**Distance LOD / mip.** Beyond `mipDist` (≈ where a voxel is <1 px, computed from resolution and FOV), partial bricks trace the 4³ **mip** (binding 4) instead of the full 8³ grid — this is what stops distant sub-pixel voxels shimmering. The near↔far switch is **dithered** across a band (interleaved-gradient noise keyed on the pixel) so it's a soft stipple, not a hard ring; TAA resolves the stipple. Separately, `detailAt(t)` fades the high-frequency per-voxel colour terms with distance to kill colour sparkle.

**TAA (`taa.frag`).** Reprojection uses the camera basis + FOV directly (no matrices), matching the raymarcher: reconstruct the world point from `sceneTex` depth at the **pixel centre** (unjittered — reprojecting the jittered ray causes permanent blur), project through the previous frame's camera to get the history UV, blend. Ghosting control is **variance clipping** (clamp history to the neighbourhood `mean ± 1.25σ`) plus **soft depth-based disocclusion** (history carries depth in alpha; a large depth mismatch just raises the blend to ~0.4 rather than a hard reset, which avoided aliased edges that never re-converged). `RaymarchRenderer` stores the previous camera and issues the jitter (Halton).

## Lighting, shadows, ambient occlusion

- **Voxel-face shadows.** Both lights are quantised to **one value per voxel face** (not per pixel): `voxelFaceLight` / `pointFaceVisible` sample an N×N grid across the face derived from `hit.voxel` + `hit.normal`, never the per-pixel hit point.
- **Sun** — directional, animated by `Game.UpdateSun`. Terrain shadow uses a **lock-free GPU shadow cache** (`faceShadow`, binding 2): a face hashes to a slot holding `(faceKey, epoch, lightValue)`. Correctness does not need synchronisation — a shadow is a deterministic function of `(face, sun)`, so racing writers produce identical values and any miss/stale-epoch/collision recomputes. A memory barrier after each draw makes writes visible next frame. The **epoch** (`RaymarchRenderer.ShadowEpoch`) bumps only after the sun moves `ShadowEpochStep`, so a paused/slow sun reuses cached shadows. **Sun direction must be normalised** — it is a shadow-ray direction and `SHADOW_RANGE` assumes unit length.
- **Point light** — positional, orbits the player (`Game.UpdatePointLight`). Inverse-square attenuation where **strength gates reach**: below `POINT_MIN` the surface is unlit and the shadow ray is skipped (correctness *and* the perf guard). Drawn as an emissive single voxel via a ray/AABB test.
- **Ambient occlusion** (`faceAO`) — classic voxel corner AO from `solidAt` neighbour queries, bilinear across the face, multiplies the **ambient** term only. Gated to the near field.

## Voxel visual styles (runtime toggles)

The look can be restyled without changing geometry (all raymarched, no polygons):
- **Rounded blobs** (`uBlob`) — refines a flat DDA hit onto the 0.5 isosurface of a trilinear occupancy density field (`densityAt`/`blobRefine`); near field only.
- **Sphere/bead voxels** (`uSphere`) — ray-sphere test per solid voxel inside the fine DDA (`SPHERE_R`); a miss keeps marching so gaps show. Automatically near-field only because it lives in the fine DDA (far uses the mip).
- **Retro palette + dither** (`uRetro`, in `present.frag`) — quantises the final image to a fixed PICO-8 palette with 4×4 Bayer ordered dithering. Must run in present, *after* TAA, or TAA averages the dither away.

Blobs and spheres produce a **smooth** hit normal: it drives diffuse/ambient, but face-based effects (shadow, AO, point light) need an axis-aligned normal, so `snapAxis(hit.normal)` is used for those (`faceN`) while the smooth normal is `shadeN`. Dynamic shapes are a separate path and are *not* restyled.

## Shaders — footguns

- Shaders are **copied to the output dir** (not embedded) and **hot-reload with `R`** (all three frag passes reload together). `R` reads from `bin/.../Render/Shaders/`, so iterate by editing the `bin` copy or rebuild (re-copies) then `R`.
- **Startup shader compile failure is fatal** (throws from the `RaymarchRenderer` constructor, uncaught). Only `TryReloadShaders` (the `R` path) catches and keeps the previous working shaders.
- **No `glslangValidator`/`glslc` installed** — GLSL is validated only by the driver at runtime; `dotnet build` will not catch shader errors. After any shader edit, sanity-check by scanning for **non-ASCII bytes** (an em-dash even in a comment crashes some drivers) and beware GLSL **reserved words** (`patch` is reserved in GLSL 4.x). GLSL C-style function prototypes are used near the top to forward-declare `shapesOcclude`.

## Baked vs dynamic shapes

Both re-voxelise onto the **world grid** (world-axis-aligned voxels), keeping rotated shapes blocky.
- **Baked** (`Shape` in `WorldGen.cs`; `Box`/`OrientedBox`/`Cube`/`Sphere`, optional rotation, `subtractive` flag) are stamped into the brickmap at generation time — static, but get shadows for free as real world voxels (pillars, the walk-through wall).
- **Dynamic** (`DynamicShape` in `Engine/`, binding 3, `traceShapes`/`traceOneShape`) render live each frame so they can spin. They depth-composite with the world and **cast/receive/self-shadow** (shadow rays also test `shapesOcclude`). To keep the world-only sun cache valid, terrain sun shadow is `faceShadow(world, cached) × shapeSunFactor(shapes, uncached)`. **No player collision** currently (removed; to be redone).
- `traceOneShape` computes the ray/AABB entry itself and starts the DDA at `max(tN, 0)` — do **not** substitute `rayBox` there (it returns the far exit for an inside origin, making shadow rays miss the shape — a real past bug).

## World generation and caching

`WorldGen.Generate(dims, seed, addTerrainNoise)` is a **multi-pass** builder — classify every brick, prefix-sum the ones needing storage, fill only those, then a compaction pass reclaims bricks that turned out uniform, then `BuildMips`. It reasons at brick granularity from the heightfield and only samples partial bricks (never all voxel slots). `Parallel.For`, with per-phase console progress. `addTerrainNoise` scatters sparse 1–2 voxel `GrassTuft` (material 6, coloured lighter than ground grass); the heightmap adds short-wavelength noise before rounding so height-step contours look ragged.

`WorldStore` persists to `world.voxcache` next to the exe. It is a **cache, not a save format**: the header records geometry inputs (grid dims, seed, `VoxelSize`, brick edge, `addTerrainNoise`, layout version) and **any mismatch triggers regeneration**, so changing those constants is safe. **The cache does NOT hash the shape list or generation code** — after editing `BuildShapes`/`BuildHeightmap` logic, bump `WorldStore.LayoutVersion` (or delete the cache) or you load a stale world. Arrays are read/written in 512 MB chunks (a byte span can't exceed `int.MaxValue`). The cache lives in `bin/`, so a clean rebuild discards it.

## Large-world memory limits (hit these easily)

- **Index (dense over volume):** 4 bytes/brick × dimX × dimY × dimZ. Grows with **height too** — tall mostly-empty headroom still costs VRAM.
- **Pool (surface shell):** grows with **surface area**, roughly `(dimX·dimZ bricks) × ~2 × 512 bytes`.

Past ~2 GB per buffer you hit hard limits (`MemoryMarshal.AsBytes` can't exceed `int.MaxValue`; many drivers cap `GL_MAX_SHADER_STORAGE_BLOCK_SIZE` at 2 GB). Guards in place: buffer byte-size math uses `nuint`, cache I/O is chunked, and `RaymarchRenderer.UploadStorage` prints a WARNING if a buffer exceeds the driver cap (symptom: gray/corrupt surface where the pool failed to upload while the index still renders). The real fix for going bigger is a sparse top-level index and/or compressed pool — not yet implemented.

## Gameplay layout

- `Game` owns the window and the Load/Update/Render loop, input, the animated sun, the orbiting point light, dynamic shapes, and all runtime toggles (printed to the console on start). Keys include `F` fly/noclip, `L` shadows, `O` AO, `V` blobs, `B` spheres, `K` retro, `P` pause sun, `C` shadow cache, `G` point light, `T` TAA, `F11` borderless fullscreen, `R` reload shaders.
- `PlayerController` — metric AABB collision against the voxel volume with axis-separated move-and-slide **plus step-up**: at cm-scale voxels every slope is a staircase of tiny ledges, so horizontal motion must climb small obstructions or the player wedges instantly. Also fly/noclip.
- `Camera` — free-look; exposes only origin + orthonormal basis + FOV (no matrices), matching what the raymarcher consumes.

## When changing key constants

- **World dimensions** live in `Game` (`BrickDimX/Y/Z`). See memory limits: Y drives index VRAM; X·Z drive both index and (via surface area) the pool.
- **`RaymarchRenderer.MaxBrickSteps`** must cover the grid's brick diagonal (~`dimX+dimY+dimZ`) or distant terrain is clipped mid-ray.
- **`FOG_DENSITY`** is per *voxel* of distance, so retune it when `VoxelSize` changes to keep physical visibility constant.
- **The shape-march cap** (loop bound in `traceOneShape`) is in voxels, so it must grow if `VoxelSize` shrinks or a metre-sized shape spans more voxels than the cap.
