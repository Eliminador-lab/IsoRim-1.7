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
                h.Patch(AccessTools.Method(typeof(SectionLayer_Terrain), nameof(SectionLayer_Terrain.Regenerate)),
                    postfix: new HarmonyMethod(typeof(Floors), nameof(TerrainRegenerate_Postfix)));
                PatchDrawing(h);
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
            if (RimWorld.Planet.WorldRendererUtility.WorldRendered)
            {
                return;   // the world map is drawn: no map rows to move
            }
            Refresh(layout, map);
            On = count > 0;
            int sig = On ? groundRows * 31 + pointer : -1;
            for (int k = 1; k <= count; k++)
            {
                sig = sig * 31 + (shown[k] ? 1 : 0) * 17 + Mathf.RoundToInt(heights[k] * 100f);
            }
            if (sig != viewSig)
            {
                viewSig = sig;
                ViewChanged();
            }
        }

        private static int viewSig = int.MinValue;

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

        // ---- THE drawing map (design "M1c-2 coordinates"): a point or matrix in the game's world space, whose z may be a
        // row of an upper floor, to where it is drawn: moved down by that floor's rows and up by its height. Drawn
        // space only ever holds ground rows (z < groundRows), so mapping twice changes nothing: every draw can go
        // through it, at the bottom (Graphics, see PatchDrawing), whatever drew it before. False: the row is between
        // floors or on a hidden floor (nothing is drawn there).
        public static bool Drawn(ref Matrix4x4 m)
        {
            if (!On || m.m23 < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(Mathf.FloorToInt(m.m23));
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            m = matrices[k] * m;
            return true;
        }

        public static bool Drawn(ref Vector3 p)
        {
            if (!On || p.z < groundRows)
            {
                return true;
            }
            int k = FloorOfRow(Mathf.FloorToInt(p.z));
            if (k <= 0 || !shown[k])
            {
                return false;
            }
            p = new Vector3(p.x, p.y + heights[k] / View3D.Squash, p.z - shifts[k]);
            return true;
        }

        // The same as a value (culling, lighting lookups): the matrix where it shows (unchanged when hidden).
        public static Matrix4x4 DrawnMatrix(Matrix4x4 m)
        {
            Drawn(ref m);
            return m;
        }

        // Which way from one world point to another as drawn (flat): an aim between floors points at what is seen.
        public static Vector3 FlatDelta(Vector3 from, Vector3 to)
        {
            Drawn(ref from);
            Drawn(ref to);
            Vector3 d = to - from;
            d.y = 0f;
            return d;
        }

        // The layout's extra offset of one thing (a pawn on a stair step), as a drawn-space matrix; identity for most.
        public static Matrix4x4 ExtraMatrix(Thing t)
        {
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
            return extra == Vector3.zero ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(extra.x, extra.y / View3D.Squash, extra.z));
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

        // The floors the pointer works on are the shown ones, from the one viewed (PointerTop) down to the ground. The ray
        // under a screen pixel lands on the first of them, from the top, whose floor is built where it meets that floor's
        // plane: the open air beside a building upstairs lets it through to the floor below, the ground always catches it.
        // Hidden floors above the viewed one are never hit. Everything that turns the pointer into a map position goes
        // through here: UI.UIToMapPosition (MouseCell, MouseMapPosition: orders, float menus, designators, drags, labels,
        // brackets), the 3D picking (Interaction) and the cut-away round the pointer (Dynamic).
        public static int PointerTop
        {
            get { return On ? pointer : 0; }
        }

        // The floor the ray under a screen pixel lands on (0 = the ground).
        public static int PointerFloorAt(Vector2 px)
        {
            if (!On || pointer <= 0 || frameMap == null)
            {
                return 0;
            }
            for (int k = pointer; k >= 1; k--)
            {
                Vector3 p = View3D.ScreenToHeight(px, heights[k]);
                int gz = Mathf.FloorToInt(p.z);
                if (gz < 0 || gz >= groundRows)
                {
                    continue;
                }
                if (HasFloor(frameMap, new IntVec3(Mathf.FloorToInt(p.x), 0, gz + shifts[k])))
                {
                    return k;
                }
            }
            return 0;
        }

        // Height (cells) of floor k and how far its rows are from the ground's (zero for the ground or without floors).
        public static void FloorPlane(int k, out float height, out int zShift)
        {
            if (!On || k <= 0 || k > count)
            {
                height = 0f;
                zShift = 0;
                return;
            }
            height = heights[k];
            zShift = shifts[k];
        }

        // The map position under a screen pixel: on the floor the ray lands on (PointerFloorAt), else on the ground.
        public static Vector3 ScreenToMap(Vector2 px)
        {
            int k = PointerFloorAt(px);
            if (k <= 0)
            {
                return View3D.ScreenToGround(px);
            }
            Vector3 p = View3D.ScreenToHeight(px, heights[k]);
            return new Vector3(p.x, 0f, p.z + shifts[k]);
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
            if (layer is SectionLayer_EdgeShadows || layer.GetType().Name == "SectionLayer_SunShadows")
            {
                // The game's shadows round walls lie flat on the ground beside them; up here that is the open air,
                // where they would hang as a dark sheet across the wall below.
                return false;
            }
            if (kind == 0)
            {
                IsoGlow.DrawLayerMarks(layer, sectionMatrix);
                if (layer is SectionLayer_Terrain)
                {
                    DrawSlab(layer);
                }
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

        // ---- floor slabs: the game's terrain does not write depth, so an upper floor drawn as terrain alone would not
        // hide what is below it. Every drawn cell of an upper floor gets a solid slab (top at the floor, a little thick,
        // sides where the floor ends), coloured like its terrain, lit like IsoRim's models.
        private sealed class Slab
        {
            public Mesh mesh;
            public bool dirty = true;
            public int stamp;
        }

        private const float SlabThickness = 0.12f;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Section, Slab> slabs = new System.Runtime.CompilerServices.ConditionalWeakTable<Section, Slab>();
        private static readonly AccessTools.FieldRef<SectionLayer, Section> layerSection = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        public static void TerrainRegenerate_Postfix(SectionLayer_Terrain __instance)
        {
            Section s = layerSection(__instance);
            Slab slab;
            if (s != null && slabs.TryGetValue(s, out slab))
            {
                slab.dirty = true;
            }
        }

        private static void DrawSlab(SectionLayer layer)
        {
            Section section = layerSection(layer);
            if (section == null || section.botLeft.z < groundRows)
            {
                return;
            }
            Slab slab = slabs.GetValue(section, s => new Slab());
            if (slab.dirty || slab.stamp != View3D.Stamp)
            {
                slab.dirty = false;
                slab.stamp = View3D.Stamp;
                BuildSlab(section, slab);
            }
            Material mat = R3DMaterials.LitModel;
            if (slab.mesh != null && mat != null)
            {
                IsoGlow.DrawLit(slab.mesh, sectionMatrix, mat, 0);
            }
        }

        private static bool Floored(Map map, IntVec3 c, int floor)
        {
            return c.InBounds(map) && FloorOfRow(c.z) == floor && !map.terrainGrid.TerrainAt(c).dontRender;
        }

        private static void BuildSlab(Section section, Slab slab)
        {
            Map map = section.map;
            int floor = FloorOfRow(section.botLeft.z);
            float s = View3D.Squash;
            float top = 0f;
            float bottom = -SlabThickness / s;
            List<Vector3> verts = new List<Vector3>();
            List<Color32> cols = new List<Color32>();
            List<int> tris = new List<int>();
            CellRect rect = section.CellRect;
            float floorH, belowH;
            int unusedShift;
            ColumnFloor(map, section.botLeft, out floorH, out unusedShift);
            ColumnFloor(map, Stacked(map, section.botLeft, -1), out belowH, out unusedShift);
            foreach (IntVec3 c in rect)
            {
                if (!Floored(map, c, floor))
                {
                    continue;
                }
                // The column (design section 5): a wall here standing on a wall below is one wall through both storeys and
                // fills the cell; over a wall below, the slab's edge starts at that wall's top (no face over its face).
                float wallHere, wallBelow;
                bool blockBelow = FullBlock(map, Stacked(map, c, -1), out wallBelow);
                if (blockBelow && FullBlock(map, c, out wallHere))
                {
                    continue;
                }
                float sideBottom = blockBelow ? Mathf.Min(top, Mathf.Max(bottom, (belowH + wallBelow - floorH) / s)) : bottom;
                Color32 col = map.terrainGrid.TerrainAt(c).DrawColor;
                col.a = 255;
                float x0 = c.x, x1 = c.x + 1, z0 = c.z, z1 = c.z + 1;
                Quad(verts, cols, tris, col, new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, top, z0));
                float bottomHere = bottom;
                bottom = sideBottom;
                if (!Floored(map, c + IntVec3.South, floor))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z0), new Vector3(x0, top, z0), new Vector3(x1, top, z0), new Vector3(x1, bottom, z0));
                }
                if (!Floored(map, c + IntVec3.North, floor))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z1), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, bottom, z1));
                }
                if (!Floored(map, c + IntVec3.West, floor))
                {
                    Quad(verts, cols, tris, col, new Vector3(x0, bottom, z0), new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x0, bottom, z1));
                }
                if (!Floored(map, c + IntVec3.East, floor))
                {
                    Quad(verts, cols, tris, col, new Vector3(x1, bottom, z0), new Vector3(x1, top, z0), new Vector3(x1, top, z1), new Vector3(x1, bottom, z1));
                }
                bottom = bottomHere;
            }
            if (verts.Count == 0)
            {
                slab.mesh = null;
                return;
            }
            if (slab.mesh == null)
            {
                slab.mesh = new Mesh();
            }
            else
            {
                slab.mesh.Clear();
            }
            slab.mesh.SetVertices(verts);
            slab.mesh.SetColors(cols);
            slab.mesh.SetTriangles(tris, 0);
            slab.mesh.RecalculateBounds();
        }

        // A quad drawn from both sides (the shader turns each face toward the camera).
        private static void Quad(List<Vector3> verts, List<Color32> cols, List<int> tris, Color32 col, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = verts.Count;
            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            verts.Add(d);
            cols.Add(col);
            cols.Add(col);
            cols.Add(col);
            cols.Add(col);
            tris.Add(i);
            tris.Add(i + 1);
            tris.Add(i + 2);
            tris.Add(i);
            tris.Add(i + 2);
            tris.Add(i + 3);
            tris.Add(i);
            tris.Add(i + 2);
            tris.Add(i + 1);
            tris.Add(i);
            tris.Add(i + 3);
            tris.Add(i + 2);
        }

        // ---- things drawn every frame

        // A thing on a hidden floor is not drawn at all; a pawn on a stair step gets its rise. The floor itself is
        // added at the bottom (Drawn), like everything else.
        public static bool ThingPhase_Prefix(Thing __instance, DrawPhase phase, out bool __state)
        {
            __state = false;
            if (phase != DrawPhase.Draw || !On || __instance == null)
            {
                return true;
            }
            Matrix4x4 unused;
            if (!RowMatrix(__instance.Position.z, out unused))
            {
                return false;
            }
            Matrix4x4 m = ExtraMatrix(__instance);
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

        // ---- the bottom of all drawing (design "M1c-2 coordinates", section 3). On Waylith, UnityEngine.Graphics is managed code:
        // every DrawMesh overload ends in DrawMeshCore or in an overload that queues the draw itself, every
        // DrawMeshInstanced in one of three overloads. Those get Drawn; lines get their two ends mapped; the game's
        // whole-map overlays (CellBoolDrawer) their cells; screen labels the point they are pinned to.
        private static void PatchDrawing(Harmony h)
        {
            HarmonyMethod single = new HarmonyMethod(typeof(Floors), nameof(Single_Prefix));
            HarmonyMethod array = new HarmonyMethod(typeof(Floors), nameof(InstancedArray_Prefix));
            HarmonyMethod list = new HarmonyMethod(typeof(Floors), nameof(InstancedList_Prefix));
            int n = 0;
            foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (!m.IsStatic || m.ContainsGenericParameters)
                {
                    continue;
                }
                System.Reflection.ParameterInfo[] ps = m.GetParameters();
                bool hasMatrix = false, hasArray = false, hasList = false;
                foreach (System.Reflection.ParameterInfo p in ps)
                {
                    hasMatrix |= p.Name == "matrix" && p.ParameterType == typeof(Matrix4x4);
                    hasArray |= p.Name == "matrices" && p.ParameterType == typeof(Matrix4x4[]);
                    hasList |= p.Name == "matrices" && p.ParameterType == typeof(List<Matrix4x4>);
                }
                string calls = Calls(m);
                if ((m.Name == "DrawMesh" || m.Name == "DrawMeshCore") && hasMatrix && calls.Contains("|Enqueue|"))
                {
                    h.Patch(m, prefix: single);
                    n++;
                }
                else if (m.Name == "DrawMeshInstanced" && calls.Contains("|EnqueueInstancedDirect|") && (hasArray || hasList))
                {
                    h.Patch(m, prefix: hasArray ? array : list);
                    n++;
                }
            }
            h.Patch(AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3), typeof(Material), typeof(float) }),
                prefix: new HarmonyMethod(typeof(Floors), nameof(Line_Prefix)));
            h.Patch(AccessTools.Method(typeof(CellBoolDrawer), nameof(CellBoolDrawer.RegenerateMesh)), postfix: new HarmonyMethod(typeof(Floors), nameof(CellBool_Postfix)));
            h.Patch(AccessTools.Method(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate)), postfix: new HarmonyMethod(typeof(Floors), nameof(LightingOverlay_Postfix)));
            h.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) }), prefix: new HarmonyMethod(typeof(Floors), nameof(WorldToScreen_Prefix)));
            h.Patch(AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(Floors), nameof(MapMeshDirty_Postfix)));
            ModLog.Note("[IsoRim] Floors: " + n + " Graphics entry points mapped to the floors.");
        }

        // The names of the methods a method calls, as "|a|b|".
        private static string Calls(System.Reflection.MethodInfo m)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("|");
            try
            {
                foreach (KeyValuePair<System.Reflection.Emit.OpCode, object> ins in PatchProcessor.ReadMethodBody(m))
                {
                    if (ins.Value is System.Reflection.MethodInfo callee)
                    {
                        sb.Append(callee.Name).Append('|');
                    }
                }
            }
            catch (Exception)
            {
            }
            return sb.ToString();
        }

        public static bool Single_Prefix(ref Matrix4x4 matrix)
        {
            return !On || Drawn(ref matrix);
        }

        private static Matrix4x4[] instanceBuffer = new Matrix4x4[256];
        private static readonly List<Matrix4x4> instanceList = new List<Matrix4x4>();

        public static bool InstancedArray_Prefix(ref Matrix4x4[] matrices, ref int count)
        {
            if (!On || matrices == null)
            {
                return true;
            }
            int n = Mathf.Min(count, matrices.Length);
            int i = 0;
            while (i < n && matrices[i].m23 < groundRows)
            {
                i++;
            }
            if (i == n)
            {
                return true;   // all on the ground (the usual case): untouched
            }
            if (instanceBuffer.Length < n)
            {
                instanceBuffer = new Matrix4x4[Mathf.NextPowerOfTwo(n)];
            }
            int kept = 0;
            for (int j = 0; j < n; j++)
            {
                Matrix4x4 m = matrices[j];
                if (Drawn(ref m))
                {
                    instanceBuffer[kept++] = m;
                }
            }
            matrices = instanceBuffer;
            count = kept;
            return kept > 0;
        }

        public static bool InstancedList_Prefix(ref List<Matrix4x4> matrices)
        {
            if (!On || matrices == null || ReferenceEquals(matrices, instanceList))
            {
                return true;
            }
            int i = 0;
            while (i < matrices.Count && matrices[i].m23 < groundRows)
            {
                i++;
            }
            if (i == matrices.Count)
            {
                return true;
            }
            instanceList.Clear();
            for (int j = 0; j < matrices.Count; j++)
            {
                Matrix4x4 m = matrices[j];
                if (Drawn(ref m))
                {
                    instanceList.Add(m);
                }
            }
            matrices = instanceList;
            return instanceList.Count > 0;
        }

        // A line between two points (paths, move orders, links): each end where it shows. Between floors the line
        // climbs, so it is laid along the slope here instead of flat at one end's height.
        public static bool Line_Prefix(ref Vector3 A, ref Vector3 B, Material mat, float lineWidth)
        {
            if (!On)
            {
                return true;
            }
            if (!Drawn(ref A) || !Drawn(ref B))
            {
                return false;
            }
            if (Mathf.Abs(A.y - B.y) < 0.01f)
            {
                return true;
            }
            Vector3 d = A - B;
            if (d.magnitude < 0.01f)
            {
                return false;
            }
            Matrix4x4 matrix = Matrix4x4.TRS((A + B) * 0.5f, Quaternion.LookRotation(d), new Vector3(lineWidth, 1f, d.magnitude));
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0);
            return false;
        }

        // The screen point of a world point on the map camera (labels, damage numbers, arrows): where it shows.
        public static void WorldToScreen_Prefix(Camera __instance, ref Vector3 position)
        {
            if (On && position.z >= groundRows && ReferenceEquals(__instance, Find.Camera))
            {
                Drawn(ref position);
            }
        }

        // The game's lighting overlay of the block holding the ground's top rows also covers the rows between the
        // ground and the first floor (design section 6: the map ends at the ground's edge): there it would darken
        // the empty background past the north edge. Its cells there are left out.
        public static void LightingOverlay_Postfix(SectionLayer_LightingOverlay __instance)
        {
            Section s = layerSection(__instance);
            int rows = s != null ? GroundRowsOf(s.map) : 0;
            if (rows <= 0 || s.botLeft.z >= rows || s.botLeft.z + 17 <= rows)
            {
                return;
            }
            List<LayerSubMesh> subs = __instance.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                List<int> t = sm.tris;
                List<Vector3> v = sm.verts;
                if (v.Count == 0)
                {
                    continue;
                }
                int kept = 0;
                for (int j = 0; j + 2 < t.Count; j += 3)
                {
                    if (v[t[j]].z > rows || v[t[j + 1]].z > rows || v[t[j + 2]].z > rows)
                    {
                        continue;
                    }
                    t[kept++] = t[j];
                    t[kept++] = t[j + 1];
                    t[kept++] = t[j + 2];
                }
                if (kept < t.Count)
                {
                    t.RemoveRange(kept, t.Count - kept);
                    if (sm.mesh != null)
                    {
                        sm.mesh.SetTriangles(t.ToArray(), 0);
                    }
                }
            }
        }

        // ---- the game's whole-map overlays (home area, roofs, beauty, temperature ...): one mesh of world-space cell
        // quads. Each cell's quad is moved onto its floor; on hidden floors and between floors it is folded away.
        // The drawers are remembered so a change of the view (another floor shown) remakes them.
        private static readonly AccessTools.FieldRef<CellBoolDrawer, List<Mesh>> boolMeshes = AccessTools.FieldRefAccess<CellBoolDrawer, List<Mesh>>("meshes");
        private static readonly List<System.WeakReference<CellBoolDrawer>> boolDrawers = new List<System.WeakReference<CellBoolDrawer>>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CellBoolDrawer, object> boolKnown = new System.Runtime.CompilerServices.ConditionalWeakTable<CellBoolDrawer, object>();

        public static void CellBool_Postfix(CellBoolDrawer __instance)
        {
            object unused;
            if (!boolKnown.TryGetValue(__instance, out unused))
            {
                boolKnown.Add(__instance, null);
                boolDrawers.Add(new System.WeakReference<CellBoolDrawer>(__instance));
            }
            if (!On)
            {
                return;
            }
            List<Mesh> meshes = boolMeshes(__instance);
            for (int i = 0; meshes != null && i < meshes.Count; i++)
            {
                Mesh mesh = meshes[i];
                Vector3[] v = mesh.vertices;
                bool changed = false;
                for (int q = 0; q + 3 < v.Length; q += 4)
                {
                    float z = Mathf.Min(Mathf.Min(v[q].z, v[q + 1].z), Mathf.Min(v[q + 2].z, v[q + 3].z));
                    if (z < groundRows)
                    {
                        continue;
                    }
                    changed = true;
                    Vector3 probe = new Vector3(v[q].x, v[q].y, z + 0.5f);
                    if (!Drawn(ref probe))
                    {
                        v[q + 1] = v[q + 2] = v[q + 3] = v[q];
                        continue;
                    }
                    Vector3 shift = probe - new Vector3(v[q].x, v[q].y, z + 0.5f);
                    for (int c = 0; c < 4; c++)
                    {
                        v[q + c] += shift;
                    }
                }
                if (changed)
                {
                    mesh.vertices = v;
                    mesh.RecalculateBounds();
                }
            }
        }

        // The view changed (floors on, another floor shown, another height): the overlays are remade.
        private static void ViewChanged()
        {
            for (int i = boolDrawers.Count - 1; i >= 0; i--)
            {
                CellBoolDrawer d;
                if (boolDrawers[i].TryGetTarget(out d))
                {
                    d.SetDirty();
                }
                else
                {
                    boolDrawers.RemoveAt(i);
                }
            }
        }

        // ---- the floor column (design section 5): cell (x, z + k * zShift) stands over ground cell (x, z). These read the
        // layout itself (not this frame's state), for code that builds meshes.

        // The ground rows of a map with floors, else 0.
        public static int GroundRowsOf(Map map)
        {
            IFloorLayout layout = Api.Floors;
            if (layout == null || map == null)
            {
                return 0;
            }
            try
            {
                int rows = layout.GroundRows(map);
                return rows > 0 && layout.FloorCount(map) > 0 ? rows : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // The floor of a cell (0 the ground, -1 between floors or without floors), its height and its row shift.
        public static int ColumnFloor(Map map, IntVec3 c, out float height, out int zShift)
        {
            height = 0f;
            zShift = 0;
            int rows = GroundRowsOf(map);
            if (rows <= 0)
            {
                return -1;
            }
            if (c.z < rows)
            {
                return 0;
            }
            int n = Mathf.Min(Api.Floors.FloorCount(map), MaxFloors);
            for (int k = 1; k <= n; k++)
            {
                float h;
                int z;
                Api.Floors.Floor(map, k, out h, out z);
                if (c.z >= z && c.z < z + rows)
                {
                    height = h;
                    zShift = z;
                    return k;
                }
            }
            return -1;
        }

        // The cell dk floors above (or below, dk < 0) a cell in its column; Invalid where there is none.
        public static IntVec3 Stacked(Map map, IntVec3 c, int dk)
        {
            float h;
            int z;
            int k = ColumnFloor(map, c, out h, out z);
            int to = k + dk;
            if (k < 0 || to < 0 || to > Mathf.Min(Api.Floors.FloorCount(map), MaxFloors))
            {
                return IntVec3.Invalid;
            }
            int zTo = 0;
            if (to > 0)
            {
                float h2;
                Api.Floors.Floor(map, to, out h2, out zTo);
            }
            return new IntVec3(c.x, 0, c.z - z + zTo);
        }

        // Whether a cell has a floor to lie on: always on the ground (and on maps without floors), on an upper floor only
        // where its floor is built (the open air up there has none), never between floors. Where the pointer's ray stops
        // (PointerFloorAt), and where what lies flat on the ground beside a thing can lie (GroundShadows; in the open air
        // it would hang across the wall below).
        public static bool HasFloor(Map map, IntVec3 c)
        {
            if (GroundRowsOf(map) <= 0)
            {
                return true;
            }
            float h;
            int z;
            int k = ColumnFloor(map, c, out h, out z);
            if (k <= 0)
            {
                return k == 0;
            }
            return c.InBounds(map) && !map.terrainGrid.TerrainAt(c).dontRender;
        }

        // The floor above a cell exists there (it is the cell's roof: the slab).
        public static bool SlabOver(Map map, IntVec3 c)
        {
            IntVec3 up = Stacked(map, c, 1);
            return up.IsValid && up.InBounds(map) && !map.terrainGrid.TerrainAt(up).dontRender;
        }

        // A full-height block (a wall) in a cell, and its height.
        public static bool FullBlock(Map map, IntVec3 c, out float height)
        {
            height = 0f;
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b == null || WallBoxes.Fill(b.def) < 0.9f)
            {
                return false;
            }
            ThingForm f = Forms.For(b.def);
            if (f.form != Form3D.Block)
            {
                return false;
            }
            height = f.height;
            return true;
        }

        // A block on an upper floor standing on a block below (one wall through both storeys): how far its sides
        // go down to meet the one below (cells), the floor's height above the ground and its row shift (so the
        // texture runs on). False (all zero) for anything else.
        public static bool WallColumn(Map map, IntVec3 c, out float drop, out float floorHeight, out int zShift)
        {
            drop = 0f;
            int k = ColumnFloor(map, c, out floorHeight, out zShift);
            if (k <= 0)
            {
                return false;
            }
            float below;
            IntVec3 under = Stacked(map, c, -1);
            if (FullBlock(map, under, out below))
            {
                float hb;
                int zb;
                ColumnFloor(map, under, out hb, out zb);
                drop = Mathf.Max(0f, floorHeight - hb - below) + 0.004f;
            }
            return true;
        }

        // Something in a column changed: the cells above and below it are remade too (a wall's sides reach down,
        // a slab is a roof below it, a roof sits on top of a column).
        private static bool dirtying;

        public static void MapMeshDirty_Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (dirtying || Api.Floors == null || Current.ProgramState != ProgramState.Playing || drawerSections(__instance) == null)
            {
                return;   // (as the game: nothing to remake while the map is made)
            }
            ulong want = (ulong)RimWorld.MapMeshFlagDefOf.Buildings | (ulong)RimWorld.MapMeshFlagDefOf.Terrain | (ulong)RimWorld.MapMeshFlagDefOf.Roofs;
            if ((dirtyFlags & want) == 0)
            {
                return;
            }
            Map map = mapOfDrawer(__instance);
            if (map == null || GroundRowsOf(map) <= 0)
            {
                return;
            }
            dirtying = true;
            try
            {
                ulong flags = (ulong)RimWorld.MapMeshFlagDefOf.Buildings | (ulong)RimWorld.MapMeshFlagDefOf.Roofs;
                SlabDirty(__instance, loc);
                for (int dk = -1; dk <= 1; dk += 2)
                {
                    IntVec3 o = Stacked(map, loc, dk);
                    if (o.IsValid && o.InBounds(map))
                    {
                        __instance.MapMeshDirty(o, flags);
                        SlabDirty(__instance, o);
                    }
                }
            }
            finally
            {
                dirtying = false;
            }
        }

        private static readonly AccessTools.FieldRef<MapDrawer, Map> mapOfDrawer = AccessTools.FieldRefAccess<MapDrawer, Map>("map");
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> drawerSections = AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        // The slab of the block holding a cell is built again (it leaves out cells with a wall through both storeys).
        private static void SlabDirty(MapDrawer drawer, IntVec3 c)
        {
            Section s = c.InBounds(mapOfDrawer(drawer)) ? drawer.SectionAt(c) : null;
            Slab slab;
            if (s != null && slabs.TryGetValue(s, out slab))
            {
                slab.dirty = true;
            }
        }
    }
}
