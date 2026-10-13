#version 450

// IsoRim 3D view: one fragment program for every IsoRim ShaderDef (Solid, SolidTex, Outline, Ghost,
// Blend, Overlay). Render state (blend, depth, cull, bias, queue) comes from the defs in
// Defs\ShaderDefs\IsoRim_Shaders.xml; the vertex stage is vanilla SceneDefault.vert.glsl.
// USE_TEXTURE is defined by the engine for defs with useTexture = true (IsoRimSolidTex).
//
// colour = vertex colour * _Color [* texture, cut out below alpha 0.5] * shade
// shade: _LightMode 0 = unlit (1.0). Modes 1 (model curve) and 2 (wall curve) come with native lighting.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _LightMode; // @default = 0.0
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

float Shade()
{
    return 1.0; // _LightMode 0 (unlit); lit modes are added with native lighting
}

void main()
{
    vec4 color = vColor * uMaterial._Color;
#ifdef USE_TEXTURE
    color *= texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    if (color.a < 0.5)
        discard;
#endif
    color.rgb *= Shade();
    outColor = color;
}
