#version 450

// IsoRim: the game's Data\Core\Shaders\Map\TransparentPlant.frag.glsl (1.7.4976) unchanged apart from two global
// members after _SwayHead that IsoRimCard.vert.glsl reads (the material layout is read from this
// stage). Compare with the game's file after a game update.

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0) (unused, kept for layout)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _SwayHead;  // @default = 0.0 (consumed by PlantWindSway.vert, kept for layout)
    vec4 _IsoView;    // global, no default: w = Squash (read by IsoRimCard.vert.glsl)
    vec4 _IsoCard;    // global, no default: sin a, cos a (a = pitch - 90), sin yaw, cos yaw
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    outColor = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
}
