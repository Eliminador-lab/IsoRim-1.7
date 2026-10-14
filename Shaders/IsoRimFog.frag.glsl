#version 450

// IsoRim: the game's Data\Core\Shaders\Map\FogOfWar.frag.glsl (1.7.4976) unchanged apart from the
// global members and the cell texture that IsoRimFog.vert.glsl reads (the material layout is read
// from this stage). Compare with the game's file after a game update.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _IsoView;    // global, no default: w = Squash
    vec4 _IsoCellMap; // global, no default: origin x, origin z, width, height of the cell texture
    vec4 _IsoLevel;   // global, no default: x = the map's ground height in cells
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DetailTex;
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex; // @vertex @default = black

void main()
{
    vec2 uv = vUv / 64.0;
    uv.y = -uv.y;
    vec4 mainCol = texture(_MainTex, uv);
    vec4 detailCol = texture(_DetailTex, uv * 8.0);
    outColor = mainCol * detailCol * vColor * uMaterial._Color;
}
