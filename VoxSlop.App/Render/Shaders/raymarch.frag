#version 430 core

// Two-level DDA over a brickmap. One primary ray per pixel, plus one shadow ray.
// Cost scales with resolution, not with voxel count -- which is the whole reason
// a billion-voxel world is tractable here.
//
// Buffer layout must match Voxels/VoxelWorld.cs.

out vec4 FragColor;

layout(std430, binding = 0) readonly buffer BrickIndex { uint bricks[]; };
layout(std430, binding = 1) readonly buffer VoxelPool  { uint voxels[]; };

// Distance LOD: each stored brick also has a 4^3 (16-uint) downsample. Far bricks
// trace this instead of the full 8^3 grid, so sub-pixel voxels stop shimmering.
layout(std430, binding = 4) readonly buffer MipPool { uint mipVoxels[]; };

// Per-voxel-face shadow cache. One slot holds (keyLo, keyHi, epoch, lightBits).
// coherent so writes from earlier draws are visible to later ones after a barrier.
layout(std430, binding = 2) coherent buffer ShadowCache { uvec4 shadowCache[]; };

// Live (spinning) shapes, re-voxelised onto the world grid each frame. Packed as
// 5 vec4 per shape; see RaymarchRenderer for the exact byte layout.
//   centreType : xyz = centre (voxel units), w = type (0 = box, 1 = sphere)
//   ax/ay/az   : xyz = orthonormal world-space axis, w = half extent (radius in ax.w)
//   colour     : rgb
struct DynShape { vec4 centreType; vec4 ax; vec4 ay; vec4 az; vec4 colour; };
layout(std430, binding = 3) readonly buffer DynamicShapes { DynShape dynShapes[]; };
uniform int uShapeCount;

// Defined below (uses rayBox); prototyped here so the shadow code above can call it.
bool shapesOcclude(vec3 ro, vec3 rd, float tMax);

uniform ivec3 uBrickDims;    // grid size in bricks
uniform vec3  uCamPos;       // eye position in VOXEL units
uniform vec3  uCamRight;
uniform vec3  uCamUp;
uniform vec3  uCamForward;
uniform vec2  uResolution;
uniform float uTanHalfFov;
uniform vec2  uJitter;       // sub-pixel NDC offset for TAA (zero when disabled)
uniform vec3  uSunDir;       // normalised, points toward the sun
uniform int   uShadows;
uniform int   uAo;              // 0 = ambient occlusion off
uniform int   uBlob;            // 1 = round voxels into smooth blobs (near field)
uniform int   uSphere;          // 1 = render each voxel as a sphere (near field)
uniform int   uMaxBrickSteps;
uniform int   uShadowSamples;   // sub-samples per axis across a voxel face (NxN)
uniform int   uShadowCache;     // 0 = recompute per pixel, 1 = use the face cache
uniform uint  uShadowEpoch;     // cache generation; bumped when the sun moves enough
uniform uint  uShadowCacheSize; // number of slots in shadowCache

// Orbiting point light.
uniform int   uPointOn;         // 0 = disabled
uniform vec3  uPointPos;        // position in VOXEL units
uniform vec3  uPointColor;      // linear RGB
uniform float uPointStrength;   // intensity; also gates whether a surface is lit at all

const int   B            = 8;          // voxels per brick edge
const uint  UNIFORM_FLAG = 0x80000000u;
const float FOG_DENSITY  = 0.00004;    // per voxel of distance; light enough to see far terrain
const float SHADOW_RANGE = 2500.0;     // voxels; long enough for terrain to shade itself
const float POINT_FALLOFF = 0.0030;    // inverse-square attenuation scale (per voxel^2)
const float POINT_MIN     = 0.03;      // below this, a surface receives no meaningful light
const float SPHERE_R      = 0.7;      // sphere-voxel radius; >0.71 fully merges, <0.5 gaps

uint brickAt(ivec3 bc)
{
    return bricks[bc.x + uBrickDims.x * (bc.y + uBrickDims.y * bc.z)];
}

uint voxelAt(uint slot, ivec3 lv)
{
    uint li = uint(lv.x + B * (lv.y + B * lv.z));   // 0..511
    uint word = voxels[slot * 128u + (li >> 2)];
    return (word >> ((li & 3u) * 8u)) & 0xFFu;
}

const int MB = 4;   // mip cells per brick edge (each cell = 2 voxels)

uint mipAt(uint slot, ivec3 mc)
{
    uint li = uint(mc.x + MB * (mc.y + MB * mc.z));  // 0..63
    uint word = mipVoxels[slot * 16u + (li >> 2)];
    return (word >> ((li & 3u) * 8u)) & 0xFFu;
}

// Direct material query for a world voxel (index -> pool). 0 = air / out of bounds.
uint materialAt(ivec3 v)
{
    if (any(lessThan(v, ivec3(0))) || any(greaterThanEqual(v, uBrickDims * B))) return 0u;
    uint entry = brickAt(v >> 3);
    if (entry == 0u) return 0u;
    if ((entry & UNIFORM_FLAG) != 0u) return entry & 0xFFu;
    return voxelAt(entry - 1u, v & 7);
}

bool solidAt(ivec3 v) { return materialAt(v) != 0u; }

// Hash of an integer voxel cell, in [0,1]. Everything procedural here builds on it.
float hashCell(ivec3 v)
{
    uint h = uint(v.x * 73856093 ^ v.y * 19349663 ^ v.z * 83492791);
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;
    return float(h & 0xFFFFu) / 65535.0;
}

