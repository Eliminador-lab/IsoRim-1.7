#version 450

// IsoRim 3D view: vertex stage of the IsoRimSolid, IsoRimLitMark and IsoRimOutline defs. The game's
// SceneDefault.vert.glsl (same inputs and outputs) plus two adjustments that depend on where the
// camera looks from, so the baked meshes need no rebuild when it turns:
//
// 1. Wall end lines (texture coordinate z = 2 + face 0..3 for south, north, west, east). At an
//    outside corner both faces draw a line up the corner, meeting in one seamless line; the inner
//    edge of each (stored on the corner) moves into its face by its share of the full width, given
//    in uv.xy: half each when both faces are turned equally toward the camera, all of it on the one
//    face that shows when the other is turned away (the block's silhouette).
// 2. Outline shells of moving models (_OutlinePush > 0, normal = the direction each corner was
//    pushed out by). Corners facing the camera are pushed straight away from it by _OutlinePush
//    (true cells), which hides the rims where one part's shell tucks into another, without moving
//    anything on screen. Corners facing away stay put: they draw the outline against whatever stands
//    behind, and a push would sink them behind a wall close behind the model.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

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
} uMaterial;

// How the push of an outline corner grows with how much it faces the camera (cosine).
const float PUSH_FROM = -0.25;
const float PUSH_FULL = 0.25;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
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

    float pushBy = uMaterial._OutlinePush;
    if (pushBy > 0.0 && squash > 0.0)
    {
        vec3 h = mat3(push.modelMatrix) * inNormal;
        h.y *= squash;                          // to true space
        float lh = length(h);
        // No direction (a part tucked under another, such as hair under a hat): pushed all round.
        float d = pushBy * (lh > 1e-6 ? smoothstep(PUSH_FROM, PUSH_FULL, dot(h / lh, toCamera)) : 1.0);
        world.xyz -= vec3(toCamera.x, toCamera.y / squash, toCamera.z) * d;
    }

    gl_Position = uVertex.ViewProj * world;

    vPosLocal = inPosition;
    vColor = inColor;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    vNormal = inNormal;
}
