# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

VoxSlop is a voxel-based 3D FPS tech demo built on **.NET 10** and **Silk.NET** (windowing + input + OpenGL 4.3). Voxels are small — currently **2 cm** (`VoxelWorld.VoxelSize`) — so even a ~100 m world holds billions of voxel *slots*. It is **not** Minecraft-style: there are no meshes and no per-block geometry. The entire world is drawn by one fullscreen fragment shader that raymarches a sparse voxel structure on the GPU.

Single project: `VoxSlop.App` (see `VoxSlop.slnx`). No test project exists.

## Commands

```sh
dotnet build VoxSlop.slnx           # build
dotnet run --project VoxSlop.App    # build + run
```

- **Running the app locks `VoxSlop.App.exe`.** A `dotnet build` while it is running compiles the DLL but fails to copy the exe (MSB3026). Close the app before rebuilding, or ignore the copy warning if you only need the DLL/shaders refreshed.
- There is **no test framework** and no lint step configured.

## Rendering architecture (the core of the project)

Understanding these three files together is essential: `Voxels/VoxelWorld.cs`, `Render/RaymarchRenderer.cs`, `Render/Shaders/raymarch.frag`.

**Brickmap, not meshes.** The world is a sparse voxel store in "brickmap" form (`VoxelWorld`):
- `Index[]` — one `uint` per brick, dense over the whole 3D volume. A brick is 8×8×8 voxels.
- `Pool[]` — packed voxel payloads (`UintsPerBrick` = 128 uints, i.e. 4 voxels/uint) for *partial* bricks only.
- Index entry encoding: `0` = empty (`EntryEmpty`), high bit set (`UniformFlag`) = whole brick is the material in the low byte, otherwise the value is `poolSlot + 1`. Empty and uniform bricks cost **no** pool storage — that is what keeps a billion-slot world in a few hundred MB.

**The single most important invariant:** `Index[]` and `Pool[]` are uploaded verbatim as SSBOs and traversed by `raymarch.frag`. **The C# memory layout and the GLSL decoding must stay in lockstep** — the same brick edge (8), the same `128u` stride, the same entry encoding, the same index formula `x + dimX*(y + dimY*z)`. Change one side and you must change the other.

**How a frame is drawn** (`RaymarchRenderer.Render`): no vertex buffers, no view/projection matrices. The vertex shader synthesises a fullscreen triangle from `gl_VertexID`; the fragment shader casts one primary ray per pixel and does a **two-level DDA** — march bricks, and descend into an 8³ voxel DDA only inside partial bricks. Cost scales with **screen resolution, not voxel count**, which is why the voxels can be this small.

**Units convention (easy to get wrong):** gameplay code (`Camera`, `PlayerController`, collision) works in **metres**. The shader and DDA work in **voxel units**. The renderer bridges them by dividing positions by `VoxelWorld.VoxelSize` before uploading. `VoxelSize` is the master scale constant — changing it rescales the whole physical world.

**Fixed SSBO bindings:** `0` = brick index, `1` = voxel pool, `2` = shadow cache, `3` = dynamic shapes.

## Large-world memory limits (hit these easily)

Two distinct GPU buffers scale differently, and both have bitten this project:

- **Index (dense over volume):** 4 bytes/brick × dimX × dimY × dimZ. Grows with **height too** — tall mostly-empty headroom still costs VRAM (air bricks are `EntryEmpty` but still occupy an index slot). ~500 MB at 640×320×640.
- **Pool (surface shell):** grows with **surface area**, roughly `(dimX·dimZ bricks) × ~2 × 512 bytes`. At 2 cm this reaches **>2 GB around a 250 m world**, which is the practical ceiling.

Past ~2 GB per buffer you hit hard limits: a byte span (`MemoryMarshal.AsBytes`) can't exceed `int.MaxValue`, and many drivers cap `GL_MAX_SHADER_STORAGE_BLOCK_SIZE` at 2 GB. Guards already in place: buffer byte-size math uses `nuint` (not `int*int`), `WorldStore` reads/writes arrays in 512 MB chunks, and `RaymarchRenderer.UploadStorage` prints a WARNING if a buffer exceeds the driver's block-size cap (symptom of exceeding it: gray/corrupt surface where the pool failed to upload while the index still renders). The real fix for going bigger is a sparse top-level index and/or a compressed pool — not yet implemented.

## Shaders

- Shaders live in `Render/Shaders/` and are **copied to the output dir** (not embedded), so they can be **hot-reloaded with `R`** at runtime. `R`-reload reads from `bin/.../Render/Shaders/`, so to iterate without a full rebuild either edit the copy in `bin` or rebuild (which re-copies) then press `R`.
- **Startup shader compile failure is fatal** — it throws from the `RaymarchRenderer` constructor and is not caught. Only `TryReloadShaders` (the `R` path) catches errors and keeps the previous working shader.
- **No `glslangValidator`/`glslc` is installed**, so GLSL is only validated by the driver at runtime; `dotnet build` will not catch shader errors. Two recurring footguns: **non-ASCII bytes** (e.g. an em-dash in a comment) crash some drivers even inside comments — keep shader files ASCII-only; and GLSL **reserved words** (`patch` is reserved for tessellation in GLSL 4.x) produce confusing syntax errors. GLSL supports C-style function prototypes — used near the top of the file to forward-declare `shapesOcclude`.

