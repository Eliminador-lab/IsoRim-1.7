#version 450

// IsoRim 3D view: one fragment program for every IsoRim ShaderDef (Solid, SolidTex, Outline, Ghost,
// Blend, Overlay). Render state (blend, depth, cull, bias, queue) comes from the defs in
// Defs\ShaderDefs\IsoRim_Shaders.xml; the vertex stage is vanilla SceneDefault.vert.glsl.
// USE_TEXTURE is defined by the engine for defs with useTexture = true (IsoRimSolidTex).
//
// colour = vertex colour * _Color [* texture, cut out below alpha 0.5] * shade
// shade (_LightMode): 0 unlit; 1 model curve; 2 wall curve; 3 roof slates (model curve * 1.05, at most 1.1).
// The sun is IsoRim's own (C# R3DMaterials.SunDirAt), pushed every frame as the global _IsoSun.
// The face normal comes from screen derivatives of the world position in true (unsquashed) space,
// turned toward the camera, and snapped to the same 26 direction groups as the C# BinOf.

#define QUANTIZE 1   // 1: snap normals to IsoRim's 26 direction groups (original look); 0: exact face normals

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _LightMode; // @default = 0.0
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

const float DEG = 0.017453292519943295;

vec3 Dir(float azimuthDeg, float elevationDeg)
{
    float az = azimuthDeg * DEG;
    float el = elevationDeg * DEG;
    return vec3(sin(az) * cos(el), sin(el), cos(az) * cos(el));
}

// R3DMaterials.BinOf + MakeBinNormals: straight up, straight down, or one of 8 compass sectors
// at 0, 35 or 62 degrees of elevation.
vec3 BinNormal(vec3 n)
{
    if (n.y >= 0.92)
        return vec3(0.0, 1.0, 0.0);
    if (n.y <= -0.5)
        return vec3(0.0, -1.0, 0.0);
    float elevation = n.y < 0.25 ? 0.0 : (n.y < 0.65 ? 35.0 : 62.0);
    float az = atan(n.x, n.z) / DEG;
    az = az - 360.0 * floor(az / 360.0);
    float sector = mod(floor(az / 45.0 + 0.5), 8.0);
    return Dir(sector * 45.0, elevation);
}

// R3DMaterials.ModelShade: 1 on top by day, 0.5 facing away from the sun.
float ModelShade(vec3 n, vec3 L)
{
    float d = max(0.0, dot(n, L));
    return 0.5 + 0.5 * clamp(d / max(0.2, L.y), 0.0, 1.0);
}

// R3DMaterials.WallShadeFor (curve 1): 1 on top and underneath, 0.52 to 0.80 round the sides.
float WallShade(vec3 n, vec3 L)
{
    if (n.y >= 0.92)
        return 1.0;
    vec2 o = n.xz;
    if (dot(o, o) < 1e-6)
        return 1.0;
    vec2 sunFlat = L.xz;
    sunFlat = dot(sunFlat, sunFlat) < 1e-6 ? vec2(0.0, -1.0) : normalize(sunFlat);
    return mix(0.52, 0.80, (dot(normalize(o), sunFlat) + 1.0) * 0.5);
}

void main()
{
    // Derivatives first, in uniform control flow (before any discard).
    float squash = uMaterial._IsoView.w;
    vec3 p = vec3(vWorldPos.x, vWorldPos.y * squash, vWorldPos.z);
    vec3 n = cross(dFdx(p), dFdy(p));

    vec4 color = vColor * uMaterial._Color;
#ifdef USE_TEXTURE
    color *= texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    if (color.a < 0.5)
        discard;
#endif

    float mode = uMaterial._LightMode;
    float shade = 1.0;
    if (mode > 0.5 && squash > 0.0)
    {
        float len = length(n);
        n = len > 1e-20 ? n / len : vec3(0.0, 1.0, 0.0);
        if (dot(n, uMaterial._IsoView.xyz) < 0.0)
            n = -n;
#if QUANTIZE
        n = BinNormal(n);
#endif
        vec3 L = uMaterial._IsoSun.xyz;
        if (mode < 1.5)
            shade = ModelShade(n, L);
        else if (mode < 2.5)
            shade = WallShade(n, L);
        else
            shade = min(1.1, ModelShade(n, L) * 1.05);
    }
    color.rgb *= shade;
    outColor = color;
}
