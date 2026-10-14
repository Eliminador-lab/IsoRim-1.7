#version 450

// IsoRim colour grade: every pixel of the picture drawn so far (scene grab IsoRimGrade, queue 4900)
// is looked up in a LUT strip and mixed in by _Strength. The strip has N slices of N x N side by side
// (N = its height, 32 for the LUT files): blue picks the slice, red runs across it, green up it. The
// lookup is the one of the 1.5/1.6 LUT shader: bilinear inside the two nearest blue slices, then
// mixed by blue.

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _Strength;    // @default = 1.0
    float _TestPattern; // @default = 0.0   harness only: grade a band of known colours instead
} u;

layout(set = 2, binding = 1) uniform sampler2D _LutTex; // @default = white
layout(set = 2, binding = 2) uniform sampler2D _GrabIsoRimGrade;

vec3 Lookup(vec3 c)
{
    float n = float(textureSize(_LutTex, 0).y);
    float slice = c.b * (n - 1.0);
    float s0 = floor(slice);
    float s1 = min(s0 + 1.0, n - 1.0);
    float f = slice - s0;
    float u0 = (c.r * (n - 1.0) + 0.5) / (n * n);
    // The engine keeps a texture's rows top first, so green 0 (the strip's bottom row) is at v = 1.
    float v = 1.0 - (c.g * (n - 1.0) + 0.5) / n;
    vec3 a = textureLod(_LutTex, vec2(u0 + s0 / n, v), 0.0).rgb;
    vec3 b = textureLod(_LutTex, vec2(u0 + s1 / n, v), 0.0).rgb;
    return mix(a, b, f);
}

uint Hash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// Harness only. Cells of 6 x 8 pixels from pixel (16, 16): 256 columns, row 0 greys 0..255, rows 1-3
// red, green and blue ramps, rows 4-11 hashed colours.
vec3 TestPattern(ivec2 p, vec3 c)
{
    ivec2 q = p - ivec2(16, 16);
    if (q.x < 0 || q.y < 0 || q.x >= 256 * 6 || q.y >= 12 * 8)
        return c;
    int i = q.x / 6;
    int row = q.y / 8;
    uvec3 v = uvec3(0u);
    if (row == 0)
        v = uvec3(uint(i));
    else if (row == 1)
        v.r = uint(i);
    else if (row == 2)
        v.g = uint(i);
    else if (row == 3)
        v.b = uint(i);
    else
    {
        uint h = Hash(uint(i + 256 * row));
        v = uvec3(h & 255u, (h >> 8) & 255u, (h >> 16) & 255u);
    }
    return vec3(v) / 255.0;
}

void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    vec3 c = clamp(texelFetch(_GrabIsoRimGrade, p, 0).rgb, 0.0, 1.0);
    if (u._TestPattern > 0.5)
        c = TestPattern(p, c);
    outColor = vec4(mix(c, Lookup(c), u._Strength), 1.0);
}
