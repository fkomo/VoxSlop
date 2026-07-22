# STYLE.md — maintainability rules for VoxSlop

Most of this codebase was originally machine-generated. These rules exist so a
human can keep developing it without getting misled. They were distilled while
refactoring the whole solution for readability (2026-07); every rule below was
motivated by a real problem found in the code at that time. Follow them for
every future edit, whether you are a person or an AI assistant.

The single theme: **code and comments must not silently drift apart.** Nothing
here checks itself — no tests, no shader validator, no lockstep analyzer — so
the discipline has to live in the edit habits.

---

## 1. Comments

**A comment states why, or an invariant the code can't show. Never what the
next line does.** If a comment merely restates the code, delete it.

**Never freeze a derived number into a comment.** This was the most common rot
found: "~42 s per full circle" (actual: 126 s), "2 cm of grass" (actual:
10 cm), "at 2 cm the 8 m beam spans ~400 voxels" (voxel size *and* the shape
had both changed). A number that is computed from constants will go stale the
moment someone tunes a constant — usually in a different file. Allowed forms,
in order of preference:

1. **Formula** — stays true forever:
   `// radians per second (full circle = Tau / SunSpeed seconds)`
2. **Stated assumption** — readers can tell when it no longer applies:
   `// a ray can cross up to dimX+dimY+dimZ bricks (~640 at 256x128x256)`
3. **Marked snapshot** — `currently` flags it as a point-in-time value:
   `// (currently ~102 x 51 x 102 m)`

Bare derived numbers with none of these markers are forbidden.

**When you change a constant, hunt its old value.** Grep for both the constant
name and its previous numeric value across `.cs` *and* shader files, and fix
every comment that mentions either — in the same commit. Constants here are
referenced by prose far from where they live (`VoxelSize` alone is discussed in
five files).

**No commented-out code.** Delete it; git remembers. If the *mechanism* should
stay discoverable (e.g. the point light's per-revolution colour cycling, dormant
now that the palette is one entry), describe it in one sentence instead of
leaving dead lines.

**Comments that depend on another file must name it** (`see
Voxels/VoxelWorld.cs`, `see CLAUDE.md "Large-world memory limits"`). "Must match
the shader" without a path forces a search.

**Every tuning constant states its unit** — metres, voxels, radians/second,
"per voxel of distance". Wrong-unit assumptions are the easiest way to break
this codebase (see §2).

## 2. Units — the one convention you must not mix up

- **CPU gameplay code** (`Camera`, `PlayerController`, `Game`, collision):
  **metres**.
- **GPU / shader / DDA code**: **voxel units**. The renderer converts by
  dividing by `VoxelWorld.VoxelSize` at upload time — that is the only place
  conversion happens.
- Shader constants that scale with distance (`FOG_DENSITY`, `SHADOW_RANGE`,
  the shape-march cap) are **per voxel**, so they need retuning whenever
  `VoxelSize` changes. They are marked as such in `raymarch.frag`.

When adding any constant or uniform, say which side of the boundary it is on
(the existing uniforms do: `uCamPos // eye position in VOXEL units`).

## 3. Naming

- **No single- or two-letter file-scope names.** `B` and `MB` in the shader are
  now `BRICK_EDGE` and `MIP_EDGE`; keep it that way. Short names are fine for
  tight local scope (loop indices, `lv`/`bc` inside a DDA loop) where the
  surrounding ten lines define them.
- **No magic numbers where an id has a name.** Material ids in GLSL use the
  `MAT_*` constants that mirror the C# `Materials` class — extend both together.
- GLSL: `SCREAMING_SNAKE` for consts, `u` prefix for uniforms, and **never
  shadow a built-in** (a loop variable named `step` used to shadow `step()`;
  `patch` is a reserved word and once broke a build as an identifier).
- Constants that mirror a value in another language carry an `= Owner.Name`
  note at the declaration, e.g. `const uint POOL_STRIDE = 128u; // =
  VoxelWorld.UintsPerBrick`. That note is the contract (§4).

## 4. The C#/GLSL lockstep contract

The brickmap arrays are uploaded verbatim and decoded by hand in GLSL. **No
tool checks this. Changing one side without the other produces corrupt rendering,
not an error.** The pairs:

