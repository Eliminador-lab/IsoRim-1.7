// RimWorld 1.7 (Waylith) compatibility shims.
// UnityStubs has no BlendMode/CullMode. IsoRim only casts them to int for Material.SetInt
// ("_SrcBlend", "_Cull" ...), which Waylith ignores anyway (render state comes from ShaderDefs).
// Values match Unity's so the code reads the same. Stage 2+ replaces these with ShaderDefs.
namespace UnityEngine.Rendering
{
    public enum BlendMode
    {
        Zero = 0, One = 1, DstColor = 2, SrcColor = 3, OneMinusDstColor = 4, SrcAlpha = 5,
        OneMinusSrcColor = 6, DstAlpha = 7, OneMinusDstAlpha = 8, SrcAlphaSaturate = 9, OneMinusSrcAlpha = 10
    }

    public enum CullMode { Off = 0, Front = 1, Back = 2 }
}
