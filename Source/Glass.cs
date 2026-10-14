using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // See-through parts of models (ModelDef.glassModel): the glass of the mech gestators, the
    // growth vat and the biosculpter pod, the window of the wastepack atomizer. The glass is drawn
    // every frame after everything solid, without writing depth, so what is inside (drawn before
    // it, between the body and the glass) shows through. All glass is one pale colour at one
    // opacity (user decision 2026-10-14), lit like the models and darkened with the scene at night.
    // The game's flat glass, top and window sprites drawn over the contents are left out while the
    // model stands in (Patches: the buildings' DrawAt / PostDraw prefixes call in here).
    [StaticConstructorOnStartup]
    public static class Glass
    {
        public static readonly Color Tint = new Color(0.72f, 0.90f, 0.94f);
        public const float Opacity = 0.4f;

        private static List<ThingDef> defs;
        private static Material material;
        private static readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();

        // The thing's model entry while it is drawn as a model that has glass; else null.
        public static ModelEntry EntryFor(Thing t)
        {
            if (t == null || !View3D.Ready || !IsoRimMod.Settings.models3D)
            {
                return null;
            }
            ThingForm form = Forms.For(t.def);
            return form.form == Form3D.Model && form.model != null && form.model.glass != null ? form.model : null;
        }

        private static Material Mat
        {
            get
            {
                if (material == null)
                {
                    // After the game's lighting overlay (3100), lit by its own cell like the ghosts.
                    material = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_Glass", 3101, R3DMaterials.LightModel);
                    if (material != null)
                    {
                        material.color = new Color(1f, 1f, 1f, Opacity);
                        IsoGlow.Use(material, IsoGlow.OwnCellUnmarked);
                    }
                }
                return material;
            }
        }

        private static Mesh MeshFor(Model m)
        {
            Mesh mesh;
            if (meshes.TryGetValue(m.id, out mesh) && mesh != null)
            {
                return mesh;
            }
            int n = m.positions.Length - m.positions.Length % 3;
            Vector3[] verts = new Vector3[n];
            Color32[] colors = new Color32[n];
            int[] tris = new int[n];
            Color32 c = Tint;
            for (int i = 0; i < n; i++)
            {
                verts[i] = m.positions[i];
                colors[i] = c;
                tris[i] = i;
            }
            mesh = new Mesh();
            mesh.vertices = verts;
            mesh.colors32 = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            meshes[m.id] = mesh;
            return mesh;
        }

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.models3D || map == null)
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
                        if (ModelLibrary.TryGet(def, out entry) && entry.glass != null && entry.HasModel)
                        {
                            defs.Add(def);
                        }
                    }
                }
                if (defs.Count == 0 || Mat == null)
                {
                    return;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
                for (int d = 0; d < defs.Count; d++)
                {
                    ThingForm form = Forms.For(defs[d]);
                    if (form.form != Form3D.Model || form.model == null || form.model.glass == null)
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
                Log.ErrorOnce("[IsoRim] Drawing glass failed: " + e, 93611261);
            }
        }

        // The yaw the thing's model is drawn at (as the baker and Spinners turn it).
        public static float YawOf(Thing t, ModelEntry entry)
        {
            float yaw = t.Rotation.AsAngle + entry.def.rotation;
            if (entry.Swapped(t.def))
            {
                yaw += 90f;
            }
            return yaw;
        }

        private static void Draw(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            float yaw = YawOf(t, entry);
            Vector3 at = t.TrueCenter();
            Vector3 offset = Quaternion.AngleAxis(yaw, Vector3.up) * def.offset;
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(at.x + offset.x, offset.y / s, at.z + offset.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.TRS(Vector3.zero, Quaternion.AngleAxis(yaw, Vector3.up), entry.Fit(t.def) * def.scale);
            Mesh mesh = MeshFor(entry.glass);
            if (Cull.MeshVisible(mesh, matrix))
            {
                Graphics.DrawMesh(mesh, matrix, Mat, 0);
            }
        }

        // Where the contents stand: the middle of the thing moved by contentOffset (model space, turned with it).
        public static Vector3 ContentCentre(Thing t, ModelEntry entry)
        {
            Vector3 c = t.TrueCenter();
            Vector3 shift = Quaternion.AngleAxis(t.Rotation.AsAngle + entry.def.rotation, Vector3.up) * entry.def.contentOffset;
            return new Vector3(c.x + shift.x, c.y, c.z + shift.z);
        }

        public static float Floor(ModelEntry entry)
        {
            return entry.def.surfaceHeight >= 0f ? entry.def.surfaceHeight : 0f;
        }

        public static float Room(ModelEntry entry)
        {
            float floor = Floor(entry);
            return entry.def.contentTop > floor ? entry.def.contentTop - floor : 1f;
        }

        // A flat sprite the game draws inside (a forming mech, a vat embryo) stood upright on the
        // chamber floor, facing the camera, shrunk if it would reach the chamber roof. rise lifts it
        // off the floor (a floating embryo).
        public static void DrawUpright(Thing t, ModelEntry entry, Material mat, Vector2 size, float rise)
        {
            if (mat == null)
            {
                return;
            }
            float k = Mathf.Min(1f, (Room(entry) - rise - 0.02f) / Mathf.Max(0.05f, size.y));
            Vector2 drawn = size * Mathf.Max(0.1f, k);
            Vector3 c = ContentCentre(t, entry);
            Vector3 pos = new Vector3(c.x, AltitudeLayer.BuildingOnTop.AltitudeFor(), c.z);

            bool wasActive = DrawHook.active, wasBillboard = DrawHook.billboard, wasAnchored = DrawHook.hasAnchor;
            float wasHeight = DrawHook.billboardHeight;
            Matrix4x4 wasXform = DrawHook.xform;
            try
            {
                DrawHook.active = true;
                DrawHook.billboard = true;
                DrawHook.hasAnchor = false;
                DrawHook.billboardHeight = Floor(entry) + 0.01f + rise + drawn.y * 0.5f;
                DrawHook.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(drawn.x, 1f, drawn.y)), mat, 0);
            }
            finally
            {
                DrawHook.active = wasActive;
                DrawHook.billboard = wasBillboard;
                DrawHook.hasAnchor = wasAnchored;
                DrawHook.billboardHeight = wasHeight;
                DrawHook.xform = wasXform;
            }
        }

        // ---- the buildings' own per-frame drawing while their model (with glass) stands in

        private static MethodInfo formingGraphicOf;
        private static FieldInfo barFilled, barUnfilled;
        private static MethodInfo fetusEarly, fetusLate;
        private static bool reflected;

        private static void Reflect()
        {
            reflected = true;
            formingGraphicOf = AccessTools.Method(typeof(Building_MechGestator), "TryGetMechFormingGraphic");
            barFilled = AccessTools.Field(typeof(Building_MechGestator), "FormingCycleBarFilledMat");
            barUnfilled = AccessTools.Field(typeof(Building_MechGestator), "FormingCycleUnfilledMat");
            fetusEarly = AccessTools.PropertyGetter(typeof(Building_GrowthVat), "FetusEarlyStage");
            fetusLate = AccessTools.PropertyGetter(typeof(Building_GrowthVat), "FetusLateStage");
        }

        // Building_MechGestator.DrawAt without the flat glass cylinder and top: the forming mech
        // stands on the chamber floor, the cycle bar lies on the console (barHeight).
        public static void DrawMechGestator(Building_MechGestator g, ModelEntry entry, Vector3 drawLoc, bool flip, Action<Thing, Vector3, bool> baseDraw)
        {
            if (!reflected)
            {
                Reflect();
            }
            baseDraw(g, drawLoc, flip);
            Bill_Autonomous bill = g.ActiveBill;
            BuildingProperties b = g.def.building;
            if (bill != null && bill.State != FormingState.Gathering && b.formingGraphicData != null)
            {
                Graphic graphic = null;
                Rot4 rot = Rot4.North;
                if (formingGraphicOf != null)
                {
                    object[] args = { null };
                    if ((bool)formingGraphicOf.Invoke(g, args))
                    {
                        graphic = (Graphic)args[0];
                        rot = Rot4.South;
                    }
                }
                if (graphic == null)
                {
                    graphic = b.formingGraphicData.Graphic;
                }
                float bob = Mathf.PingPong(Find.TickManager.TicksGame * b.formingMechBobSpeed, b.formingMechYBobDistance);
                DrawUpright(g, entry, graphic.MatAt(rot, g), graphic.drawSize, bob);
            }
            GenDraw.FillableBarRequest bar = g.BarDrawData;
            bar.center = drawLoc;
            bar.fillPercent = g.CurrentBillFormingPercent;
            bar.filledMat = barFilled != null ? (Material)barFilled.GetValue(null) : bar.filledMat;
            bar.unfilledMat = barUnfilled != null ? (Material)barUnfilled.GetValue(null) : bar.unfilledMat;
            bar.rotation = g.Rotation;
            Matrix4x4 was = DrawHook.xform;
            if (entry.def.barHeight >= 0f && DrawHook.active && !DrawHook.billboard)
            {
                DrawHook.xform = View3D.LiftMatrix(entry.def.barHeight);
            }
            try
            {
                GenDraw.DrawFillableBar(bar);
            }
            finally
            {
                DrawHook.xform = was;
            }
        }

        // Building_GrowthVat.DrawAt without the flat top: a growing embryo floats upright in the vat.
        // (A child inside is drawn by the game's DynamicDrawPhaseAt, as a 3D figure: People.Draw.)
        public static void DrawGrowthVat(Building_GrowthVat vat, ModelEntry entry, Vector3 drawLoc, bool flip, Action<Thing, Vector3, bool> baseDraw)
        {
            if (!reflected)
            {
                Reflect();
            }
            baseDraw(vat, drawLoc, flip);
            HumanEmbryo embryo = vat.selectedEmbryo;
            if (vat.Working && vat.SelectedPawn == null && embryo != null && vat.innerContainer.Contains(embryo))
            {
                MethodInfo getter = vat.EmbryoGestationTicksRemaining > 540000 ? fetusEarly : fetusLate;
                Graphic fetus = getter != null ? (Graphic)getter.Invoke(vat, null) : null;
                if (fetus != null)
                {
                    float size = Mathf.Lerp(0.4f, 0.95f, vat.EmbryoGestationPct);
                    DrawUpright(vat, entry, fetus.MatAt(Rot4.North), new Vector2(size, size), FloatRise(Find.TickManager.TicksGame));
                }
            }
        }

        // The game's slow float of a vat's or pod's occupant (CompBiosculpterPod.FloatingOffset), as a rise.
        public static float FloatRise(float ticks)
        {
            return 0.02f + CompBiosculpterPod.FloatingOffset(ticks).z;
        }

        // Building_WastepackAtomizer.DrawAt without the flat bottom and window: the wastepacks
        // (CompAtomizer.PostDraw) lie on the chamber floor in the middle, under the window.
        public static void DrawAtomizer(Building_WastepackAtomizer w, ModelEntry entry, Vector3 drawLoc, bool flip, Action<Thing, Vector3, bool> baseDraw)
        {
            CompAtomizer atomizer = w.Atomizer;
            Matrix4x4 was = DrawHook.xform;
            try
            {
                if (atomizer != null && DrawHook.active && !DrawHook.billboard)
                {
                    // The game draws them at a fixed offset from the middle, the same for every rotation
                    // (the draw scope has already moved everything by contentOffset).
                    Vector3 off = atomizer.Props.contentsDrawOffset;
                    DrawHook.xform = Matrix4x4.Translate(new Vector3(-off.x, 0f, -off.z)) * DrawHook.xform;
                }
                baseDraw(w, drawLoc, flip);
            }
            finally
            {
                DrawHook.xform = was;
            }
        }

        // CompBiosculpterPod.PostDraw without the flat background: the occupant lies in the pod,
        // drawn as the game does (a 3D figure, People.Draw), in the middle of the pod.
        public static void DrawBiosculpter(CompBiosculpterPod pod, ModelEntry entry)
        {
            if (pod.State != BiosculpterPodState.Occupied)
            {
                return;
            }
            Pawn occupant = pod.Occupant;
            if (occupant == null)
            {
                return;
            }
            Vector3 c = ContentCentre(pod.parent, entry);
            Vector3 at = new Vector3(c.x, pod.parent.DrawPos.y, c.z) + CompBiosculpterPod.FloatingOffset(Find.TickManager.TicksGame);
            occupant.Drawer.renderer.RenderPawnAt(at, null, true);
        }

        // The building holding a person the game draws inside it (gene extractor, growth vat,
        // biosculpter pod: the last holds them in a comp), or null.
        public static Building HolderOf(Pawn pawn)
        {
            IThingHolder holder = pawn.ParentHolder;
            if (!(holder is IThingHolderWithDrawnPawn))
            {
                return null;
            }
            Building b = holder as Building;
            if (b == null && holder is ThingComp comp)
            {
                b = comp.parent as Building;
            }
            return b != null && b.Spawned ? b : null;
        }
    }
}
