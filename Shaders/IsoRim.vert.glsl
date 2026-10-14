#version 450

// IsoRim 3D view: vertex stage of the IsoRimSolid, IsoRimLitMark and IsoRimOutline defs. The game's
// SceneDefault.vert.glsl (same inputs and outputs) plus two adjustments that depend on where the
// camera looks from, so the baked meshes need no rebuild when it turns:
//
// 1. Wall end lines (texture coordinate z = 2 + face 0..3 for south, north, west, east). At an
//    outside corner both faces draw a line up the corner, meeting in one seamless line; the inner
//    edge of each (stored on the corner) moves into its face by its share of the full width, given
//    in uv.xy: half each when both faces are turned equally toward the camera, all of it on the one
//    face that shows when the other is turned away (the block's silhouette). z = 10..73: the same for
//    faces at any of the eight compass directions (gravship hull corner pieces).
// 2. Outline shells of moving models (_OutlinePush > 0): pushed straight away from the camera by
//    _OutlinePush (true cells), which hides the rims where one part's shell tucks into another
//    (hat crown, hair line, waist) without moving anything on screen. Trade-off: a part close in
//    front of a wall loses its outline where the wall is less than that far behind it.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 4) in vec4 inUv1; // @vertex TexCoord1 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

// Instanced draws (DrawMeshInstanced: the same part of many doors, animals or people at once,
// PartBatches): the engine compiles this variant with INSTANCED and fills _InstanceTransforms with one
// matrix per copy. Binding 4 (2 is the glow texture); declared in IsoRimSolid.frag.glsl too, where the
// engine reads the material layout from.
#ifdef INSTANCED
layout(std430, set = 2, binding = 4) readonly buffer _InstanceTransforms
{
    mat4 instanceTransforms[];
};
#endif

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vNormal;
layout(location = 4) out vec3 vPosLocal;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match IsoRimSolid.frag.glsl exactly (the layout is read from the fragment stage).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _LightMode; // @default = 0.0
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    vec4 _IsoSky;     // global, no default: rgb = the game's sky light colour (the LightOverlay _Color)
    vec4 _IsoGlowMap; // global, no default: (origin x, origin z, 1/(W+1), 1/(H+1)) of the glow texture
    float _GlowMode;  // @default = 0.0
    float _MarkPass;  // @default = 0.0     1 on the light-pass materials (IsoRimLitMark*)
    float _OutlinePush; // @default = 0.0   vertex stage only (IsoRim.vert.glsl): moving outlines
    vec4 _IsoCellMap; // global, no default: (origin x, origin z, W, H) of the cell texture (IsoCellMap)
    vec4 _IsoLevel;   // global, no default: x = the map's ground height in cells, y = what is left of a cut wall
} uMaterial;

// ---- Step 7: the cut-away in the vertex stage (C# ShaderCut, Source\ShaderCut.cs). IsoRim's walls, damage,
// trees and roofs layers carry in texture coordinate 1 the cell a vertex belongs to (x + 0.5, z + 0.5) and what may
// happen to it (z, flags); the map's cell texture (IsoCellMap) says, per cell, the full top height (r, g), how far the
// cell's block is cut down (b) and whether its tree is thinned (a & 1) or its roof open (a & 2). _IsoLevel.y = the part
// of a cut wall that is left (cutHeight).
layout(set = 2, binding = 3) uniform sampler2D _IsoCellTex;

const ivec2 CutDirs[9] = ivec2[9](ivec2(0, 0), ivec2(0, -1), ivec2(0, 1), ivec2(-1, 0), ivec2(1, 0),
                                  ivec2(-1, -1), ivec2(1, -1), ivec2(-1, 1), ivec2(1, 1));
const float CutFoot = 0.06;   // cells: below this nothing moves (the dark line along the ground)

vec4 CutCell(ivec2 c)
{
    ivec2 size = ivec2(uMaterial._IsoCellMap.zw);
    c -= ivec2(floor(uMaterial._IsoCellMap.xy + 0.5));
    if (c.x < 0 || c.y < 0 || c.x >= size.x || c.y >= size.y)
    {
        return vec4(0.0);
    }
    return texelFetch(_IsoCellTex, ivec2(c.x, size.y - 1 - c.y), 0);   // rows stored north first
}

float CutFull(vec4 t)
{
    return (round(t.r * 255.0) * 256.0 + round(t.g * 255.0)) / 4096.0;
}

float CutTop(vec4 t, float cut)
{
    return CutFull(t) * (1.0 - (1.0 - uMaterial._IsoLevel.y) * cut);
}

