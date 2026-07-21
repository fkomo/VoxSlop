#version 430 core

// Temporal anti-aliasing resolve. Blends the current jittered frame into the
// reprojected history, with a neighbourhood colour clamp to suppress ghosting.

out vec4 FragColor;

uniform sampler2D uScene;    // this frame: rgb colour, a = scene depth (voxel units, -1 = sky)
uniform sampler2D uHistory;  // previous accumulated colour

uniform vec2  uResolution;
uniform vec2  uJitter;       // same sub-pixel NDC jitter the raymarch used
uniform float uTanHalfFov;
uniform vec3  uCamPos, uCamRight, uCamUp, uCamForward;

// Previous frame's camera, for reprojection.
uniform vec3  uPrevPos, uPrevRight, uPrevUp, uPrevForward;
uniform float uPrevTanHalfFov;

uniform int   uReset;        // 1 = history invalid (first frame / resize)
uniform float uBlend;        // fraction of the current frame mixed in each frame

void main()
{
    vec2 uv = gl_FragCoord.xy / uResolution;
    float aspect = uResolution.x / uResolution.y;

    vec4 scene = texture(uScene, uv);
    vec3 cur = scene.rgb;

    if (uReset != 0)
    {
        FragColor = vec4(cur, scene.a);   // seed depth for next frame's disocclusion test
        return;
    }

    // Reconstruct the world point at the pixel CENTRE (unjittered). Reprojecting the
    // jittered ray instead would offset history sampling by ~half a pixel every frame,
    // which bilinear history sampling turns into a permanent blur.
    vec2 ndc = uv * 2.0 - 1.0;
    vec3 dir = normalize(uCamForward
                       + uCamRight * (ndc.x * uTanHalfFov * aspect)
                       + uCamUp    * (ndc.y * uTanHalfFov));
    float dist = (scene.a < 0.0) ? 1e5 : scene.a;   // sky: reproject as a far point
    vec3 worldPos = uCamPos + dir * dist;

    // Project it through the previous camera to find where it was on screen.
    vec3 v = worldPos - uPrevPos;
    float fz = dot(v, uPrevForward);
    vec3 result = cur;

    if (fz > 1e-4)
    {
        float px = (dot(v, uPrevRight) / fz) / (uPrevTanHalfFov * aspect);
        float py = (dot(v, uPrevUp)    / fz) / uPrevTanHalfFov;
        vec2 prevUv = vec2(px, py) * 0.5 + 0.5;

        if (all(greaterThanEqual(prevUv, vec2(0.0))) && all(lessThanEqual(prevUv, vec2(1.0))))
        {
            // Neighbourhood mean and variance -> a tight colour box to clip history to.
            // Variance clipping rejects stale (ghosting) history far better than a raw
            // min/max box, which is what a moving light or a fast pan reveals.
            vec2 texel = 1.0 / uResolution;
            vec3 m1 = vec3(0.0), m2 = vec3(0.0);
            for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                vec3 c = texture(uScene, uv + vec2(i, j) * texel).rgb;
                m1 += c; m2 += c * c;
            }
            vec3 mean = m1 / 9.0;
            vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
            vec3 lo = mean - 1.25 * sigma;
            vec3 hi = mean + 1.25 * sigma;

            vec4 histSample = texture(uHistory, prevUv);
            vec3 hist = clamp(histSample.rgb, lo, hi);

            // Disocclusion: if the surface reprojected here was at a very different
            // distance than what history holds, the history is suspect. Rather than a
            // hard reset (which leaves a raw, aliased pixel that takes ~10 frames to
            // re-converge, and false-fires along every silhouette), just distrust it:
            // the variance clip above already tames the colour, and a moderate blend
            // lets the pixel settle in a few frames. Genuine disocclusions converge
            // fast this way instead of lingering as aliased edges.
            float blend = clamp(uBlend, 0.0, 1.0);
            bool curSky = scene.a < 0.0;
            bool histSky = histSample.a < 0.0;
            if (curSky != histSky)
            {
                blend = max(blend, 0.4);
            }
            else if (!curSky)
            {
                float expected = length(worldPos - uPrevPos);
                if (abs(expected - histSample.a) > 0.08 * expected + 1.5) blend = max(blend, 0.4);
            }

            result = mix(hist, cur, blend);
        }
    }

    // Carry the scene depth so next frame can do the disocclusion test above.
    FragColor = vec4(result, scene.a);
}