// Trilinear value noise over the voxel grid, smoothstepped so octaves don't
// show their cell boundaries.
float valueNoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = p - i;
    f = f * f * (3.0 - 2.0 * f);
    ivec3 c = ivec3(i);

    float n000 = hashCell(c + ivec3(0, 0, 0));
    float n100 = hashCell(c + ivec3(1, 0, 0));
    float n010 = hashCell(c + ivec3(0, 1, 0));
    float n110 = hashCell(c + ivec3(1, 1, 0));
    float n001 = hashCell(c + ivec3(0, 0, 1));
    float n101 = hashCell(c + ivec3(1, 0, 1));
    float n011 = hashCell(c + ivec3(0, 1, 1));
    float n111 = hashCell(c + ivec3(1, 1, 1));

    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

// Grass gets three scales of variation stacked on top of each other: broad
// patches of dry-vs-lush, smaller clumps inside those, and a per-voxel speckle.
// `detail` (1 near, 0 far) fades the highest-frequency terms so that distant,
// sub-pixel voxels stop sparkling.
vec3 grassColor(ivec3 v, float detail)
{
    // Note: "patch" is a reserved word in GLSL 4.x (tessellation), hence "meadow".
    float meadow = valueNoise(vec3(v) / 90.0);    // broad dry/lush patches
    float clump  = valueNoise(vec3(v) / 18.0);    // knee-high clumps
    float speck  = mix(0.5, hashCell(v), detail); // per-voxel; fades to its mean far off

    float t = clamp(meadow * 0.55 + clump * 0.33 + speck * 0.12, 0.0, 1.0);

    // Warm straw tips -> fresh blade green -> cool deep shade.
    const vec3 DRY  = vec3(0.53, 0.54, 0.24);
    const vec3 MID  = vec3(0.27, 0.47, 0.16);
    const vec3 LUSH = vec3(0.12, 0.31, 0.13);

    vec3 c = t < 0.5 ? mix(DRY, MID, t * 2.0) : mix(MID, LUSH, (t - 0.5) * 2.0);

    // A touch of blue-green in the lushest patches reads as shaded blades.
    c = mix(c, c * vec3(0.82, 1.0, 0.92), smoothstep(0.55, 1.0, t) * 0.6);

    // Gentle per-voxel value jitter -- the main shimmer source, so fade it with distance.
    c *= mix(1.0, 0.93 + 0.14 * hashCell(v + ivec3(17, 0, 41)), detail);

    return c;
}

vec3 surfaceColor(uint m, ivec3 v, float detail)
{
    if (m == 1u) return grassColor(v, detail) * 0.68;   // ground grass, darker
    if (m == 6u) return grassColor(v, detail) * 1.30;   // raised tufts, lighter

    vec3 base;
    if      (m == 2u) base = vec3(0.42, 0.30, 0.18);   // dirt
    else if (m == 3u) base = vec3(0.44, 0.44, 0.47);   // stone
    else if (m == 4u) base = vec3(0.70, 0.68, 0.64);   // concrete
    else if (m == 5u) base = vec3(0.83, 0.42, 0.14);   // rust
    else              base = vec3(1.0, 0.0, 1.0);      // unknown material

    // Mild grain up close, faded out where voxels go sub-pixel.
    return base * mix(1.0, 0.90 + 0.20 * hashCell(v), detail);
}

// Detail LOD in [0,1]: 1 where a voxel is at least ~1 pixel wide, falling to 0 as
// voxels shrink below pixel size with distance. `dist` is in voxel units.
float detailAt(float dist)
{
    float subpixel = uResolution.y / (2.0 * uTanHalfFov); // distance where a voxel ~= 1 px
    return 1.0 - smoothstep(subpixel, subpixel * 4.0, dist);
}

bool intersectGrid(vec3 ro, vec3 rd, vec3 bmax, out float t0, out float t1, out vec3 entryNormal)
{
    vec3 inv = 1.0 / rd;
    vec3 a = (vec3(0.0) - ro) * inv;
    vec3 b = (bmax - ro) * inv;
    vec3 lo = min(a, b);
    vec3 hi = max(a, b);

    t0 = max(max(lo.x, lo.y), lo.z);
    t1 = min(min(hi.x, hi.y), hi.z);

    // The slab that produced t0 tells us which face we came in through.
    entryNormal = vec3(lessThanEqual(lo.yzx, lo.xyz)) * vec3(lessThanEqual(lo.zxy, lo.xyz));
    entryNormal *= -sign(rd);

    return t1 >= max(t0, 0.0);
}

struct Hit
{
    float t;
    vec3  normal;
    uint  material;
    ivec3 voxel;
};