// False: the vertex is hidden (a leaf of a thinned tree, an open roof, a side against a block that is not lower).
// Otherwise a vertex of a cut block above CutFoot is pressed down so the top lands at the cut height (and a textured
// side's v follows, so the pattern is cut off rather than squeezed, as when the wall was baked lower).
bool CutAway(inout vec3 pos, inout vec2 uv)
{
    int flags = int(inUv1.z + 0.5);
    if (flags == 0 || uMaterial._IsoCellMap.z < 1.0)
    {
        return true;
    }
    ivec2 own = ivec2(floor(inUv1.xy));
    vec4 o = CutCell(own);
    int bits = int(round(o.a * 255.0));
    if ((flags & 64) != 0 && (bits & 1) != 0)
        return false;
    if ((flags & 128) != 0 && (bits & 1) == 0)
        return false;
    if ((flags & 256) != 0 && (bits & 2) != 0)
        return false;
    float cut = o.b;
    int second = (flags >> 9) & 15;
    if (second != 0)
    {
        cut = max(cut, CutCell(own + CutDirs[second]).b);
    }
    float full = CutFull(o);
    int cond = (flags >> 1) & 15;
    if (cond != 0)
    {
        vec4 n = CutCell(own + CutDirs[cond]);
        if (CutTop(n, n.b) >= CutTop(o, cut) - 0.01)
            return false;
    }
    float squash = uMaterial._IsoView.w;
    if ((flags & 1) != 0 && cut > 0.0 && full > CutFoot + 0.01 && squash > 0.0)
    {
        float yc = pos.y * squash - uMaterial._IsoLevel.x;
        if (yc > CutFoot)
        {
            float s = max(0.0, (full * uMaterial._IsoLevel.y - CutFoot) / (full - CutFoot));
            float yn = mix(yc, CutFoot + (yc - CutFoot) * s, cut);
            if ((flags & 32) != 0)
                uv.y *= yn / yc;
            pos.y = (yn + uMaterial._IsoLevel.x) / squash;
        }
    }
    return true;
}

void main()
{
    vec3 pos = inPosition;
    vec2 uv = inUv0.xy;
    bool keep = CutAway(pos, uv);
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(pos, 1.0);
#else
    vec4 world = push.modelMatrix * vec4(pos, 1.0);
#endif
    vec3 toCamera = uMaterial._IsoView.xyz;
    float squash = uMaterial._IsoView.w;

    float code = inUv0.z;
    if (code > 1.5 && code < 5.5)
    {
        int f = int(code - 1.5);   // 0 south, 1 north, 2 west, 3 east
        vec2 n = f == 0 ? vec2(0.0, -1.0) : (f == 1 ? vec2(0.0, 1.0) : (f == 2 ? vec2(-1.0, 0.0) : vec2(1.0, 0.0)));
        vec2 along = inUv0.xy;                 // into the face from the corner, full line width long
        float share = 0.5;
        float lv = length(toCamera.xz);
        float la = length(along);
        if (lv > 1e-4 && la > 1e-6)
        {
            vec2 v = toCamera.xz / lv;
            float mine = max(0.0, dot(n, v));
            float other = max(0.0, dot(-along / la, v));   // the block's end face round the corner
            share = mine + other > 1e-4 ? mine / (mine + other) : 0.5;
        }
        world.xz += along * share;
    }
    else if (code > 9.5 && code < 73.5)
    {
        // The same for a corner line between faces at any of the eight compass directions (gravship hull
        // corner pieces, Gravship.cs: a diagonal face meeting a straight one): z = 10 + 8 * this face + the
        // face round the corner, each 0..7 = east, north-east, north, ... counter-clockwise seen from above.
        int k = int(code - 9.5);
        int ka = k / 8;
        vec2 n = vec2(cos(float(ka) * 0.78539816), sin(float(ka) * 0.78539816));
        vec2 o = vec2(cos(float(k - ka * 8) * 0.78539816), sin(float(k - ka * 8) * 0.78539816));
        vec2 along = inUv0.xy;
        float share = 0.5;
        float lv = length(toCamera.xz);
        if (lv > 1e-4)
        {
            vec2 v = toCamera.xz / lv;
            float mine = max(0.0, dot(n, v));
            float other = max(0.0, dot(o, v));
            share = mine + other > 1e-4 ? mine / (mine + other) : 0.5;
        }
        world.xz += along * share;
    }

    float pushBy = uMaterial._OutlinePush;
    if (pushBy > 0.0 && squash > 0.0)
    {
        world.xyz -= vec3(toCamera.x, toCamera.y / squash, toCamera.z) * pushBy;
    }

    gl_Position = keep ? uVertex.ViewProj * world : vec4(2.0, 2.0, 0.5, 1.0);   // hidden: outside the view, all corners alike

    vPosLocal = pos;
    vColor = inColor;
    vUv = uv;
    vWorldPos = world.xyz;
    vNormal = inNormal;
}
