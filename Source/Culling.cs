using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Frustum culling for the 3D view. Unity culled every DrawMesh by its bounds; Waylith draws every
    // command it is given, so in 1.7 the game's own view rectangle was the only cull, and the 3D view
    // widens that to the axis-aligned box of the tilted, turned screen (View3D.GroundRect): at 45
    // degrees about twice what shows. Every command costs main-thread and render-thread time whether
    // it shows or not, so the far-zoom frame doubled.
    //
    // Exact and conservative: a block of the map (a section) or a model part is left out only when its
    // whole box, in the space it is drawn in, lies outside the screen. Section boxes come from the
    // vertices of every sub-mesh the section draws, widened where a vertex shader moves vertices: plant
    // sway and wall end lines (a little), upright cards (their card size, IsoRimCard.vert), the fog lid
    // (lifted onto the tallest top in the map, IsoRimFog.vert) and the game's sun shadows (slid along
    // the cast vector). A section with a sub-mesh on any other vertex shader that moves vertices is
    // never left out by this (the game's own rectangle test only). Nothing is tested against depth.
    // Only the shown camera (View3D.ApplyCamera's matrices, which the frame is rendered with).
    public static class Cull
    {
        // Harness A/B switch (probe cull=0/1), and a deliberately too small screen (probe cullshrink=f < 1)
        // to prove the A/B screenshots can see a wrong cull.
        public static bool Enabled = true;
        public static float Shrink = 1f;

        private static bool ready;
        private static Vector4 rowX, rowY;       // clip x and y of a point in drawn (squashed) space; orthographic, so affine
        private static float limX = 1f, limY = 1f;
        private static float squash = 0.003f;

        // Set in View3D.ApplyCamera: the matrices the frame is drawn with.
        public static void SetCamera(Matrix4x4 viewProj, float s)
        {
            rowX = viewProj.GetRow(0);
            rowY = viewProj.GetRow(1);
            // Two pixels of slack on every side.
            limX = (1f + 4f / Mathf.Max(1, Screen.width)) * Shrink;
            limY = (1f + 4f / Mathf.Max(1, Screen.height)) * Shrink;
            squash = s;
            ready = true;
        }

        public static void Reset()
        {
            ready = false;
        }

        // Harness: whether the given view-projection (the one a frame is rendered with) is the one culled against.
        public static bool SameCamera(Matrix4x4 viewProj)
        {
            return !ready || (viewProj.GetRow(0) == rowX && viewProj.GetRow(1) == rowY);
        }

        // Never during the gravship cutscene or capture, which draw parts of the map elsewhere.
        private static bool On
        {
            get { return ready && Enabled && View3D.Ready && !WorldComponent_GravshipController.CutsceneInProgress && !WorldComponent_GravshipController.GravshipRenderInProgess; }
        }

        // Whether any of the box (min..max, drawn space) can be on screen.
        public static bool BoxVisible(Vector3 min, Vector3 max)
        {
            float cx = (min.x + max.x) * 0.5f, cy = (min.y + max.y) * 0.5f, cz = (min.z + max.z) * 0.5f;
            float ex = (max.x - min.x) * 0.5f, ey = (max.y - min.y) * 0.5f, ez = (max.z - min.z) * 0.5f;
            float x = rowX.x * cx + rowX.y * cy + rowX.z * cz + rowX.w;
            float rx = Mathf.Abs(rowX.x) * ex + Mathf.Abs(rowX.y) * ey + Mathf.Abs(rowX.z) * ez;
            if (x - rx > limX || x + rx < -limX)
            {
                return false;
            }
            float y = rowY.x * cx + rowY.y * cy + rowY.z * cz + rowY.w;
            float ry = Mathf.Abs(rowY.x) * ex + Mathf.Abs(rowY.y) * ey + Mathf.Abs(rowY.z) * ez;
            return !(y - ry > limY || y + ry < -limY);
        }

        // The same for a mesh drawn with a matrix (exact for the mesh's box under any affine matrix).
        public static bool MeshVisible(Mesh mesh, Matrix4x4 m)
        {
            Vector3 min, max;
            if (!On || !inThings || !BoundsOf(mesh, out min, out max))
            {
                return true;
            }
            Vector3 c = (min + max) * 0.5f, e = (max - min) * 0.5f;
            // Clip rows of the whole transform: row * m.
            float ax = rowX.x * m.m00 + rowX.y * m.m10 + rowX.z * m.m20;
            float bx = rowX.x * m.m01 + rowX.y * m.m11 + rowX.z * m.m21;
            float cxr = rowX.x * m.m02 + rowX.y * m.m12 + rowX.z * m.m22;
            float dx = rowX.x * m.m03 + rowX.y * m.m13 + rowX.z * m.m23 + rowX.w;
            float x = ax * c.x + bx * c.y + cxr * c.z + dx;
            float rx = Mathf.Abs(ax) * e.x + Mathf.Abs(bx) * e.y + Mathf.Abs(cxr) * e.z;
            if (x - rx > limX || x + rx < -limX)
            {
                culledParts++;
                return false;
            }
            float ay = rowY.x * m.m00 + rowY.y * m.m10 + rowY.z * m.m20;
            float by = rowY.x * m.m01 + rowY.y * m.m11 + rowY.z * m.m21;
            float cy = rowY.x * m.m02 + rowY.y * m.m12 + rowY.z * m.m22;
            float dy = rowY.x * m.m03 + rowY.y * m.m13 + rowY.z * m.m23 + rowY.w;
            float y = ay * c.x + by * c.y + cy * c.z + dy;
            float ry = Mathf.Abs(ay) * e.x + Mathf.Abs(by) * e.y + Mathf.Abs(cy) * e.z;
            if (y - ry > limY || y + ry < -limY)
            {
                culledParts++;
                return false;
            }
            if (drawingHidden)
            {
                thingViolations++;
            }
            return true;
        }

        // The box of a mesh's vertices (Waylith works it out from the vertices when the triangles are set;
        // Mesh.bounds may have been overwritten, sub-mesh 0's own box is not).
        private static bool BoundsOf(Mesh mesh, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            if (mesh == null)
            {
                return false;
            }
            Waylith.Graphics.Meshes.Mesh em = mesh.EngineMesh;
            if (em == null)
            {
                return false;
            }
            Waylith.Graphics.Meshes.CpuMeshData data = em.CpuStorage;
            if (data == null || data.VertexCount == 0)
            {
                return false;
            }
            Waylith.Graphics.Meshes.SubMesh[] subs = data.SubMeshes;
            if (subs == null || subs.Length != 1)
            {
                return false;   // several sub-meshes: not used by IsoRim or the game's map layers; drawn
            }
            Waylith.Graphics.Meshes.MeshBounds b = subs[0].Bounds;
            if (b.IsEmpty || (b.MinX == 0f && b.MinY == 0f && b.MinZ == 0f && b.MaxX == 0f && b.MaxY == 0f && b.MaxZ == 0f))
            {
                return false;   // no box worked out (set through another path): drawn
            }
            if (float.IsNaN(b.MinX) || float.IsInfinity(b.MinX) || float.IsInfinity(b.MaxX))
            {
                return false;
            }
            min = new Vector3(b.MinX, b.MinY, b.MinZ);
            max = new Vector3(b.MaxX, b.MaxY, b.MaxZ);
            return true;
        }

        // ---- moving models (DynamicModels.Submit): only while the game draws the map's things, so a
        // portrait or any other camera is never affected.
        private static bool inThings;
        public static int culledParts;

        public static void ThingsBegin()
        {
            inThings = true;
            hidden.Clear();
        }

        public static void ThingsEnd()
        {
            inThings = false;
            drawingHidden = false;
        }

        // ---- whole things (Thing.DynamicDrawPhase), so their per-frame work is skipped too: only pawns and
        // doors, in a box far larger than anything they draw (pawns: the largest of body size and sprite size,
        // plus a cell and a half round and half as much again up; doors: their cells and one more, the wall
        // height and a cell and a half up).
        // Decided once a frame on the main thread (EnsureInitialized, or Draw when that phase is not run);
        // the parallel pre-draw only reads the decisions.
        private static readonly Dictionary<Thing, bool> hidden = new Dictionary<Thing, bool>();
        private static bool drawingHidden;
        public static bool checkThings;            // harness: hidden things drawn anyway, parts that show counted
        public static int thingsCulled, thingViolations;

        public static bool ThingPhase(Thing t, DrawPhase phase)
        {
            if (!inThings || !On || !(t is Pawn || t is Building_Door))
            {
                return true;
            }
            bool hide;
            if (phase == DrawPhase.ParallelPreDraw)
            {
                return !hidden.TryGetValue(t, out hide) || !hide || checkThings;
            }
            if (!hidden.TryGetValue(t, out hide))
            {
                try
                {
                    hide = !ThingMayShow(t);
                }
                catch (Exception e)
                {
                    hide = false;
                    Log.ErrorOnce("[IsoRim] Culling " + t + " failed: " + e, 93611302);
                }
                hidden[t] = hide;
                if (hide)
                {
                    thingsCulled++;
                }
            }
            if (phase == DrawPhase.Draw)
            {
                drawingHidden = hide && checkThings;
            }
            return !hide || checkThings;
        }

        public static void ThingPhaseDone()
        {
            drawingHidden = false;
        }

        private static bool ThingMayShow(Thing t)
        {
            float s = squash;
            Vector3 min, max;
            if (t is Pawn)
            {
                Pawn pawn = (Pawn)t;
                Vector3 p = t.DrawPos;
                Vector2 ds = t.DrawSize;
                float big = Mathf.Max(Mathf.Max(1f, pawn.BodySize), Mathf.Max(ds.x, ds.y));
                // (Humanlike pawns have no kind life stage graphics; asking logs an error.)
                PawnKindLifeStage stage = pawn.ageTracker != null && pawn.RaceProps != null && !pawn.RaceProps.Humanlike ? pawn.ageTracker.CurKindLifeStage : null;
                if (stage != null && stage.bodyGraphicData != null)
                {
                    big = Mathf.Max(big, Mathf.Max(stage.bodyGraphicData.drawSize.x, stage.bodyGraphicData.drawSize.y));
                }
                float r = 1.5f + big;
                min = new Vector3(p.x - r, -1f / s, p.z - r);
                max = new Vector3(p.x + r, (2.5f + 1.5f * big) / s, p.z + r);
            }
            else
            {
                CellRect r = t.OccupiedRect();
                min = new Vector3(r.minX - 1f, -1f / s, r.minZ - 1f);
                max = new Vector3(r.maxX + 2f, (View3D.WallHeight + 1.5f) / s, r.maxZ + 2f);
            }
            return BoxVisible(min, max);
        }

        // ---- sections

        // Bumped whenever any map sub-mesh gets new vertices (LayerSubMesh.FinalizeMesh with verts):
        // section boxes are worked out again when next tested.
        private static int geometryStamp = 1;

        public static void MeshFinalized(MeshParts parts)
        {
            if ((parts & MeshParts.Verts) != MeshParts.None)
            {
                geometryStamp++;
            }
        }

        private enum Shift : byte { None, Small, Card, Fog, SunShadow, Unknown }

        private class SectionBox
        {
            public int stamp;
            public int settings;
            public bool unknown;      // a sub-mesh whose vertex shader moves vertices by an unknown amount
            public bool any;
            public Vector3 min, max;  // everything that is not moved by a known amount at draw time
            public bool fog;
            public Vector3 fogMin, fogMax;
            public bool shadow;
            public Vector3 shadowMin, shadowMax;
        }

        private static readonly ConditionalWeakTable<Section, SectionBox> boxes = new ConditionalWeakTable<Section, SectionBox>();
        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> layersOf = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
        private static readonly AccessTools.FieldRef<Section, bool> anyDirtyOf = AccessTools.FieldRefAccess<Section, bool>("anyLayerDirty");
        private static readonly Dictionary<Waylith.Rendering.Shader, Shift> shifts = new Dictionary<Waylith.Rendering.Shader, Shift>();
        private static readonly HashSet<string> unknownSeen = new HashSet<string>();

        // Harness counters (per frame, read by the probe).
        public static int sectionsTested, sectionsCulled, sectionsUnknown;

        // Replaces "view.Overlaps(section.Bounds)" in MapDrawer.DrawMapMesh.
        public static bool SectionOverlaps(ref CellRect view, Section section)
        {
            bool overlaps = view.Overlaps(section.Bounds);
            if (!overlaps || !On)
            {
                return overlaps;
            }
            sectionsTested++;
            try
            {
                if (SectionVisible(section))
                {
                    return true;
                }
                sectionsCulled++;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Culling a map block failed: " + e, 93611301);
                return true;
            }
        }

        private static bool SectionVisible(Section section)
        {
            if (anyDirtyOf(section))
            {
                return true;   // drawn, which rebuilds it (its new shape is not known yet)
            }
            SectionBox box = boxes.GetValue(section, s => new SectionBox());
            int settings = SettingsKey();
            if (box.stamp != geometryStamp || box.settings != settings)
            {
                Measure(section, box);
                box.stamp = geometryStamp;
                box.settings = settings;
            }
            if (box.unknown)
            {
                sectionsUnknown++;
                return true;
            }
            if (box.any && BoxVisible(box.min, box.max))
            {
                return true;
            }
            if (box.fog)
            {
                // Lifted onto the tallest top of any cell (IsoRimFog.vert: (base + top + 0.012) / squash).
                float lift = FogLift(section.map) / squash;
                if (BoxVisible(box.fogMin, new Vector3(box.fogMax.x, box.fogMax.y + lift, box.fogMax.z)))
                {
                    return true;
                }
            }
            if (box.shadow)
            {
                // Slid by the cast vector times the vertex alpha (0..1): the box and the box slid all the way.
                Vector4 cast;
                if (!Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out cast))
                {
                    return true;
                }
                Vector3 v = new Vector3(cast.x, cast.y, cast.z);
                Vector3 lo = Vector3.Min(box.shadowMin, box.shadowMin + v);
                Vector3 hi = Vector3.Max(box.shadowMax, box.shadowMax + v);
                if (BoxVisible(lo - Vector3.one * 0.05f, hi + Vector3.one * 0.05f))
                {
                    return true;
                }
            }
            return false;
        }

        private static int SettingsKey()
        {
            return View3D.Stamp * 31 + (IsoRimMod.Settings.fogLid ? 1 : 0);
        }

        private static float FogLift(Map map)
        {
            IsoCellMap c = IsoCellMap.Existing(map);
            float top = c != null && c.Texture != null ? c.MaxTop : 16f;
            return (c != null ? c.baseY : 0f) + top + 0.012f + 0.05f;
        }

        // Works out a section's boxes from the sub-meshes of all its layers.
        private static void Measure(Section section, SectionBox box)
        {
            box.unknown = false;
            box.any = box.fog = box.shadow = false;
            List<SectionLayer> layers = layersOf(section);
            float s = View3D.Squash;
            bool fogLid = IsoRimMod.Settings.fogLid;
            for (int li = 0; li < layers.Count; li++)
            {
                List<LayerSubMesh> subs = layers[li].subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    LayerSubMesh sm = subs[i];
                    if (!sm.finalized || sm.mesh == null || sm.material == null)
                    {
                        continue;
                    }
                    Vector3 min, max;
                    if (!BoundsOf(sm.mesh, out min, out max))
                    {
                        if (sm.mesh.vertexCount > 0)
                        {
                            box.unknown = true;
                            return;
                        }
                        continue;
                    }
                    Shift shift = fogLid && sm.material == MatBases.FogOfWar ? Shift.Fog : ShiftOf(sm.material);
                    switch (shift)
                    {
                        case Shift.Unknown:
                            box.unknown = true;
                            return;
                        case Shift.Fog:
                            Grow(ref box.fog, ref box.fogMin, ref box.fogMax, min, max);
                            break;
                        case Shift.SunShadow:
                            Grow(ref box.shadow, ref box.shadowMin, ref box.shadowMax, min, max);
                            break;
                        case Shift.Card:
                        {
                            // Corners turn about the hinge: within the card's size of it every way (true cells;
                            // drawn height is divided by squash), plus the sway.
                            float r = CardReach(sm) + 0.25f;
                            Vector3 pad = new Vector3(r, r / s, r);
                            Grow(ref box.any, ref box.min, ref box.max, min - pad, max + pad);
                            break;
                        }
                        default:
                        {
                            // Plant sway (0.1 cells), wall end lines (0.05) and outline shells (pushed along the
                            // line of sight only): a quarter cell every way.
                            Vector3 pad = new Vector3(0.25f, 0.25f / s, 0.25f);
                            Grow(ref box.any, ref box.min, ref box.max, min - pad, max + pad);
                            break;
                        }
                    }
                }
            }
        }

        private static void Grow(ref bool has, ref Vector3 min, ref Vector3 max, Vector3 a, Vector3 b)
        {
            if (!has)
            {
                has = true;
                min = a;
                max = b;
                return;
            }
            min = Vector3.Min(min, a);
            max = Vector3.Max(max, b);
        }

        private static float CardReach(LayerSubMesh sm)
        {
            float r = 0f;
            List<Vector3> o = sm.uvsChannelTwo;
            for (int i = 0; i < o.Count; i++)
            {
                r = Mathf.Max(r, o[i].sqrMagnitude);
            }
            return Mathf.Sqrt(r);
        }

        // How a material's vertex shader moves vertices, from its file name.
        private static Shift ShiftOf(Material mat)
        {
            Waylith.Rendering.Material em = mat.engineMaterial;
            Waylith.Rendering.Shader shader = em != null ? em.Shader : null;
            if (shader == null)
            {
                return Shift.Unknown;
            }
            Shift shift;
            if (shifts.TryGetValue(shader, out shift))
            {
                return shift;
            }
            string file = shader.VertexShaderFileName;
            string name = string.IsNullOrEmpty(file) ? "" : System.IO.Path.GetFileName(file);
            switch (name)
            {
                // Position passed straight through (checked in the 1.7 shader sources).
                case "":
                case "SceneDefault.vert.glsl":
                case "IsoRimTex.vert.glsl":      // SceneDefault plus instancing
                case "MapWorldUV.vert.glsl":
                case "TerrainWater.vert.glsl":
                case "Transparent.vert.glsl":
                case "TransparentAnimated.vert.glsl":
                case "TransparentAnimatedColorLerp.vert.glsl":
                case "Darkness.vert.glsl":
                case "CutoutAnimated.vert.glsl":
                case "FrameAnim.vert.glsl":
                case "GasRotating.vert.glsl":
                case "FerroWave.vert.glsl":
                case "PollutionCloud.vert.glsl":
                case "OxygenOverlayIndoors.vert.glsl":
                case "VacuumOverlay.vert.glsl":
                case "BuildingSquashNStretch.vert.glsl":
                    shift = Shift.None;
                    break;
                case "PlantWindSway.vert.glsl":   // 0.1 cells
                case "IsoRim.vert.glsl":          // wall end lines 0.05; outline push along the line of sight
                    shift = Shift.Small;
                    break;
                case "IsoRimCard.vert.glsl":
                    shift = Shift.Card;
                    break;
                case "IsoRimFog.vert.glsl":
                    shift = Shift.Fog;
                    break;
                case "SunShadow.vert.glsl":       // + global _CastVect (SkyManager) times vertex alpha
                case "SunShadowFade.vert.glsl":   // the same (its _CastVect has no material default: the global)
                    shift = Shift.SunShadow;
                    break;
                default:
                    shift = Shift.Unknown;
                    if (unknownSeen.Add(name))
                    {
                        ModLog.Note("[IsoRim] Map blocks drawn with " + name + " (" + shader.Name + ") are never culled: its vertex stage moves vertices.");
                    }
                    break;
            }
            shifts[shader] = shift;
            return shift;
        }

        // MapDrawer.DrawMapMesh: "view.Overlaps(section.Bounds)" becomes SectionOverlaps(ref view, section).
        public static IEnumerable<CodeInstruction> DrawMapMeshTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getBounds = AccessTools.PropertyGetter(typeof(Section), nameof(Section.Bounds));
            MethodInfo overlaps = AccessTools.Method(typeof(CellRect), nameof(CellRect.Overlaps), new[] { typeof(CellRect) });
            MethodInfo replacement = AccessTools.Method(typeof(Cull), nameof(SectionOverlaps));
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int done = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) && list[i].operand as MethodInfo == getBounds
                    && list[i + 1].opcode == OpCodes.Call && list[i + 1].operand as MethodInfo == overlaps)
                {
                    list[i].opcode = OpCodes.Nop;
                    list[i].operand = null;
                    list[i + 1].operand = replacement;
                    done++;
                }
            }
            if (done != 1)
            {
                ModLog.Warn("[IsoRim] MapDrawer.DrawMapMesh is not in the expected form (" + done + " section tests found): map blocks off screen are drawn too.");
            }
            return list;
        }
    }
}
