using RimWorld;
using UnityEngine;

namespace IsoRim
{
    // How much wider (x) and deeper (z) a figure's body is drawn for each of the game's body types, on top of
    // the build's own model (person_<build>_*), so the five types read as differently as the game's sprites:
    // the models alone make Fat and Hulk far narrower than the game draws them, and Female and Thin about Male.
    // Measured from the game's sprites (3D off, shirt and trousers, widest part below the head), against Male:
    //   facing south  Female 0.95  Fat 1.54  Hulk 1.47  Thin 0.78
    //   from the side Female 1.12  Fat 1.69  Hulk 1.35  Thin ~0.9
    // and the models' widest part (shirt or trousers): Female 0.86, Fat 1.20, Hulk 1.36, Thin 0.82 (depth 0.97,
    // 1.52, 1.21, 0.83). Everything worn on the body takes the same scale, so clothes still fit; the height (y)
    // stays 1, as the game puts the head at the same height on every body type (BodyTypeDef.headOffset).
    // Child and baby bodies (drawn with the thin and fat models) keep their models as they are.
    internal static class BodyScales
    {
        private static readonly Vector3 Female = new Vector3(1.10f, 1f, 1.15f);
        private static readonly Vector3 Thin = new Vector3(0.95f, 1f, 1.10f);
        private static readonly Vector3 Fat = new Vector3(1.28f, 1f, 1.10f);
        private static readonly Vector3 Hulk = new Vector3(1.12f, 1f, 1.12f);

        public static Vector3 For(BodyTypeDef bodyType)
        {
            if (bodyType == BodyTypeDefOf.Female)
            {
                return Female;
            }
            if (bodyType == BodyTypeDefOf.Thin)
            {
                return Thin;
            }
            if (bodyType == BodyTypeDefOf.Fat)
            {
                return Fat;
            }
            if (bodyType == BodyTypeDefOf.Hulk)
            {
                return Hulk;
            }
            return Vector3.one;
        }
    }
}
