#version 430 core

// Presents the resolved image to the screen, optionally through a retro fixed-
// palette + ordered-dither filter, and draws the crosshair on top (kept out of
// the accumulation so TAA cannot smear it, and applied after TAA so the dither
// pattern is not averaged away).

out vec4 FragColor;

uniform sampler2D uImage;
uniform vec2 uResolution;
uniform int  uRetro;   // 1 = quantise to a fixed palette with ordered dithering

// PICO-8 palette.
const vec3 PALETTE[16] = vec3[16](
    vec3(0.000, 0.000, 0.000), vec3(0.114, 0.169, 0.325), vec3(0.494, 0.145, 0.325),
    vec3(0.000, 0.529, 0.318), vec3(0.671, 0.322, 0.212), vec3(0.373, 0.341, 0.310),
    vec3(0.761, 0.765, 0.780), vec3(1.000, 0.945, 0.910), vec3(1.000, 0.000, 0.302),
    vec3(1.000, 0.639, 0.000), vec3(1.000, 0.925, 0.153), vec3(0.000, 0.894, 0.212),
    vec3(0.161, 0.678, 1.000), vec3(0.514, 0.463, 0.612), vec3(1.000, 0.467, 0.659),
    vec3(1.000, 0.800, 0.667));

// 4x4 Bayer ordered-dither threshold in [0,1).
float bayer(ivec2 p)
{
    const int M[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
    return float(M[(p.y & 3) * 4 + (p.x & 3)]) / 16.0;
}

vec3 retro(vec3 c, ivec2 pix)
{
    // Two closest palette entries, then dither between them by their distance ratio.
    int i0 = 0, i1 = 0;
    float d0 = 1e9, d1 = 1e9;
    for (int i = 0; i < 16; i++)
    {
        vec3 dd = c - PALETTE[i];
        float d = dot(dd, dd);
        if (d < d0) { d1 = d0; i1 = i0; d0 = d; i0 = i; }
        else if (d < d1) { d1 = d; i1 = i; }
    }
    d0 = sqrt(d0); d1 = sqrt(d1);
    float mixAmt = d0 / max(d0 + d1, 1e-5);   // 0 = right on the nearest colour
    return (bayer(pix) < mixAmt) ? PALETTE[i1] : PALETTE[i0];
}

void main()
{
    vec2 uv = gl_FragCoord.xy / uResolution;
    vec3 c = texture(uImage, uv).rgb;

    if (uRetro != 0)
        c = retro(c, ivec2(gl_FragCoord.xy));

    vec2 d = abs(gl_FragCoord.xy - uResolution * 0.5);
    if ((d.x < 1.0 && d.y < 9.0) || (d.y < 1.0 && d.x < 9.0))
        c = mix(c, vec3(1.0), 0.75);

    FragColor = vec4(c, 1.0);
}
