using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A strip on a model where lights run while the building works (ModelDef.workPanel): the
    // bioferrite harvester and the electroharvester, whose sprites have no 3D counterpart for their
    // working look. The lights move with the game's ticks while the building is working, stand
    // still while it is paused, and are dark while it is idle or unpowered.
    public static class WorkPanels
    {
        private const int Lights = 3;
        private const float LightLength = 0.09f;
        private const float Speed = 0.35f;    // cells a second along the strip at normal speed

        private static List<ThingDef> defs;
        private static Model strip, lit, dark;
        private static readonly Dictionary<int, float> phases = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> lastTick = new Dictionary<int, int>();

        // Whether the building is doing its work now: the game's IsWorking, with power, and for a
        // power plant (electroharvester) giving power out.
        public static bool Working(Thing t)
        {
            Building b = t as Building;
            if (b == null || !b.IsWorking())
            {
                return false;
            }
            CompPowerTrader power = b.GetComp<CompPowerTrader>();
            if (power == null)
            {
                return true;
            }
            if (!power.PowerOn)
            {
                return false;
            }
            return !(power is CompPowerPlant) || power.PowerOutput > 0f;
        }

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.models3D || map == null || Find.TickManager == null)
            {
                return;
            }
            try
            {
                if (defs == null)
                {
                    defs = new List<ThingDef>();
                    foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
                    {
                        ModelEntry entry;
                        if (ModelLibrary.TryGet(def, out entry) && entry.HasModel && entry.def.workPanel != null && entry.def.workPanel.Count >= 2)
                        {
                            defs.Add(def);
                        }
                    }
                    Vector3 h = new Vector3(0.5f, 0.5f, 0.5f);
                    strip = ObjLoader.Box(-h, h);
                    lit = ObjLoader.Box(-h, h);
                    lit.materials[0].glow = true;
                    dark = ObjLoader.Box(-h, h);
                }
                if (defs.Count == 0)
                {
                    return;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
                for (int d = 0; d < defs.Count; d++)
                {
                    ThingForm form = Forms.For(defs[d]);
                    if (form.form != Form3D.Model || form.model == null)
                    {
                        continue;
                    }
                    List<Thing> things = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (view.Contains(t.Position) && !t.Fogged())
                        {
                            Draw(t, form.model);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing work panels failed: " + e, 93611271);
            }
        }

        private static void Draw(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            int now = Find.TickManager.TicksGame;
            int was;
            float phase;
            phases.TryGetValue(t.thingIDNumber, out phase);
            if (!lastTick.TryGetValue(t.thingIDNumber, out was))
            {
                was = now;
            }
            lastTick[t.thingIDNumber] = now;
            bool working = Working(t);
            Vector3 a = def.workPanel[0], b = def.workPanel[1];
            float length = Vector3.Distance(a, b);
            if (length < 1e-3f)
            {
                return;
            }
            if (working && now > was)
            {
                phase = Mathf.Repeat(phase + Mathf.Min(now - was, 60) / 60f * Speed / length, 1f);
                phases[t.thingIDNumber] = phase;
            }

            float yaw = Glass.YawOf(t, entry);
            Vector3 at = t.TrueCenter();
            Vector3 pos = new Vector3(at.x, 0f, at.z);
            Vector3 fit = entry.Fit(t.def) * def.scale;
            Matrix4x4 place = Matrix4x4.Translate(def.offset) * Matrix4x4.Scale(fit);
            Vector3 dir = (b - a) / length;
            Quaternion along = Quaternion.LookRotation(dir, Vector3.up);
            float w = def.workPanelWidth;

            // The strip, dark, a little under the lights.
            Matrix4x4 m = place * Matrix4x4.TRS((a + b) * 0.5f, along, new Vector3(w * 1.3f, 0.012f, length + w * 0.3f));
            DynamicModels.DrawLocal(strip, pos, yaw, m, new Color(0.13f, 0.13f, 0.13f), "Metal");
            Color glow = working ? def.workPanelColor : def.workPanelColor * 0.35f;
            glow.a = 1f;
            for (int k = 0; k < Lights; k++)
            {
                float s = Mathf.Repeat(phase + (float)k / Lights, 1f);
                float lo = Mathf.Max(0f, s * length - LightLength * 0.5f), hi = Mathf.Min(length, s * length + LightLength * 0.5f);
                if (hi - lo < 0.01f)
                {
                    continue;
                }
                Vector3 c = a + dir * ((lo + hi) * 0.5f) + Vector3.up * 0.008f;
                Matrix4x4 lm = place * Matrix4x4.TRS(c, along, new Vector3(w, 0.016f, hi - lo));
                DynamicModels.DrawLocal(working ? lit : dark, pos, yaw, lm, glow, "Metal");
            }
        }

        // For the harness: whether the lights are running, and where.
        public static string Report(Thing t)
        {
            float phase;
            phases.TryGetValue(t.thingIDNumber, out phase);
            return (Working(t) ? "working" : "idle") + " phase " + phase.ToString("0.00");
        }
    }

    // The cables CompCableConnection runs from a harvester to the holding platforms it serves. The
    // game lays them flat on the ground to a point of the sprite; with the model they run from the
    // platform over the ground and up to the model's own connectors (ModelDef.cableAnchors), taken
    // in turn.
    public static class Cables
    {
        private const int Segments = 14;
        private const float Thickness = 0.05f;

        private static FieldInfo cablesField, posField;
        private static bool reflected;
        private static Model piece;

        public static bool Draw(CompCableConnection comp, ModelEntry entry)
        {
            if (!reflected)
            {
                reflected = true;
                cablesField = AccessTools.Field(typeof(CompCableConnection), "cables");
                Type cable = AccessTools.Inner(typeof(CompCableConnection), "Cable");
                posField = cable != null ? AccessTools.Field(cable, "pos") : null;
                Vector3 h = new Vector3(0.5f, 0.5f, 0.5f);
                piece = ObjLoader.Box(-h, h);
            }
            IList cables = cablesField != null ? cablesField.GetValue(comp) as IList : null;
            if (cables == null || posField == null || piece == null)
            {
                return false;
            }
            Thing t = comp.parent;
            List<Vector3> anchors = entry.def.cableAnchors;
            float yaw = Glass.YawOf(t, entry);
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 fit = entry.Fit(t.def) * entry.def.scale;
            Vector3 centre = t.TrueCenter();
            Color colour = comp.Props.color;
            for (int i = 0; i < cables.Count; i++)
            {
                Vector3 target = (Vector3)posField.GetValue(cables[i]);
                Vector3 local = anchors[i % anchors.Count] + entry.def.offset;
                Vector3 end = turn * Vector3.Scale(local, fit);
                Vector3 b = new Vector3(centre.x + end.x, end.y, centre.z + end.z);
                // The game runs the cable under the platform's sprite to the held entity; it shows from
                // where it leaves the platform's edge.
                Vector3 a = new Vector3(target.x, 0.02f, target.z);
                Building platform = target.ToIntVec3().GetFirstBuilding(t.Map);
                if (platform != null && platform != t)
                {
                    a = EdgeOf(platform.OccupiedRect(), a, b);
                }
                DrawCable(a, b, colour);
            }
            return true;
        }

        // Where the line from a (inside the rect) toward b leaves the rect, on the floor.
        private static Vector3 EdgeOf(CellRect r, Vector3 a, Vector3 b)
        {
            const float rim = 0.15f;   // on the platform's rim
            float minX = r.minX + rim, maxX = r.maxX + 1 - rim, minZ = r.minZ + rim, maxZ = r.maxZ + 1 - rim;
            Vector3 d = b - a;
            float s = 1f;
            if (d.x > 1e-5f) s = Mathf.Min(s, (maxX - a.x) / d.x);
            if (d.x < -1e-5f) s = Mathf.Min(s, (minX - a.x) / d.x);
            if (d.z > 1e-5f) s = Mathf.Min(s, (maxZ - a.z) / d.z);
            if (d.z < -1e-5f) s = Mathf.Min(s, (minZ - a.z) / d.z);
            s = Mathf.Clamp01(s);
            return new Vector3(a.x + d.x * s, 0.02f, a.z + d.z * s);
        }

        // Along the floor from the platform, then up over the side to the connector.
        private static void DrawCable(Vector3 a, Vector3 b, Color colour)
        {
            Vector3 flat = new Vector3(a.x - b.x, 0f, a.z - b.z);
            Vector3 toward = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            Vector3 p1 = Vector3.Lerp(a, b, 0.6f);
            p1.y = 0.02f;
            Vector3 p2 = b + toward * 0.3f;
            p2.y = b.y * 0.6f;
            Vector3 prev = a;
            for (int k = 1; k <= Segments; k++)
            {
                float s = (float)k / Segments, u = 1f - s;
                Vector3 p = u * u * u * a + 3f * u * u * s * p1 + 3f * u * s * s * p2 + s * s * s * b;
                p.y = Mathf.Max(p.y, 0.02f);
                Vector3 d = p - prev;
                float len = d.magnitude;
                if (len > 1e-4f)
                {
                    Matrix4x4 m = Matrix4x4.TRS((p + prev) * 0.5f, Quaternion.LookRotation(d / len, Vector3.up), new Vector3(Thickness, Thickness, len + Thickness * 0.5f));
                    DynamicModels.DrawLocal(piece, Vector3.zero, 0f, m, colour, "Metal");
                }
                prev = p;
            }
        }

        // For the harness: how many cables the building runs.
        public static int Count(CompCableConnection comp)
        {
            if (!reflected)
            {
                return -1;
            }
            IList cables = cablesField != null ? cablesField.GetValue(comp) as IList : null;
            return cables != null ? cables.Count : -1;
        }
    }
}
