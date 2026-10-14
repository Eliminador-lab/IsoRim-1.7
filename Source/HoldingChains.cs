using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The restraint chains of a holding platform drawn with its model (ModelDef.chainAnchors).
    //
    // The game keeps the chains itself (Building_HoldingPlatform.DrawChains): none until it has
    // ticked with an entity on it; then, for an entity with points of its own (most entities),
    // one chain from each of the platform's four restraint points to the matching point on the
    // entity, pulled out over its first 20 ticks; for one without, two chains crossing the
    // platform corner to corner. The same chains are drawn here as 3D links: from the model's eye
    // ring nearest the game's end point on the platform, to the entity lying on the deck
    // (surfaceHeight), or across the entity from ring to ring. An empty platform keeps the game's
    // loose chains, raised onto the deck with the rest of its per-frame drawing.
    public static class HoldingChains
    {
        private const float LinkLength = 0.11f;
        private const float LinkWidth = 0.075f;
        private const float LinkThick = 0.036f;
        private const float EntityRise = 0.04f;   // where a chain meets the entity, over the deck
        private const float AcrossRise = 0.16f;   // a chain crossing an entity with no points: over it, over the deck
        private static readonly Color Steel = new Color(0.74f, 0.75f, 0.77f);

        private static FieldInfo chainsField;
        private static MethodInfo targetPointsGetter;
        private static bool reflected;
        private static Model link;

        // Draws the chains of an occupied platform. Returns false if it cannot (the game draws its own).
        public static bool Draw(Building_HoldingPlatform platform, ModelEntry entry)
        {
            if (!reflected)
            {
                reflected = true;
                chainsField = AccessTools.Field(typeof(Building_HoldingPlatform), "chains");
                targetPointsGetter = AccessTools.PropertyGetter(typeof(Building_HoldingPlatform), "TargetPawnAttachPoints");
                Vector3 h = new Vector3(0.5f, 0.5f, 0.5f);
                link = ObjLoader.Box(-h, h);
            }
            List<Vector3> anchors = entry.def.chainAnchors;
            if (chainsField == null || targetPointsGetter == null || link == null || anchors == null || anchors.Count == 0)
            {
                return false;
            }
            // As the game: no chains until it has built them; then built afresh each frame.
            if (!(chainsField.GetValue(platform) is List<Building_HoldingPlatform.Chain> chains))
            {
                return true;
            }
            bool toEntity = targetPointsGetter.Invoke(platform, null) != null;
            chains = toEntity ? platform.BuildTargetPointMapping() : platform.DefaultPointMapping;
            chainsField.SetValue(platform, chains);

            float yaw = Glass.YawOf(platform, entry);
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 fit = entry.Fit(platform.def) * entry.def.scale;
            Vector3 centre = platform.TrueCenter();
            List<Vector3> rings = new List<Vector3>(anchors.Count);
            for (int i = 0; i < anchors.Count; i++)
            {
                Vector3 local = turn * Vector3.Scale(anchors[i] + entry.def.offset, fit);
                rings.Add(new Vector3(centre.x + local.x, local.y, centre.z + local.z));
            }
            float deck = Glass.Floor(entry) * fit.y;
            string swatch = "Metal";
            for (int i = 0; i < chains.Count; i++)
            {
                Building_HoldingPlatform.Chain chain = chains[i];
                Vector3 a = Nearest(rings, chain.from);
                if (toEntity)
                {
                    Vector3 b = new Vector3(chain.to.x, deck + EntityRise, chain.to.z);
                    DrawChain(a, b, 0.05f, swatch);
                    if ((b - a).sqrMagnitude > 0.01f)
                    {
                        // The cuff on the entity.
                        Vector3 d = new Vector3(b.x - a.x, 0f, b.z - a.z);
                        Quaternion q = d.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(d.normalized, Vector3.up) : Quaternion.identity;
                        Matrix4x4 m = Matrix4x4.TRS(b, q, new Vector3(0.11f, 0.05f, 0.08f));
                        DynamicModels.DrawLocal(link, Vector3.zero, 0f, m, Steel * 0.7f, swatch);
                    }
                }
                else
                {
                    // Ring to ring across the platform, over the entity in the middle.
                    Vector3 b = Nearest(rings, chain.to);
                    Vector3 mid = (a + b) * 0.5f;
                    mid.y = deck + AcrossRise;
                    DrawChain(a, mid, 0.02f, swatch);
                    DrawChain(mid, b, 0.02f, swatch);
                }
            }
            return true;
        }

        private static Vector3 Nearest(List<Vector3> rings, Vector3 to)
        {
            Vector3 best = rings[0];
            float bestD = float.MaxValue;
            for (int i = 0; i < rings.Count; i++)
            {
                float dx = rings[i].x - to.x, dz = rings[i].z - to.z;
                float d = dx * dx + dz * dz;
                if (d < bestD)
                {
                    bestD = d;
                    best = rings[i];
                }
            }
            return best;
        }

        // Links from a to b, every other one turned on its side, hanging a little (sag, cells per cell of length).
        private static void DrawChain(Vector3 a, Vector3 b, float sag, string swatch)
        {
            float length = Vector3.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(length / LinkLength));
            Vector3 prev = a;
            for (int k = 1; k <= n; k++)
            {
                float s = (float)k / n;
                Vector3 p = Vector3.Lerp(a, b, s);
                p.y -= 4f * s * (1f - s) * sag * length;
                Vector3 d = p - prev;
                float len = d.magnitude;
                if (len > 1e-4f)
                {
                    Quaternion q = Quaternion.LookRotation(d / len, Vector3.up);
                    if ((k & 1) == 0)
                    {
                        q *= Quaternion.AngleAxis(90f, Vector3.forward);
                    }
                    Matrix4x4 m = Matrix4x4.TRS((p + prev) * 0.5f, q, new Vector3(LinkWidth, LinkThick, len * 1.5f));
                    DynamicModels.DrawLocal(link, Vector3.zero, 0f, m, Steel, swatch);
                }
                prev = p;
            }
        }

        // For the harness: what the platform holds and how its chains run.
        public static string Report(Building_HoldingPlatform platform)
        {
            if (!reflected)
            {
                return "not drawn yet";
            }
            List<Building_HoldingPlatform.Chain> chains = chainsField != null ? chainsField.GetValue(platform) as List<Building_HoldingPlatform.Chain> : null;
            Pawn held = platform.HeldPawn;
            return "holds " + (held != null ? held.def.defName : "nothing") + ", chains " + (chains != null ? chains.Count.ToString() : "none")
                + (held != null ? ", to entity " + (targetPointsGetter.Invoke(platform, null) != null) : "") + ", form " + Forms.For(platform.def).form;
        }
    }
}
