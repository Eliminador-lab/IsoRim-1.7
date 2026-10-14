#version 450

// IsoRim: a full-screen pass. The mesh is a quad already in clip space (-1..1), so the camera
// (tilted, turned) does not matter. Used by the colour grade.

layout(location = 0) in vec3 inPosition; // @vertex Position required

void main()
{
    gl_Position = vec4(inPosition.xy, 0.5, 1.0);
}