| C# (`VoxelWorld` / renderer)          | GLSL (`raymarch.frag`)                  |
|---------------------------------------|-----------------------------------------|
| `VoxelWorld.BrickEdge` (8)            | `BRICK_EDGE`                            |
| `VoxelWorld.MipEdge` (4)              | `MIP_EDGE`                              |
| `VoxelWorld.UintsPerBrick` (128)      | `POOL_STRIDE`                           |
| `VoxelWorld.MipUintsPerBrick` (16)    | `MIP_STRIDE`                            |
| `VoxelWorld.UniformFlag`              | `UNIFORM_FLAG`                          |
| index formula `x + dimX*(y + dimY*z)` | `brickAt()`                             |
| byte packing in `WritePoolVoxel`      | decode in `voxelAt()` / `mipAt()`       |
| `Materials` class ids                 | `MAT_*` constants                       |
| SSBO bindings 0–4 in `RaymarchRenderer` | `layout(binding = N)` declarations    |
| `FloatsPerShape` packing in `UpdateDynamicShapes` | `struct DynShape`           |

Rule: touch a row's left cell, touch its right cell **in the same edit**, then
actually run the app (§6) — a build alone proves nothing about GLSL.

## 5. Structure

- **A multi-phase algorithm is one short driver plus one named method per
  phase.** `WorldGen.Generate` is the template: the driver reads as
  `ClassifyBricks → AssignPoolSlots → FillPartialBricks → CompactPool →
  BuildMips`, and each pass's doc comment says which pass it is and why it
  exists. Same for the frame in `RaymarchRenderer.Render`: `DrawScene →
  ResolveTaa → Present`. Don't let a driver method grow inline pass bodies
  again.
- **Big shader files get a map.** `raymarch.frag` opens with a table of
  contents and is divided by `// --- Section ---` banners. When adding a
  function, put it in the right section and update the map. If a section
  outgrows a screen or two, consider whether it earns its own banner.
- Named states beat magic literals in algorithms: the world generator uses
  `BrickEmpty / BrickUniform / BrickPartial`, not `0 / 1 / 2`.
- **One type per file — no exceptions.** Every class, struct and enum gets its
  own file named after it (`Shape` → `Voxels/Shape.cs`). If a helper type only
  makes sense next to its user, that closeness is expressed by folder and
  namespace, not by sharing a file.

## 6. Shader editing workflow (GLSL has no safety net here)

- `dotnet build` does **not** compile GLSL. The only validators are the driver
  at app start (compile failure = fatal crash) and the `R` hot-reload (failure
  = keeps previous shaders and prints the error — the safer loop).
- Iterate with `R`: it reads from `bin/.../Render/Shaders/`, so either edit the
  bin copy directly or rebuild (which re-copies) before pressing `R`.
- **ASCII only** in shader files — a single em-dash or curly quote, even in a
  comment, crashes some drivers. After editing, check:
  `grep -nP '[^\x00-\x7F]' VoxSlop.App/Render/Shaders/*`
- Before declaring victory on any shader change, run the app and look at the
  picture. Corruption (gray terrain, missing geometry) is the failure mode of
  layout mistakes, not error messages.

## 7. Cache discipline

`world.voxcache` records geometry *inputs* (dims, seed, `VoxelSize`, brick
edge, noise flag, `LayoutVersion`) and regenerates on any mismatch — changing
those constants is always safe. But it does **not** hash generation *code*:
after editing `BuildShapes`, `BuildHeightmap`, `SampleVoxel` or any material
logic, bump `WorldStore.LayoutVersion` (or delete the cache) or you will be
staring at the old world wondering why your change did nothing.

## 8. Pre-finish checklist for any edit

- [ ] Changed a constant? Grep its name *and old value* through `.cs`, `.frag`,
      `.md` and fix stale mentions.
- [ ] Touched anything in the §4 table? Updated the other language and ran the
      app to look at the frame.
- [ ] Touched shader files? ASCII check passed; app starts (or `R` reload
      succeeds).
- [ ] Changed world-generation logic? Bumped `WorldStore.LayoutVersion`.
- [ ] Changed `VoxelSize`? Retuned the per-voxel shader constants (marked in
      `raymarch.frag`) and the shape-march cap.
- [ ] Left no commented-out code, no unmarked derived numbers, no "temp" notes.
- [ ] New code reads like the neighbouring code — same comment density, same
      naming style.