// Marches bricks, and descends into voxels only for bricks that hold a surface.
bool trace(vec3 ro, vec3 rd, float tMax, out Hit hit)
{
    vec3 gridMax = vec3(uBrickDims * B);

    float t0, t1;
    vec3 normal;
    if (!intersectGrid(ro, rd, gridMax, t0, t1, normal)) return false;

    float t = max(t0, 0.0) + 1e-3;
    t1 = min(t1, tMax);
    if (t > t1) return false;

    vec3  sgn   = sign(rd);
    ivec3 istep = ivec3(sgn);

    // Brick-level DDA. All t values stay in voxel units so the inner loop can
    // share them without rescaling.
    ivec3 bc = clamp(ivec3(floor((ro + rd * t) / float(B))), ivec3(0), uBrickDims - 1);
    vec3 deltaBrick = abs(float(B) / rd);
    vec3 nextBrick;
    for (int i = 0; i < 3; i++)
    {
        float bound = float(bc[i]) + (sgn[i] > 0.0 ? 1.0 : 0.0);
        nextBrick[i] = (rd[i] == 0.0) ? 1e30 : (bound * float(B) - ro[i]) / rd[i];
    }

    vec3 deltaVoxel = abs(1.0 / rd);

    // Beyond ~this distance (voxel units) a fine voxel is under ~1 pixel, so partial
    // bricks trace the 4^3 mip instead of the full 8^3 grid to stop shimmering.
    float mipDist = uResolution.y / (2.0 * uTanHalfFov);

    // Dither the near/far switch across a band so it is a soft stipple rather than a
    // hard ring. Interleaved-gradient noise keyed on the pixel gives a stable, even
    // pattern; near and far look nearly identical in the band so the stipple is faint.
    float mipDither = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));

    for (int step = 0; step < uMaxBrickSteps; step++)
    {
        uint entry = brickAt(bc);

        if (entry != 0u)
        {
            if ((entry & UNIFORM_FLAG) != 0u)
            {
                hit.t = t;
                hit.normal = normal;
                hit.material = entry & 0xFFu;
                hit.voxel = ivec3(floor(ro + rd * (t + 1e-3)));
                return true;
            }

            // Partial brick. Near: full 8^3 DDA. Far: the 4^3 mip (cell = 2 voxels)
            // so sub-pixel voxels resolve to stable 4 cm cells instead of shimmering.
            uint slot = entry - 1u;
            vec3  base = vec3(bc * B);
            vec3  p    = ro + rd * (t + 1e-3);

            // Probability of using the mip ramps from 0 to 1 across the transition band.
            float lodFrac = smoothstep(mipDist * 0.7, mipDist * 1.9, t);
            if (mipDither < lodFrac)
            {
                ivec3 mc = clamp(ivec3(floor((p - base) * 0.5)), ivec3(0), ivec3(MB - 1));

                vec3 nextCell;
                for (int i = 0; i < 3; i++)
                {
                    float bound = float(mc[i]) + (sgn[i] > 0.0 ? 1.0 : 0.0);
                    nextCell[i] = (rd[i] == 0.0) ? 1e30 : (base[i] + bound * 2.0 - ro[i]) / rd[i];
                }
                vec3 deltaCell = abs(2.0 / rd);
                float tv = t;
                vec3  nv = normal;

                for (int k = 0; k < 3 * MB; k++)
                {
                    uint m = mipAt(slot, mc);
                    if (m != 0u)
                    {
                        hit.t = tv;
                        hit.normal = nv;
                        hit.material = m;
                        hit.voxel = bc * B + mc * 2;
                        return true;
                    }

                    if (nextCell.x < nextCell.y && nextCell.x < nextCell.z)
                    {
                        tv = nextCell.x; nextCell.x += deltaCell.x;
                        mc.x += istep.x;  nv = vec3(-sgn.x, 0.0, 0.0);
                        if (mc.x < 0 || mc.x >= MB) break;
                    }
                    else if (nextCell.y < nextCell.z)
                    {
                        tv = nextCell.y; nextCell.y += deltaCell.y;
                        mc.y += istep.y;  nv = vec3(0.0, -sgn.y, 0.0);
                        if (mc.y < 0 || mc.y >= MB) break;
                    }
                    else
                    {
                        tv = nextCell.z; nextCell.z += deltaCell.z;
                        mc.z += istep.z;  nv = vec3(0.0, 0.0, -sgn.z);
                        if (mc.z < 0 || mc.z >= MB) break;
                    }

                    if (tv > t1) return false;
                }
            }
            else
            {
                ivec3 lv = clamp(ivec3(floor(p - base)), ivec3(0), ivec3(B - 1));

                vec3 nextVoxel;
                for (int i = 0; i < 3; i++)
                {
                    float bound = float(lv[i]) + (sgn[i] > 0.0 ? 1.0 : 0.0);
                    nextVoxel[i] = (rd[i] == 0.0) ? 1e30 : (base[i] + bound - ro[i]) / rd[i];
                }

                float tv = t;
                vec3  nv = normal;

                // A ray can cross at most 3*(B-1)+1 voxels of one brick.
                for (int k = 0; k < 3 * B; k++)
                {
                    uint m = voxelAt(slot, lv);
                    if (m != 0u)
                    {
                        if (uSphere == 0)
                        {
                            hit.t = tv;
                            hit.normal = nv;
                            hit.material = m;
                            hit.voxel = bc * B + lv;
                            return true;
                        }

                        // Bead voxel: intersect this voxel's sphere. On a miss, keep
                        // marching so gaps between beads reveal what is behind.
                        vec3 centre = vec3(bc * B + lv) + 0.5;
                        vec3 oc = ro - centre;
                        float bb = dot(oc, rd);
                        float disc = bb * bb - (dot(oc, oc) - SPHERE_R * SPHERE_R);
                        if (disc >= 0.0)
                        {
                            float th = -bb - sqrt(disc);
                            if (th > 1e-3 && th <= t1)
                            {
                                hit.t = th;
                                hit.normal = normalize(ro + rd * th - centre);
                                hit.material = m;
                                hit.voxel = bc * B + lv;
                                return true;
                            }
                        }
                        // fall through: sphere missed, advance the DDA
                    }

                    if (nextVoxel.x < nextVoxel.y && nextVoxel.x < nextVoxel.z)
                    {
                        tv = nextVoxel.x; nextVoxel.x += deltaVoxel.x;
                        lv.x += istep.x;  nv = vec3(-sgn.x, 0.0, 0.0);
                        if (lv.x < 0 || lv.x >= B) break;
                    }
                    else if (nextVoxel.y < nextVoxel.z)
                    {
                        tv = nextVoxel.y; nextVoxel.y += deltaVoxel.y;
                        lv.y += istep.y;  nv = vec3(0.0, -sgn.y, 0.0);
                        if (lv.y < 0 || lv.y >= B) break;
                    }
                    else
                    {
                        tv = nextVoxel.z; nextVoxel.z += deltaVoxel.z;
                        lv.z += istep.z;  nv = vec3(0.0, 0.0, -sgn.z);
                        if (lv.z < 0 || lv.z >= B) break;
                    }

                    if (tv > t1) return false;
                }
            }
        }

        // Advance to the next brick.
        if (nextBrick.x < nextBrick.y && nextBrick.x < nextBrick.z)
        {
            t = nextBrick.x; nextBrick.x += deltaBrick.x;
            bc.x += istep.x; normal = vec3(-sgn.x, 0.0, 0.0);
            if (bc.x < 0 || bc.x >= uBrickDims.x) return false;
        }
        else if (nextBrick.y < nextBrick.z)
        {
            t = nextBrick.y; nextBrick.y += deltaBrick.y;
            bc.y += istep.y; normal = vec3(0.0, -sgn.y, 0.0);
            if (bc.y < 0 || bc.y >= uBrickDims.y) return false;
        }
        else
        {
            t = nextBrick.z; nextBrick.z += deltaBrick.z;
            bc.z += istep.z; normal = vec3(0.0, 0.0, -sgn.z);
            if (bc.z < 0 || bc.z >= uBrickDims.z) return false;
        }

        if (t > t1) return false;
    }

    return false;
}

