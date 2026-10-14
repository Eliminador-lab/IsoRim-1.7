#version 450

// IsoRim 3D view: vertex stage of the IsoRimFog def, the fog of unexplored areas drawn as a lid
// lying on the tops of the rock and walls under it. The game's MapWorldUV.vert.glsl (same inputs
// and outputs; the texture follows the flat x and z, so lifting does not move it) plus the lift:
// each corner of the game's fog mesh (corners, edge middles and centres of the cells) rises to the
// highest top among the cells it touches, read from the map's cell texture (IsoCellMap: one texel
// per cell, red and green = top height in 1/4096 cells), and a little above it. The mesh itself is never changed,
// so the lid follows mining, building and cut-away the same frame the cell texture does.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv; // object-space XZ; fragment divides by the per-shader tiling size

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match IsoRimFog.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    vec4 _IsoView;    // global: w = Squash
    vec4 _IsoCellMap; // global: origin x, origin z, width, height (cells) of the cell texture
    vec4 _IsoLevel;   // global: x = the map's ground height in cells (0 today)
} uMaterial;

layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;

// How high the lid sits above the tops (cells), as the 1.5 lift.
const float Above = 0.012;

float TopAt(vec2 p, ivec2 size)
{
    ivec2 c = ivec2(floor(p - uMaterial._IsoCellMap.xy));
    if (c.x < 0 || c.y < 0 || c.x >= size.x || c.y >= size.y)
    {
        return 0.0;
    }
    // Rows are stored north first (the texture upload flips them).
    vec2 t = texelFetch(_IsoCellTex, ivec2(c.x, size.y - 1 - c.y), 0).rg;
    return (round(t.r * 255.0) * 256.0 + round(t.g * 255.0)) / 4096.0;   // 16-bit height, 1/4096 cell steps
}

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    ivec2 size = ivec2(uMaterial._IsoCellMap.zw);
    float squash = uMaterial._IsoView.w;
    if (squash > 0.0 && size.x > 0)
    {
        // The cells touching this corner: a little to each side of it (a corner touches four,
        // an edge middle two, a cell centre one).
        float top = max(max(TopAt(world.xz + vec2(-0.02, -0.02), size), TopAt(world.xz + vec2(0.02, -0.02), size)),
                        max(TopAt(world.xz + vec2(-0.02, 0.02), size), TopAt(world.xz + vec2(0.02, 0.02), size)));
        world.y += (uMaterial._IsoLevel.x + top + Above) / squash;
    }
    gl_Position = uVertex.ViewProj * world;
    vColor = inColor;
    vUv = inPosition.xz;
}
