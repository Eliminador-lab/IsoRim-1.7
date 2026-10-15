using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Bullets, arrows and shells in 3D. The game draws a projectile flat on the ground along its flight, so in the 3D
    // view shots seemed to leave from the shooter's feet. Here it flies from the muzzle (the height the shooter holds
    // its weapon, or a turret's gun) and its height blends along the flight to the height of what it was aimed at
    // (a standing pawn's chest, a lying pawn, a building's middle, the ground). A lobbed shell's arc, which the game
    // draws as a shift up the screen, becomes real height. The shadow stays on the ground. With the 3D view off the
    // game draws as usual.
    public static class Projectiles
    {
        private sealed class Flight
        {
            public float start;   // cells above the ground at the muzzle
            public float end;     // cells above the ground at the target
        }

        private static readonly ConditionalWeakTable<Projectile, Flight> flights = new ConditionalWeakTable<Projectile, Flight>();

        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly Func<Projectile, float> arcHeight = AccessTools.MethodDelegate<Func<Projectile, float>>(AccessTools.PropertyGetter(typeof(Projectile), "ArcHeightFactor"));
        private static readonly Func<Projectile, float> arcFraction = AccessTools.MethodDelegate<Func<Projectile, float>>(AccessTools.PropertyGetter(typeof(Projectile), "DistanceCoveredFractionArc"));
        private static readonly Action<ThingWithComps> postDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
        private static readonly AccessTools.FieldRef<Material> shadowMat = AccessTools.StaticFieldRefAccess<Material>(AccessTools.Field(typeof(Projectile), "shadowMaterial"));

        // Projectile.DrawAt in 3D: the game's drawing redone with the height added. False = drawn here.
        public static bool DrawAt(Projectile p, Vector3 drawLoc)
        {
            if (!View3D.Ready || p == null || p.def?.projectile == null)
            {
                return true;
            }
            float arc = arcHeight(p) * GenMath.InverseParabola(arcFraction(p));
            float h = Height(p, drawLoc) + arc;
            if (p.def.projectile.shadowSize > 0f)
            {
                DrawShadow(p, drawLoc, arc);
            }
            Vector3 at = drawLoc + new Vector3(0f, h / View3D.Squash, 0f);
            Quaternion rotation = p.ExactRotation;
            if (p.def.projectile.spinRate != 0f)
            {
                float period = 60f / p.def.projectile.spinRate;
                rotation = Quaternion.AngleAxis(Find.TickManager.TicksGame % period / period * 360f, Vector3.up);
            }
            if (p.def.projectile.useGraphicClass)
            {
                p.Graphic.Draw(at, p.Rotation, p, rotation.eulerAngles.y);
            }
            else
            {
                Graphics.DrawMesh(MeshPool.GridPlane(p.def.graphicData.drawSize), at, rotation, p.DrawMat, 0);
            }
            postDraw(p);
            return false;
        }

        // Height above the ground (cells) at this point of the flight: muzzle to target, by the share of the way flown.
        public static float Height(Projectile p, Vector3 at)
        {
            Flight f = flights.GetValue(p, Begin);
            Vector3 o = originRef(p);
            Vector3 d = destinationRef(p);
            float total = new Vector2(d.x - o.x, d.z - o.z).magnitude;
            float done = new Vector2(at.x - o.x, at.z - o.z).magnitude;
            float t = total > 0.001f ? Mathf.Clamp01(done / total) : 1f;
            return Mathf.Lerp(f.start, f.end, t);
        }

        private static Flight Begin(Projectile p)
        {
            Flight f = new Flight();
            try
            {
                f.start = MuzzleHeight(p.Launcher);
                f.end = AimHeight(p);
            }
            catch (Exception e)
            {
                f.start = f.end = 0.5f;
                Log.ErrorOnce("[IsoRim] Working out a projectile's height failed: " + e, 93611501);
            }
            return f;
        }

        // How high the shooter fires from, in cells above its feet.
        public static float MuzzleHeight(Thing launcher)
        {
            if (launcher == null)
            {
                return 0.5f;
            }
            if (launcher is Pawn pawn)
            {
                if (pawn.GetPosture() != PawnPosture.Standing)
                {
                    return 0.2f;
                }
                // About where a 3D figure or a standing sprite holds its weapon (People, Patches.HandHeight).
                return Interaction.PickHeight(pawn) * (pawn.RaceProps != null && pawn.RaceProps.Humanlike ? 0.42f : 0.6f);
            }
            if (launcher is Building_Turret turret)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(turret.def, out entry) && entry.HasModel && entry.def.topHeight > 0f)
                {
                    return entry.def.topHeight * entry.Fit(turret.def).y;
                }
                return Mathf.Clamp(Interaction.PickHeight(turret) * 0.75f, 0.3f, 1.5f);
            }
            return Mathf.Clamp(Interaction.PickHeight(launcher) * 0.5f, 0.1f, 1.5f);
        }

        // How high what the shot is aimed at is hit: the thing hit, or for a miss the thing it was meant for.
        public static float AimHeight(Projectile p)
        {
            Thing t = p.usedTarget.Thing;
            if (t == null && p.intendedTarget.HasThing && p.intendedTarget.Cell.InHorDistOf(p.usedTarget.Cell, 4f))
            {
                t = p.intendedTarget.Thing;
            }
            if (t == null)
            {
                return 0.1f;
            }
            if (t is Pawn pawn)
            {
                return pawn.Dead || pawn.GetPosture() != PawnPosture.Standing ? 0.12f : Interaction.PickHeight(pawn) * 0.45f;
            }
            return Mathf.Clamp(Interaction.PickHeight(t) * 0.5f, 0.05f, 1f);
        }

        // The game's ground shadow (shells), at the shadow's place on the ground: the arc is height now, not a shift.
        private static void DrawShadow(Projectile p, Vector3 drawLoc, float arc)
        {
            Material mat = shadowMat();
            if (mat == null)
            {
                return;
            }
            float size = p.def.projectile.shadowSize * Mathf.Lerp(1f, 0.6f, arc);
            Matrix4x4 m = Matrix4x4.TRS(drawLoc + new Vector3(0f, -0.01f, 0f), Quaternion.identity, new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
        }
    }
}
