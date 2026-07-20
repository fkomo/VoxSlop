#version 430 core

// Fullscreen triangle generated from gl_VertexID -- no vertex buffer needed.
// Vertices land at (-1,-1), (3,-1), (-1,3), which covers the viewport exactly once.
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
