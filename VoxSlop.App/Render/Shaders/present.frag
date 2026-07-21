#version 430 core

// Presents the resolved image to the screen and draws the crosshair on top (kept
// out of the accumulation so TAA cannot smear it).

out vec4 FragColor;

uniform sampler2D uImage;
uniform vec2 uResolution;

void main()
{
    vec2 uv = gl_FragCoord.xy / uResolution;
    vec3 c = texture(uImage, uv).rgb;

    vec2 d = abs(gl_FragCoord.xy - uResolution * 0.5);
    if ((d.x < 1.0 && d.y < 9.0) || (d.y < 1.0 && d.x < 9.0))
        c = mix(c, vec3(1.0), 0.75);

    FragColor = vec4(c, 1.0);
}