## Lighting and the voxel-face shadow cache

Two lights, both quantised to **one value per voxel face** (not per pixel): `voxelFaceLight` / `pointFaceVisible` sample an N×N grid across the face derived from `hit.voxel` + `hit.normal` (never the per-pixel hit point), so every pixel on a face gets the identical coverage ratio.

- **Sun** — directional, animated by `Game.UpdateSun`. Its terrain shadow uses a **lock-free GPU shadow cache** (`faceShadow`, SSBO binding 2): a face hashes to a slot holding `(faceKey, epoch, lightValue)`. Correctness does not depend on synchronisation — a shadow is a deterministic function of `(face, sun)`, so racing writers produce identical values and any miss/stale-epoch/collision recomputes. A memory barrier after each draw makes writes visible next frame. The **epoch** (`RaymarchRenderer.ShadowEpoch`) is bumped only after the sun moves `ShadowEpochStep`, so a paused/slow sun reuses cached shadows.
- **Point light** — positional, orbits the player (`Game.UpdatePointLight`). Inverse-square attenuation where **strength gates reach**: below a threshold the surface is treated as unlit and the shadow ray is skipped (both correctness and the perf guard). Drawn as an emissive single voxel via a ray/AABB test.

**Sun direction must be normalised** before upload — the shader uses it as a shadow-ray direction and `SHADOW_RANGE` assumes unit length.

## Two kinds of shapes: baked vs dynamic

Both re-voxelise onto the **world grid** (world-axis-aligned voxels), which is what keeps rotated shapes looking blocky rather than smoothly rotated.

- **Baked shapes** (`Shape` in `WorldGen.cs`; `Box`, `OrientedBox`, `Cube`, `Sphere`, each with optional rotation and a `subtractive` flag) are stamped into the brickmap at generation time. Static, but get shadows/collision for free because they are real world voxels. Used for the pillars and the walk-through wall.
- **Dynamic shapes** (`DynamicShape` in `Engine/`, uploaded to SSBO binding 3, traced by `traceShapes`/`traceOneShape`) are rendered live each frame, so they can spin. They depth-composite with the world, are shaded like world voxels, and **cast/receive/self-shadow**: shadow rays also test `shapesOcclude`. To keep the sun shadow cache valid (shapes move every frame but the cache is world-only), terrain sun shadow is `faceShadow(world, cached) × shapeSunFactor(shapes, uncached)`. Dynamic shapes currently have **no player collision** (removed; to be redone).

Note `traceOneShape` computes the ray/AABB entry itself and starts the DDA at `max(tN, 0)` — do **not** substitute `rayBox` there, which returns the far exit for an inside origin and makes shadow rays miss the shape (was a real "clipped shadows" bug).

## World generation and caching

`WorldGen.Generate(dims, seed, addTerrainNoise)` is a **multi-pass** builder — classify every brick, prefix-sum the ones needing storage, fill only those, then a compaction pass reclaims bricks that turned out uniform. It never samples all voxel slots directly; it reasons at brick granularity from the heightfield and only samples partial bricks. Parallelised with `Parallel.For`, and prints per-phase progress to the console (heightmap / classify / fill) — generation is slow at high resolution. `addTerrainNoise` scatters sparse 1–2 voxel grass tufts; the heightmap also adds short-wavelength noise before rounding so height-step contours look ragged, not artificial.

`WorldStore` persists to `world.voxcache` next to the exe. It is a **cache, not a save format**: the header records geometry inputs (grid dims, seed, `VoxelSize`, brick edge, `addTerrainNoise`, layout version) and **any mismatch triggers regeneration**, so changing those constants is safe. **The cache does NOT hash the shape list or generation code** — after editing `BuildShapes`/`BuildHeightmap` logic, bump `WorldStore.LayoutVersion` (or delete the cache) or you will load a stale world. The cache lives in `bin/`, so a clean rebuild discards it.

## Gameplay layout

- `Game` owns the window and the Load/Update/Render loop, input, the animated sun, the orbiting point light, the dynamic shapes, and the runtime toggles (controls are printed to the console on start; keys include `F` fly/noclip, `L` shadows, `P` pause sun, `C` shadow cache, `G` point light, `R` reload shaders).
- `PlayerController` — metric AABB collision against the voxel volume with axis-separated move-and-slide **plus step-up**: at cm-scale voxels every slope is a staircase of tiny ledges, so horizontal motion must climb small obstructions or the player wedges instantly. Also fly/noclip.
- `Camera` — free-look; exposes only an origin + orthonormal basis + FOV (no matrices), matching what the raymarcher consumes.

## When changing key constants

- **World dimensions** live in `Game` (`BrickDimX/Y/Z`). See the memory-limits section: Y drives index VRAM; X·Z drive both index and (via surface area) the pool.
- **`RaymarchRenderer.MaxBrickSteps`** must cover the grid's brick diagonal (~`dimX+dimY+dimZ`) or distant terrain is clipped mid-ray.
- **`FOG_DENSITY`** in the shader is per *voxel* of distance, so it must be retuned when `VoxelSize` changes to keep physical visibility roughly constant.
- **The shape-march cap** (loop bound in `traceOneShape`) is in voxels, so it must grow if `VoxelSize` shrinks or a metre-sized shape would span more voxels than the cap.
