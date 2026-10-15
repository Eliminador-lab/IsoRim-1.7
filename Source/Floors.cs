using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Stacked floors on one map, for gameplay addons (IsoRim ProjectZ). IsoRim stays visual: the addon says where its
    // floors are, IsoRim draws them.
    //
    // A layout puts floor k's cells in rows of the same map north of the ground: cell (x, z + zShift_k) is the cell
    // above ground cell (x, z). IsoRim draws everything on floor k moved down by zShift_k rows and raised by the floor's
    // height, so it stands over the ground cell below it: map blocks (sections, which line up 1:1 with the ground's
    // when zShift is a multiple of 17) with one matrix each, things while they are drawn, and IsoRim's own marks
    // (selection brackets, drag outlines, ghosts) by the row they are in. Floors above the one the player works on
    // can be hidden; the pointer works on one floor at a time.
    public interface IFloorLayout
    {
        // Ground rows of the map, or 0 when the map has no floors (then IsoRim draws it as usual).
        int GroundRows(Map map);

        // Floors above the ground (1 = one upper floor).
        int FloorCount(Map map);

        // Floor k (1..FloorCount): its height above the ground in cells, how many rows north of the ground its cells
        // are, and whether it is drawn now.
        bool Floor(Map map, int k, out float height, out int zShift);

        // The floor the pointer works on (0 = the ground).
        int PointerFloor(Map map);

        // An extra true-space offset for one moving thing on top of its cell's (a pawn on a stair). Zero for most.
        Vector3 ThingOffset(Thing t);
    }

    // Hooks for gameplay addons. Everything is off (null) by default and then costs nothing.
    public static class Api
    {
        private static IFloorLayout floors;

        public static IFloorLayout Floors
        {
            get { return floors; }
            set
            {
                floors = value;
                IsoRim.Floors.Registered();
            }
        }
    }

    public static class Floors
    {
        private const int MaxFloors = 8;

        // True while the current map has floors and the 3D view draws (checked first by every hook).
        public static bool On;

        private static Map frameMap;
        private static int frameNo = -1;
        private static int groundRows;
        private static int count;
        private static int pointer;
        private static readonly float[] heights = new float[MaxFloors + 1];
        private static readonly int[] shifts = new int[MaxFloors + 1];
        private static readonly bool[] shown = new bool[MaxFloors + 1];
        private static readonly Matrix4x4[] matrices = new Matrix4x4[MaxFloors + 1];

        private static bool patched;

        internal static void Registered()
        {
            if (Api.Floors != null && Patches.Ready)
            {
                ApplyPatches();
            }
        }

        // From Patches.ApplyAll, and later if a layout registers after that.
        internal static void ApplyPatches()
        {
            if (patched || Api.Floors == null)
            {
                return;
            }
            patched = true;
            Harmony h = new Harmony("isorim.floors");
            try
            {
                h.Patch(AccessTools.Method(typeof(Section), nameof(Section.DrawSection)),
                    prefix: new HarmonyMethod(typeof(Floors), nameof(DrawSection_Prefix)), finalizer: new HarmonyMethod(typeof(Floors), nameof(Section_Finalizer)));
                h.Patch(AccessTools.Method(typeof(Section), nameof(Section.DrawDynamicSections)),
                    prefix: new HarmonyMethod(typeof(Floors), nameof(DrawDynamicSections_Prefix)), finalizer: new HarmonyMethod(typeof(Floors), nameof(Section_Finalizer)));
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DynamicDrawPhase)),
                    prefix: new HarmonyMethod(typeof(Floors), nameof(ThingPhase_Prefix)), finalizer: new HarmonyMethod(typeof(Floors), nameof(ThingPhase_Finalizer)));
                ModLog.Note("[IsoRim] Floor layout registered: stacked floors are drawn.");
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not hook the drawing of stacked floors: " + e);
            }
        }

        // Works out this frame's floors for the current map (once a frame; cheap when the map has none).
        public static void BeginFrame()
        {
            IFloorLayout layout = Api.Floors;
            Map map = Find.CurrentMap;
            On = false;
            if (layout == null || map == null || !View3D.Ready)
            {
                return;
            }
            Refresh(layout, map);
            On = count > 0;
        }

        private static void Refresh(IFloorLayout layout, Map map)
        {
            if (frameNo == Time.frameCount && ReferenceEquals(frameMap, map))
            {
                return;
            }
            frameNo = Time.frameCount;
            frameMap = map;
            count = 0;
            try
            {
                groundRows = layout.GroundRows(map);
                if (groundRows <= 0)
                {
                    return;
                }
                int n = Mathf.Min(layout.FloorCount(map), MaxFloors);
                float s = View3D.Squash;
                matrices[0] = Matrix4x4.identity;
                shown[0] = true;
                for (int k = 1; k <= n; k++)
                {
                    float h;
                    int z;
                    shown[k] = layout.Floor(map, k, out h, out z);
                    heights[k] = h;
                    shifts[k] = z;
                    matrices[k] = Matrix4x4.Translate(new Vector3(0f, h / s, -z));
                }
                pointer = Mathf.Clamp(layout.PointerFloor(map), 0, n);
                count = n;
            }
            catch (Exception e)
            {
                count = 0;
                Log.ErrorOnce("[IsoRim] The floor layout failed: " + e, 93611401);
            }
        }

        // The floor of a row: 0 ground, k, or -1 for rows between floors.
        public static int FloorOfRow(int z)
        {
            if (z < groundRows)
            {
                return 0;
            }
            for (int k = 1; k <= count; k++)
            {
                if (z >= shifts[k] && z < shifts[k] + groundRows)
                {
                    return k;
                }
            }
            return -1;
        }

        // Drawn-space matrix for what stands in a row; false when that floor is hidden now (or the row is between floors).
        public static bool RowMatrix(int z, out Matrix4x4 m)
        {
            int k = FloorOfRow(z);
            if (k <= 0)
            {
                m = Matrix4x4.identity;
                return k == 0;
            }
            m = matrices[k];
            return shown[k];
        }

        // True-space offset of a row (height in y), zero on the ground.
        public static Vector3 RowOffset(int z)
        {
            int k = FloorOfRow(z);
            return k > 0 ? new Vector3(0f, heights[k], -shifts[k]) : Vector3.zero;
        }

        public static float RowHeight(int z)
        {
            int k = FloorOfRow(z);
            return k > 0 ? heights[k] : 0f;
        }

        // A drawn-space matrix moved onto the floor of the row its translation is in (IsoRim's own marks: brackets,
        // drag outlines, ghosts, the designator bracket).
        public static Matrix4x4 ByZ(Matrix4x4 m)
        {
            if (!On)
            {
                return m;
            }
            int z = Mathf.FloorToInt(m.m23);
            if (z < groundRows)
            {
                return m;
            }
            Matrix4x4 f;
            RowMatrix(z, out f);
            return f * m;
        }

        // The whole drawn-space offset of a thing: its cell's floor plus the layout's extra offset. False = hidden.
        public static bool ThingMatrix(Thing t, out Matrix4x4 m)
        {
            bool visible = RowMatrix(t.Position.z, out m);
            Vector3 extra;
            try
            {
                extra = Api.Floors.ThingOffset(t);
            }
            catch (Exception e)
            {
                extra = Vector3.zero;
                Log.ErrorOnce("[IsoRim] The floor layout's thing offset failed: " + e, 93611402);
            }
            if (extra != Vector3.zero)
            {
                m = Matrix4x4.Translate(new Vector3(extra.x, extra.y / View3D.Squash, extra.z)) * m;
            }
            return visible;
        }

        // A drawn-space matrix moved onto the floor of the thing being drawn now (IsoRim draws that bypass DrawHook).
        public static Matrix4x4 InScope(Matrix4x4 m)
        {
            return DrawHook.floor ? DrawHook.floorMatrix * m : m;
        }

        // Drawn-space offset of a thing (culling boxes, name labels); zero without floors.
        public static Vector3 DrawnOffset(Thing t)
        {
            if (!On || t == null || !t.Spawned)
            {
                return Vector3.zero;
            }
            Vector3 o = ThingOffset(t);
            return new Vector3(o.x, o.y / View3D.Squash, o.z);
        }

        // True-space offset of a thing (for culling boxes and labels).
        public static Vector3 ThingOffset(Thing t)
        {
            Vector3 o = RowOffset(t.Position.z);
            try
            {
                o += Api.Floors.ThingOffset(t);
            }
            catch (Exception)
            {
            }
            return o;
        }

        // ---- the pointer

        // Height (cells) of the floor the pointer works on and how far its rows are from the ground's.
        public static void PointerPlane(out float height, out int zShift)
        {
            if (!On || pointer <= 0)
            {
                height = 0f;
                zShift = 0;
                return;
            }
            height = heights[pointer];
            zShift = shifts[pointer];
        }

        // The map position under a screen pixel: on the ground, or with floors on the floor the pointer works on.
        public static Vector3 ScreenToMap(Vector2 px)
        {
            if (!On || pointer <= 0)
            {
                return View3D.ScreenToGround(px);
            }
            Vector3 p = View3D.ScreenToHeight(px, heights[pointer]);
            return new Vector3(p.x, 0f, p.z + shifts[pointer]);
        }

        // ---- the view rectangle: the ground's, and the same ground seen raised on each shown floor

        public static CellRect WidenView(CellRect ground)
        {
            if (!On)
            {
                return ground;
            }
            CellRect r = ground;
            float tan = Mathf.Max(0.2f, Mathf.Tan(View3D.Pitch * Mathf.Deg2Rad));
            for (int k = 1; k <= count; k++)
            {
                if (!shown[k])
                {
                    continue;
                }
                int pad = Mathf.CeilToInt(heights[k] / tan) + 2;
                CellRect f = ground.ExpandedBy(pad);
                f.minZ = Mathf.Max(f.minZ, 0) + shifts[k];
                f.maxZ = Mathf.Min(f.maxZ, groundRows - 1) + shifts[k];
                if (f.maxZ >= f.minZ)
                {
                    r = r.Encapsulate(f);
                }
            }
            return r;
        }

        // ---- sections

        // Set while a section on an upper floor is drawn: the matrix its layers are drawn with.
        public static bool sectionLifted;
        public static Matrix4x4 sectionMatrix = Matrix4x4.identity;

        public static bool DrawSection_Prefix(Section __instance)
        {
            sectionLifted = false;
            if (!On)
            {
                return true;
            }
            Matrix4x4 m;
            bool visible = RowMatrix(__instance.botLeft.z, out m);
            if (!visible)
            {
                return false;
            }
            if (__instance.botLeft.z >= groundRows)
            {
                sectionLifted = true;
                sectionMatrix = m;
            }
            return true;
        }

        public static bool DrawDynamicSections_Prefix(Section __instance)
        {
            return DrawSection_Prefix(__instance);
        }

        public static void Section_Finalizer()
        {
            sectionLifted = false;
        }

        // Cull.SectionOverlaps: false for a section on a hidden floor (or between floors); otherwise true, with the
        // drawn-space shift of its box (zero on the ground and without floors).
        public static bool SectionDrawShift(Section section, out Vector3 shift)
        {
            shift = Vector3.zero;
            if (!On || section.botLeft.z < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(section.botLeft.z);
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            shift = new Vector3(0f, heights[k] / View3D.Squash, -shifts[k]);
            return true;
        }

        // A layer of a section on an upper floor (Patches.DrawLayer_Section, only while sectionLifted): drawn here with
        // the floor's matrix. kind as there: 0 normal, 1 lighting overlay, 2 fog, 3 the game's building damage.
        // Always returns false (the game's own drawing is skipped).
        public static bool DrawLayer(SectionLayer layer, int kind)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (kind == 1 || kind == 3 && st.damage3D || !layer.Visible)
            {
                // No lighting overlay on upper floors: the sheet covers the open air between floors too and would
                // darken the ground under it a second time at night. Damage: IsoRim's own marks are on the shapes.
                return false;
            }
            if (kind == 0)
            {
                IsoGlow.DrawLayerMarks(layer, sectionMatrix);
            }
            bool fogLid = kind == 2 && st.fogLid;
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled)
                {
                    Material mat = fogLid && sm.material == MatBases.FogOfWar ? IsoCellMap.FogFor(sm.material) ?? sm.material : sm.material;
                    Graphics.DrawMesh(sm.mesh, sectionMatrix, mat, sm.renderLayer);
                }
            }
            return false;
        }

        // ---- things drawn every frame

        public static bool ThingPhase_Prefix(Thing __instance, DrawPhase phase, out bool __state)
        {
            __state = false;
            if (phase != DrawPhase.Draw || !On || __instance == null)
            {
                return true;
            }
            Matrix4x4 m;
            if (!ThingMatrix(__instance, out m))
            {
                return false;
            }
            if (m == Matrix4x4.identity)
            {
                return true;
            }
            __state = true;
            DrawHook.floor = true;
            DrawHook.floorMatrix = m;
            return true;
        }

        public static void ThingPhase_Finalizer(bool __state)
        {
            if (__state)
            {
                DrawHook.floor = false;
                DrawHook.floorMatrix = Matrix4x4.identity;
            }
        }

        // Sets the floor for what is drawn next for a thing or cell of a map (status icons, designations), saving what
        // was there; nothing happens without floors or off the current map. A hidden floor skips the drawing.
        public static void BeginFor(Thing t, IntVec3 cell, Map map, ref Patches.DrawScope state)
        {
            if (!On || map == null || map != frameMap || t != null && !t.Spawned)
            {
                return;
            }
            Matrix4x4 m;
            bool visible = t != null ? ThingMatrix(t, out m) : RowMatrix(cell.z, out m);
            state.floorChanged = true;
            state.wasFloor = DrawHook.floor;
            state.wasFloorMatrix = DrawHook.floorMatrix;
            state.wasSkip = DrawHook.skip;
            DrawHook.floor = m != Matrix4x4.identity;
            DrawHook.floorMatrix = m;
            if (!visible)
            {
                DrawHook.skip = true;
            }
        }

        // Puts back what BeginFor changed (Patches.Scope_Finalizer).
        public static void EndFor(Patches.DrawScope state)
        {
            if (state.floorChanged)
            {
                DrawHook.floor = state.wasFloor;
                DrawHook.floorMatrix = state.wasFloorMatrix;
                DrawHook.skip = state.wasSkip;
            }
        }
    }
}