vec3 skyColor(vec3 rd)
{
    float h = clamp(rd.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 sky = mix(vec3(0.62, 0.68, 0.76), vec3(0.24, 0.42, 0.75), h);
    float sun = pow(max(dot(rd, uSunDir), 0.0), 320.0);
    return sky + vec3(1.0, 0.92, 0.75) * sun;
}

// Fraction of the lit voxel FACE that can see the sun, returned as a light factor
// in [0,1]. The sample points are derived from the voxel coordinate and its face
// normal -- NOT from the per-pixel hit point -- so every pixel covering the face
// evaluates the same set of rays and gets an identical result. That is what makes
// the shadow resolution one-per-voxel instead of one-per-pixel, while sub-sampling
// across the face still yields a soft coverage ratio at the voxel's own edges.
float voxelFaceLight(ivec3 voxel, vec3 n)
{
    // Outer face plane: voxel centre pushed half a voxel along the normal.
    vec3 faceCentre = vec3(voxel) + 0.5 + n * 0.5;

    // Orthonormal tangent frame spanning the face. n is always axis-aligned here.
    vec3 t1 = (abs(n.y) > 0.5) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 t2 = normalize(cross(n, t1));
    t1 = cross(t2, n);

    int s = clamp(uShadowSamples, 1, 8);
    float inv = 1.0 / float(s);
    float lit = 0.0;

    for (int i = 0; i < s; i++)
    for (int j = 0; j < s; j++)
    {
        // Centre of each sub-cell, in [-0.5, 0.5] across the face. A small inset
        // (0.5) keeps samples off the shared edges with neighbouring voxels.
        float u = (float(i) + 0.5) * inv - 0.5;
        float v = (float(j) + 0.5) * inv - 0.5;

        // Start just off the face so the ray cannot immediately re-hit this voxel.
        vec3 origin = faceCentre + t1 * u + t2 * v + n * 1e-2;

        Hit blocker;
        if (!trace(origin, uSunDir, SHADOW_RANGE, blocker)) lit += 1.0;
    }

    return lit * (inv * inv);
}

// Packs a voxel face into a 64-bit key. Voxel coords reach ~8192 (13 bits) in X/Z
// and a few hundred (10 bits) in Y; the face index is 0..5 (3 bits).
uvec2 faceKey(ivec3 voxel, int faceIndex)
{
    uint lo = uint(voxel.x) | (uint(voxel.z) << 13);            // 13 + 13 bits
    uint hi = uint(voxel.y) | (uint(faceIndex) << 12);          // 12 + 3 bits
    return uvec2(lo, hi);
}

uint hashKey(uvec2 k)
{
    uint h = k.x * 2654435761u ^ k.y * 2246822519u;
    h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
    return h;
}

int faceIndexOf(vec3 n)
{
    if (n.x > 0.5) return 0; if (n.x < -0.5) return 1;
    if (n.y > 0.5) return 2; if (n.y < -0.5) return 3;
    return (n.z > 0.5) ? 4 : 5;
}

// Voxel-face shadow, computed once per (face, sun epoch) and reused across every
// pixel that covers the face. The cached value is a deterministic function of the
// face and sun, so a race between two pixels computing the same slot is harmless:
// both write the identical number. A slot mismatch (empty, stale epoch, or a hash
// collision with a different face) simply recomputes -- never a wrong value.
float faceShadow(ivec3 voxel, vec3 n)
{
    if (uShadowCache == 0) return voxelFaceLight(voxel, n);

    uvec2 key = faceKey(voxel, faceIndexOf(n));
    uint slot = hashKey(key) % uShadowCacheSize;

    uvec4 e = shadowCache[slot];
    if (e.x == key.x && e.y == key.y && e.z == uShadowEpoch)
        return uintBitsToFloat(e.w);

    float light = voxelFaceLight(voxel, n);
    shadowCache[slot] = uvec4(key.x, key.y, uShadowEpoch, floatBitsToUint(light));
    return light;
}

// Fraction of a voxel face that can see a point light at lightPos (voxel units).
// Same face-quantised sampling as the sun, but each ray is cast toward the light
// and only counts occluders BETWEEN the face and the light, not past it.
float pointFaceVisible(ivec3 voxel, vec3 n, vec3 lightPos)
{
    vec3 faceCentre = vec3(voxel) + 0.5 + n * 0.5;

    vec3 t1 = (abs(n.y) > 0.5) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 t2 = normalize(cross(n, t1));
    t1 = cross(t2, n);

    int s = clamp(uShadowSamples, 1, 8);
    float inv = 1.0 / float(s);
    float lit = 0.0;

    for (int i = 0; i < s; i++)
    for (int j = 0; j < s; j++)
    {
        float u = (float(i) + 0.5) * inv - 0.5;
        float v = (float(j) + 0.5) * inv - 0.5;

        vec3 origin = faceCentre + t1 * u + t2 * v + n * 1e-2;
        vec3 d = lightPos - origin;
        float dist = length(d);
        vec3 dir = d / dist;

        Hit blocker;
        // Stop short of the light so we don't count geometry at or behind it.
        // A sample is lit only if neither the world nor any live shape blocks it.
        if (!trace(origin, dir, dist - 0.5, blocker) && !shapesOcclude(origin, dir, dist - 0.5))
            lit += 1.0;
    }

    return lit * (inv * inv);
}

// Colored contribution of the orbiting point light on a voxel face. Strength
// drives inverse-square attenuation AND gates the work: if strength * attenuation
// * NdotL is below POINT_MIN the surface receives no meaningful light, so we skip
// the shadow test entirely (both a correctness statement and the perf guard that
// keeps the lit region local to the light).
vec3 pointLight(ivec3 voxel, vec3 n)
{
    if (uPointOn == 0) return vec3(0.0);

    vec3 faceCentre = vec3(voxel) + 0.5 + n * 0.5;
    vec3 d = uPointPos - faceCentre;
    float dist = length(d);
    float ndl = max(dot(n, d / dist), 0.0);

    float atten = 1.0 / (1.0 + dist * dist * POINT_FALLOFF);
    float contrib = uPointStrength * atten * ndl;
    if (contrib < POINT_MIN) return vec3(0.0);

    return uPointColor * contrib * pointFaceVisible(voxel, n, uPointPos);
}

// Nearest intersection of a unit-direction ray with an axis-aligned box, plus the
// entry face normal. Returns false on a miss or if the box is fully behind ro.
bool rayBox(vec3 ro, vec3 rd, vec3 lo, vec3 hi, out float t, out vec3 n)
{
    vec3 inv = 1.0 / rd;
    vec3 a = (lo - ro) * inv;
    vec3 b = (hi - ro) * inv;
    vec3 tmin = min(a, b);
    vec3 tmax = max(a, b);

    float tN = max(max(tmin.x, tmin.y), tmin.z);
    float tF = min(min(tmax.x, tmax.y), tmax.z);
    if (tF < max(tN, 0.0)) return false;

    t = (tN >= 0.0) ? tN : tF;   // use the far face if the origin is inside
    if (t < 0.0) return false;

    // The slab that produced tN is the entry face.
    n = vec3(lessThanEqual(tmin.yzx, tmin.xyz)) * vec3(lessThanEqual(tmin.zxy, tmin.xyz));
    n *= -sign(rd);
    return true;
}

bool insideDynShape(int i, vec3 p)
{
    DynShape s = dynShapes[i];
    vec3 d = p - s.centreType.xyz;
    if (s.centreType.w > 0.5)                 // sphere
        return dot(d, d) <= s.ax.w * s.ax.w;
    return abs(dot(d, s.ax.xyz)) <= s.ax.w
        && abs(dot(d, s.ay.xyz)) <= s.ay.w
        && abs(dot(d, s.az.xyz)) <= s.az.w;
}

void dynShapeAabb(int i, out vec3 lo, out vec3 hi)
{
    DynShape s = dynShapes[i];
    vec3 c = s.centreType.xyz;
    if (s.centreType.w > 0.5) { lo = c - s.ax.w; hi = c + s.ax.w; return; }
    vec3 h = s.ax.w * abs(s.ax.xyz) + s.ay.w * abs(s.ay.xyz) + s.az.w * abs(s.az.xyz);
    lo = c - h; hi = c + h;
}

// Marches the WORLD voxel grid through one shape's AABB and returns the first
// world voxel whose centre lies inside the shape. World-axis-aligned voxels ->
// the spinning shape keeps the block look instead of smooth rotated faces.
bool traceOneShape(int i, vec3 ro, vec3 rd, float tMax, out float outT, out vec3 outN, out ivec3 outVox)
{
    vec3 lo, hi;
    dynShapeAabb(i, lo, hi);

    // Ray/AABB interval. Note: we must start the march at the ENTRY (tN), clamped
    // to 0 when the origin is already inside the box. Using rayBox here would hand
    // back the far exit for an inside origin, so the DDA would start past the body
    // and miss it -- the cause of clipped shadows on rotated shapes.
    vec3 inv = 1.0 / rd;
    vec3 tlo = (lo - ro) * inv;
    vec3 thi = (hi - ro) * inv;
    vec3 tmin3 = min(tlo, thi);
    vec3 tmax3 = max(tlo, thi);
    float tN = max(max(tmin3.x, tmin3.y), tmin3.z);
    float tF = min(min(tmax3.x, tmax3.y), tmax3.z);
    if (tF < max(tN, 0.0)) return false;   // ray misses the box
    if (tN > tMax) return false;

    float tStop = min(tMax, tF);

    float t = max(tN, 0.0) + 1e-3;
    vec3  sgn = sign(rd);
    ivec3 istep = ivec3(sgn);
    vec3  tDelta = abs(inv);

    ivec3 v = ivec3(floor(ro + rd * t));
    vec3 tNext;
    for (int k = 0; k < 3; k++)
    {
        float bound = float(v[k]) + (sgn[k] > 0.0 ? 1.0 : 0.0);
        tNext[k] = (rd[k] == 0.0) ? 1e30 : (bound - ro[k]) * inv[k];
    }

    // Entry face normal from the slab that produced tN (only meaningful when the
    // origin is outside; overwritten by the DDA before any interior hit anyway).
    vec3 n = vec3(lessThanEqual(tmin3.yzx, tmin3.xyz)) * vec3(lessThanEqual(tmin3.zxy, tmin3.xyz));
    n *= -sgn;

    // Cap covers the long axis of the biggest shape in voxels; at 2 cm the 8 m beam
    // spans ~400 voxels and its rotated AABB corridor is longer still.
    for (int k = 0; k < 2048; k++)
    {
        if (t > tStop) return false;
        if (insideDynShape(i, vec3(v) + 0.5)) { outT = t; outN = n; outVox = v; return true; }

        if (tNext.x < tNext.y && tNext.x < tNext.z)
        {
            t = tNext.x; tNext.x += tDelta.x; v.x += istep.x; n = vec3(-sgn.x, 0.0, 0.0);
        }
        else if (tNext.y < tNext.z)
        {
            t = tNext.y; tNext.y += tDelta.y; v.y += istep.y; n = vec3(0.0, -sgn.y, 0.0);
        }
        else
        {
            t = tNext.z; tNext.z += tDelta.z; v.z += istep.z; n = vec3(0.0, 0.0, -sgn.z);
        }
    }
    return false;
}

// Nearest hit across all live shapes, within tMax.
bool traceShapes(vec3 ro, vec3 rd, float tMax, out float bt, out vec3 bn, out ivec3 bv, out vec3 bcol)
{
    bool hitAny = false;
    bt = tMax;
    for (int i = 0; i < uShapeCount; i++)
    {
        float t; vec3 n; ivec3 v;
        if (traceOneShape(i, ro, rd, bt, t, n, v) && t < bt)
        {
            bt = t; bn = n; bv = v; bcol = dynShapes[i].colour.rgb; hitAny = true;
        }
    }
    return hitAny;
}

// Any live shape blocking the segment [ro, ro + rd*tMax]? Used as an occluder for
// shadow rays, so shapes cast shadows onto the world and onto each other.
bool shapesOcclude(vec3 ro, vec3 rd, float tMax)
{
    for (int i = 0; i < uShapeCount; i++)
    {
        float t; vec3 n; ivec3 v;
        if (traceOneShape(i, ro, rd, tMax, t, n, v)) return true;
    }
    return false;
}

// Sun-shadow coverage of a face considering ONLY the live shapes. Kept separate
// from the cached world shadow so the cache stays valid: shapes move every frame,
// but the world's self-shadow only changes when the sun does. Multiply the two.
float shapeSunFactor(ivec3 voxel, vec3 n)
{
    if (uShapeCount == 0) return 1.0;

    vec3 faceCentre = vec3(voxel) + 0.5 + n * 0.5;
    vec3 t1 = (abs(n.y) > 0.5) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 t2 = normalize(cross(n, t1));
    t1 = cross(t2, n);

    int s = clamp(uShadowSamples, 1, 8);
    float inv = 1.0 / float(s);
    float lit = 0.0;

    for (int i = 0; i < s; i++)
    for (int j = 0; j < s; j++)
    {
        float u = (float(i) + 0.5) * inv - 0.5;
        float v = (float(j) + 0.5) * inv - 0.5;
        vec3 origin = faceCentre + t1 * u + t2 * v + n * 1e-2;
        if (!shapesOcclude(origin, uSunDir, SHADOW_RANGE)) lit += 1.0;
    }

    return lit * (inv * inv);
}

// Classic voxel corner occlusion: at a face corner, count the two edge neighbours
// and the diagonal neighbour in the air cell just off the face. Two solid edges
// fully occlude the corner.
float cornerAO(ivec3 air, ivec3 a1, ivec3 a2)
{
    float s1 = solidAt(air + a1) ? 1.0 : 0.0;
    float s2 = solidAt(air + a2) ? 1.0 : 0.0;
    if (s1 > 0.5 && s2 > 0.5) return 0.0;
    float sc = solidAt(air + a1 + a2) ? 1.0 : 0.0;
    return 1.0 - (s1 + s2 + sc) / 3.0;
}

// Ambient occlusion for a voxel face, in [0,1] (1 = open). Bilinearly interpolates
// the four corner values by the hit position across the face, so edges round off.
float faceAO(ivec3 hv, vec3 n, vec3 hitP)
{
    ivec3 ni = ivec3(round(n));
    ivec3 t1, t2;
    if      (abs(n.x) > 0.5) { t1 = ivec3(0, 1, 0); t2 = ivec3(0, 0, 1); }
    else if (abs(n.y) > 0.5) { t1 = ivec3(1, 0, 0); t2 = ivec3(0, 0, 1); }
    else                     { t1 = ivec3(1, 0, 0); t2 = ivec3(0, 1, 0); }

    ivec3 air = hv + ni;   // empty cell just outside the face
    float a00 = cornerAO(air, -t1, -t2);
    float a10 = cornerAO(air,  t1, -t2);
    float a01 = cornerAO(air, -t1,  t2);
    float a11 = cornerAO(air,  t1,  t2);

    vec3 local = hitP - vec3(hv);
    float fu = clamp(dot(local, vec3(t1)), 0.0, 1.0);
    float fv = clamp(dot(local, vec3(t2)), 0.0, 1.0);
    return mix(mix(a00, a10, fu), mix(a01, a11, fu), fv);
}

// --- Rounded-blob voxels -----------------------------------------------------
// A trilinear occupancy field over the voxel grid; its 0.5 isosurface is a
// smoothed, rounded version of the blocky geometry.
float densityAt(vec3 p)
{
    vec3 q = p - 0.5;                 // voxel centres sit at integer + 0.5
    vec3 b = floor(q);
    vec3 f = q - b;
    ivec3 bi = ivec3(b);

    float c000 = float(solidAt(bi + ivec3(0, 0, 0)));
    float c100 = float(solidAt(bi + ivec3(1, 0, 0)));
    float c010 = float(solidAt(bi + ivec3(0, 1, 0)));
    float c110 = float(solidAt(bi + ivec3(1, 1, 0)));
    float c001 = float(solidAt(bi + ivec3(0, 0, 1)));
    float c101 = float(solidAt(bi + ivec3(1, 0, 1)));
    float c011 = float(solidAt(bi + ivec3(0, 1, 1)));
    float c111 = float(solidAt(bi + ivec3(1, 1, 1)));

    return mix(mix(mix(c000, c100, f.x), mix(c010, c110, f.x), f.y),
               mix(mix(c001, c101, f.x), mix(c011, c111, f.x), f.y), f.z);
}

// Outward surface normal from the density gradient (tetrahedron taps). Density
// rises into the solid, so the outward normal is the negated gradient.
vec3 blobNormal(vec3 p)
{
    const float h = 0.35;
    vec2 k = vec2(1.0, -1.0);
    vec3 g = k.xyy * densityAt(p + k.xyy * h)
           + k.yyx * densityAt(p + k.yyx * h)
           + k.yxy * densityAt(p + k.yxy * h)
           + k.xxx * densityAt(p + k.xxx * h);
    float gl = length(g);
    return gl > 1e-5 ? -g / gl : vec3(0.0, 1.0, 0.0);
}

// Refines a flat DDA hit into the blob isosurface: fixed-step march of the density
// field around the hit, then a linear crossing solve. Returns false (keep the flat
// hit) if no crossing is found.
bool blobRefine(vec3 ro, vec3 rd, float tDda, out float outT, out vec3 outN, out uint outMat, out ivec3 outVox)
{
    const float THRESH = 0.5;
    const float STEP = 0.25;

    float ts = tDda - 1.0;
    float prev = densityAt(ro + rd * ts) - THRESH;

    for (int i = 0; i < 16; i++)
    {
        ts += STEP;
        float d = densityAt(ro + rd * ts) - THRESH;
        if (d > 0.0 && prev <= 0.0)
        {
            float frac = -prev / max(d - prev, 1e-5);
            outT = ts - STEP + frac * STEP;
            vec3 p = ro + rd * outT;
            outN = blobNormal(p);

            ivec3 mv = ivec3(floor(p - outN * 0.5));   // step inside for the material
            if (materialAt(mv) == 0u) mv = ivec3(floor(p));
            outVox = mv;
            outMat = materialAt(mv);
            return outMat != 0u;
        }
        prev = d;
        if (ts > tDda + 1.0) break;
    }
    return false;
}

vec3 snapAxis(vec3 n)
{
    vec3 a = abs(n);
    if (a.x >= a.y && a.x >= a.z) return vec3(sign(n.x), 0.0, 0.0);
    if (a.y >= a.z)               return vec3(0.0, sign(n.y), 0.0);
    return vec3(0.0, 0.0, sign(n.z));
}

void main()
{
    // uJitter is a sub-pixel NDC offset for temporal anti-aliasing (zero when off).
    vec2 ndc = (gl_FragCoord.xy / uResolution) * 2.0 - 1.0 + uJitter;
    float aspect = uResolution.x / uResolution.y;

    vec3 rd = normalize(uCamForward
                      + uCamRight * (ndc.x * uTanHalfFov * aspect)
                      + uCamUp    * (ndc.y * uTanHalfFov));

    vec3 color;
    Hit hit;

    bool hitWorld = trace(uCamPos, rd, 1e6, hit);
    float worldT = hitWorld ? hit.t : 1e30;

    if (hitWorld)
    {
        // Round the blocky hit into the smooth blob isosurface (near field only --
        // it is costly and invisible far off). A failed refine keeps the flat hit.
        if (uBlob != 0 && detailAt(hit.t) > 0.4)
        {
            float bt; vec3 bn; uint bm; ivec3 bv;
            if (blobRefine(uCamPos, rd, hit.t, bt, bn, bm, bv))
            {
                hit.t = bt; hit.normal = bn; hit.material = bm; hit.voxel = bv;
                worldT = bt;
            }
        }

        // Smooth normal drives diffuse/ambient; face-based effects (voxel-quantised
        // shadow, AO, point light) need an axis-aligned normal, so snap it when the
        // hit normal is smooth (blobs or spheres).
        vec3 shadeN = hit.normal;
        vec3 faceN  = (uBlob != 0 || uSphere != 0) ? snapAxis(hit.normal) : hit.normal;

        vec3 albedo = surfaceColor(hit.material, hit.voxel, detailAt(hit.t));

        float ndl = max(dot(shadeN, uSunDir), 0.0);
        // One shadow value for the whole voxel face, quantised to a coverage ratio.
        // Cached world self-shadow times the uncached shape-cast factor.
        float shadow = 1.0;
        if (uShadows != 0 && ndl > 0.0)
            shadow = faceShadow(hit.voxel, faceN) * shapeSunFactor(hit.voxel, faceN);

        // Ambient occlusion darkens crevices. Geometry-based, so it is independent of
        // the sun and stacks under the hard shadow. Skipped far off (invisible there,
        // and mip hits have no fine neighbourhood).
        float ao = 1.0;
        if (uAo != 0 && detailAt(hit.t) > 0.01)
            ao = faceAO(hit.voxel, faceN, uCamPos + rd * hit.t);
        float aoFactor = 0.35 + 0.65 * ao;   // floor so corners are not pitch black

        // Sky-dominant ambient plus a weak bounce off the ground.
        vec3 ambient = mix(vec3(0.30, 0.34, 0.42), vec3(0.22, 0.20, 0.16), shadeN.y * -0.5 + 0.5);
        vec3 lit = albedo * (ambient * aoFactor + vec3(1.0, 0.95, 0.85) * ndl * shadow * 1.15);

        // Add the orbiting point light on top of the sun/ambient.
        lit += albedo * pointLight(hit.voxel, faceN);

        float fog = 1.0 - exp(-hit.t * FOG_DENSITY);
        color = mix(lit, skyColor(rd), fog);
    }
    else
    {
        color = skyColor(rd);
    }

    // Live spinning shapes, voxelised onto the world grid and depth-composited with
    // the terrain. Shaded like a world voxel; receive shadows from the world and
    // from other shapes (and their own blocky self-shadowing).
    float shapeT; vec3 shapeN; ivec3 shapeV; vec3 shapeCol;
    if (uShapeCount > 0 && traceShapes(uCamPos, rd, worldT, shapeT, shapeN, shapeV, shapeCol))
    {
        vec3 albedo = shapeCol * mix(1.0, 0.90 + 0.20 * hashCell(shapeV), detailAt(shapeT));
        float ndl = max(dot(shapeN, uSunDir), 0.0);
        float shadow = 1.0;
        if (uShadows != 0 && ndl > 0.0)                // uncached: shapes move every frame
            shadow = voxelFaceLight(shapeV, shapeN) * shapeSunFactor(shapeV, shapeN);

        vec3 ambient = mix(vec3(0.30, 0.34, 0.42), vec3(0.22, 0.20, 0.16), shapeN.y * -0.5 + 0.5);
        vec3 lit = albedo * (ambient + vec3(1.0, 0.95, 0.85) * ndl * shadow * 1.15);
        lit += albedo * pointLight(shapeV, shapeN);

        float fog = 1.0 - exp(-shapeT * FOG_DENSITY);
        color = mix(lit, skyColor(rd), fog);
        worldT = shapeT;   // let the light-source voxel below depth-test against shapes
    }

    // Draw the light source as the single emissive voxel it occupies, when it is
    // nearer than the world surface (so terrain in front still occludes it). The
    // source is not lit or shadowed by anything.
    vec3 boxLo = floor(uPointPos);
    float boxT;
    vec3 boxN;
    if (uPointOn != 0 && rayBox(uCamPos, rd, boxLo, boxLo + 1.0, boxT, boxN) && boxT < worldT)
    {
        // Slight per-face brightness so the cube reads as a cube, not a flat blob.
        vec3 emissive = uPointColor * (1.15 + 0.35 * (boxN.y * 0.5 + 0.5));

        float fog = 1.0 - exp(-boxT * FOG_DENSITY);
        color = mix(emissive, skyColor(rd), fog);
    }

    // Approximate sRGB output; the default framebuffer here is not sRGB-encoded.
    // Alpha carries the scene depth (voxel units, -1 for sky) for TAA reprojection.
    // The crosshair is drawn later by the present pass so TAA does not smear it.
    float depth = (worldT > 1e29) ? -1.0 : worldT;
    FragColor = vec4(pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)), depth);
}
