#version 450

// IsoRim 3D view: vertex stage of the IsoRimSolidTex and IsoRimLitMarkTex defs. The game's
// SceneDefault.vert.glsl unchanged, plus an instanced variant (INSTANCED, compiled by the engine for
// DrawMeshInstanced: the same textured part of many doors, animals or people at once, PartBatches),
// which takes each copy's matrix from _InstanceTransforms (binding 4, also declared in
// IsoRimSolid.frag.glsl, where the engine reads the material layout from).

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 1.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

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

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
#endif
    gl_Position = uVertex.ViewProj * world;

    vPosLocal = inPosition;
    vColor = inColor;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    vNormal = inNormal;
}
