#version 450

// IsoRim 3D view: vertex stage of the IsoRimCardPlant and IsoRimCardSnow defs (plants without a 3D
// model, drawn as sprites stood up to face the camera). The mesh is baked once, flat: every corner
// sits on its card's hinge (the middle of the sprite's bottom edge, in the map mesh's squashed
// space) and texture coordinate 1 holds where the corner lay in the flat sprite relative to that
// hinge. Here the card is stood up and turned to face the camera from the global _IsoCard, so a
// tilt or a turn of the camera rebuilds nothing. Same maths as the 1.5 bake (View3D.UprightPoint
// then), then the game's own wind sway (PlantWindSway.vert.glsl) on the stood-up corner.

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 4) in vec4 inUv1;      // @vertex TexCoord1 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) flat out float vFallSeed; // per plant seed from uv z

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// The first members of the block in IsoRimCardPlant.frag.glsl / IsoRimCardSnow.frag.glsl (the
// layout is read from the fragment stage; both start the same).
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    vec4 _MainTex_ST;
    float _SwayHead;
    vec4 _IsoView;    // global: w = Squash
    vec4 _IsoCard;    // global: sin a, cos a (a = pitch - 90 degrees), sin yaw, cos yaw
} uMaterial;

void main()
{
    float sa = uMaterial._IsoCard.x;
    float ca = uMaterial._IsoCard.y;
    float sy = uMaterial._IsoCard.z;
    float cy = uMaterial._IsoCard.w;
    float squash = uMaterial._IsoView.w;

    // Tilted up about the hinge, then turned about the vertical to face the camera.
    vec3 d = inUv1.xyz;
    float y2 = d.y * ca - d.z * sa;
    float z2 = d.y * sa + d.z * ca;
    vec3 card = vec3(inPosition.x + d.x * cy + z2 * sy,
                     inPosition.y + y2 / squash,
                     inPosition.z - d.x * sy + z2 * cy);

    const float Freq = 0.15;
    const float Amp = 0.1;
    float head = uMaterial._SwayHead;
    vec3 windOffset = vec3(sin(head * Freq + card.x * 4.0), 0.0, sin(head * Freq + card.z * 4.0)) * Amp;
    vec3 pos = card + windOffset * inColor.a;

    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(pos, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;
    vFallSeed = inUv0.z;
}
