#version 430 core

// Two-level DDA over a brickmap. One primary ray per pixel, plus one shadow ray.
// Cost scales with resolution, not with voxel count -- which is the whole reason
// a billion-voxel world is tractable here.
//
// Buffer layout must match Voxels/VoxelWorld.cs.

out vec4 FragColor;

layout(std430, binding = 0) readonly buffer BrickIndex { uint bricks[]; };
layout(std430, binding = 1) readonly buffer VoxelPool  { uint voxels[]; };

uniform ivec3 uBrickDims;    // grid size in bricks
uniform vec3  uCamPos;       // eye position in VOXEL units
uniform vec3  uCamRight;
uniform vec3  uCamUp;
uniform vec3  uCamForward;
uniform vec2  uResolution;
uniform float uTanHalfFov;
uniform vec3  uSunDir;       // normalised, points toward the sun
uniform int   uShadows;
uniform int   uMaxBrickSteps;

const int   B            = 8;          // voxels per brick edge
const uint  UNIFORM_FLAG = 0x80000000u;
const float FOG_DENSITY  = 0.00006;    // per voxel of distance; light enough to see far terrain
const float SHADOW_RANGE = 2500.0;     // voxels; long enough for terrain to shade itself

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
// patches of dry-vs-lush, smaller tufts inside those, and a per-voxel speckle.
// Without the coarse layers a purely per-voxel hash just reads as TV static.
vec3 grassColor(ivec3 v)
{
    // Note: "patch" is a reserved word in GLSL 4.x (tessellation), hence "meadow".
    float meadow = valueNoise(vec3(v) / 130.0);   // ~6.5 m at 5 cm voxels
    float clump  = valueNoise(vec3(v) / 26.0);    // ~1.3 m
    float speck  = hashCell(v);                   // one voxel

    float t = clamp(meadow * 0.52 + clump * 0.33 + speck * 0.15, 0.0, 1.0);

    const vec3 DRY  = vec3(0.47, 0.50, 0.19);     // sun-bleached yellow-green
    const vec3 MID  = vec3(0.25, 0.47, 0.17);
    const vec3 LUSH = vec3(0.11, 0.30, 0.12);     // deep shaded green

    vec3 c = t < 0.5 ? mix(DRY, MID, t * 2.0) : mix(MID, LUSH, (t - 0.5) * 2.0);

    // Independent value jitter, so neighbouring voxels of similar hue still
    // separate from each other at close range.
    c *= 0.86 + 0.28 * hashCell(v + ivec3(17, 0, 41));

    return c;
}

vec3 surfaceColor(uint m, ivec3 v, vec3 n)
{
    if (m == 1u) return grassColor(v);

    vec3 base;
    if      (m == 2u) base = vec3(0.42, 0.30, 0.18);   // dirt
    else if (m == 3u) base = vec3(0.44, 0.44, 0.47);   // stone
    else if (m == 4u) base = vec3(0.70, 0.68, 0.64);   // concrete
    else if (m == 5u) base = vec3(0.83, 0.42, 0.14);   // rust
    else              base = vec3(1.0, 0.0, 1.0);      // unknown material

    // Mild grain, enough to keep flat areas from reading as untextured plastic.
    return base * (0.90 + 0.20 * hashCell(v));
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

            // Partial brick -- run a voxel DDA confined to its 8^3 interior.
            uint slot = entry - 1u;
            vec3  base = vec3(bc * B);
            vec3  p    = ro + rd * (t + 1e-3);
            ivec3 lv   = clamp(ivec3(floor(p - base)), ivec3(0), ivec3(B - 1));

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
                    hit.t = tv;
                    hit.normal = nv;
                    hit.material = m;
                    hit.voxel = bc * B + lv;
                    return true;
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

void main()
{
    vec2 ndc = (gl_FragCoord.xy / uResolution) * 2.0 - 1.0;
    float aspect = uResolution.x / uResolution.y;

    vec3 rd = normalize(uCamForward
                      + uCamRight * (ndc.x * uTanHalfFov * aspect)
                      + uCamUp    * (ndc.y * uTanHalfFov));

    vec3 color;
    Hit hit;

    if (trace(uCamPos, rd, 1e6, hit))
    {
        vec3 albedo = surfaceColor(hit.material, hit.voxel, hit.normal);

        float ndl = max(dot(hit.normal, uSunDir), 0.0);
        float shadow = 1.0;
        if (uShadows != 0 && ndl > 0.0)
        {
            // Step off the surface by half a voxel so the ray doesn't re-hit its origin.
            vec3 origin = uCamPos + rd * hit.t + hit.normal * 0.5;
            Hit blocker;
            if (trace(origin, uSunDir, SHADOW_RANGE, blocker)) shadow = 0.0;
        }

        // Sky-dominant ambient plus a weak bounce off the ground.
        vec3 ambient = mix(vec3(0.30, 0.34, 0.42), vec3(0.22, 0.20, 0.16), hit.normal.y * -0.5 + 0.5);
        vec3 lit = albedo * (ambient + vec3(1.0, 0.95, 0.85) * ndl * shadow * 1.15);

        float fog = 1.0 - exp(-hit.t * FOG_DENSITY);
        color = mix(lit, skyColor(rd), fog);
    }
    else
    {
        color = skyColor(rd);
    }

    // Crosshair, drawn straight into the frame -- no UI pass in this demo.
    vec2 d = abs(gl_FragCoord.xy - uResolution * 0.5);
    if ((d.x < 1.0 && d.y < 9.0) || (d.y < 1.0 && d.x < 9.0))
        color = mix(color, vec3(1.0), 0.75);

    // Approximate sRGB output; the default framebuffer here is not sRGB-encoded.
    FragColor = vec4(pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
}
