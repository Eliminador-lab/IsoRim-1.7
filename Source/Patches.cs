using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Every hook into the game lives here. Each one is applied on its own, so if a game update
    // renames something, that one feature logs a warning and switches itself off while the rest
    // keep working. With the 3D view off, every hook passes straight through to the original.
    public static class Patches
    {
        private static Harmony harmony;
        private static AccessTools.FieldRef<PawnRenderer, Pawn> pawnOfRenderer;
        private static readonly List<FieldInfo> dragFields = new List<FieldInfo>();   // the game's own camera movement, cleared while the mod drags
        private static bool dragging;
        private static Vector3 dragAnchor;     // the point on the ground that was under the pointer when the drag began
        private static Vector3 dragStartPos;   // where the camera was at the start of this frame

        // Set while a standing pawn is being drawn: where its feet are and how big it is
        // compared with a person.
        private static bool pawnUpright;
        private static bool pawnFigure;      // the pawn being drawn is a 3D figure
        private static float pawnFacing;     // which way it faces, degrees clockwise from north
        private static float pawnHandHeight; // how high it holds a weapon, in cells
        private static Vector3 pawnFeet;
        private static float pawnSize = 1f;
        private const float HandHeight = 0.55f;   // up the sprite from the feet, in cells
        private const float HandReach = 0.2f;     // how far in front of the pawn the weapon is held
        private static readonly Dictionary<Type, int> layerKinds = new Dictionary<Type, int>();

        public static void ApplyAll()
        {
            harmony = new Harmony("isorim.core");

            Patch("camera", AccessTools.Method(typeof(CameraDriver), "Update"),
                postfix: nameof(CameraUpdate_Postfix));

            // Moving the camera: the game moves it along the map's own axes, which only match
            // the screen while north is up.
            Patch("camera movement in the isometric view", AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect"),
                postfix: nameof(DollyVect_Postfix));
            // Dragging the map with the middle mouse button: done by the mod while the 3D view is on,
            // so the ground stays under the pointer whichever way the camera is turned.
            foreach (string name in new[] { "desiredDollyRaw", "desiredDolly", "mouseDragVect" })
            {
                FieldInfo f = AccessTools.Field(typeof(CameraDriver), name);
                if (f != null && f.FieldType == typeof(Vector2))
                {
                    dragFields.Add(f);
                }
            }
            Patch("map dragging", AccessTools.Method(typeof(CameraDriver), "Update"), prefix: nameof(CameraUpdate_Prefix));

            // Dragging a selection box, and the name labels under colonists.
            Type selection = AccessTools.TypeByName("RimWorld.ThingSelectionUtility");
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "GetMapRect"),
                postfix: nameof(GetMapRect_Postfix));
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectableThingsInScreenRectDistinct"),
                postfix: nameof(DragSelect_Postfix));
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectableZonesInScreenRectDistinct"),
                postfix: nameof(DragSelectZones_Postfix));
            Patch("box selection in the isometric view", selection == null ? null : AccessTools.Method(selection, "MultiSelectablePlansInScreenRectDistinct"),
                postfix: nameof(DragSelectPlans_Postfix));
            Patch("name labels in the isometric view", AccessTools.Method(typeof(GenMapUI), "LabelDrawPosFor", new[] { typeof(Thing), typeof(float) }),
                prefix: nameof(LabelDrawPosFor_Prefix));

            Patch("mouse position", AccessTools.Method(typeof(Verse.UI), "UIToMapPosition", new[] { typeof(Vector2) }),
                prefix: nameof(UIToMapPosition_Prefix));

            Patch("visible area", AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentViewRect"),
                postfix: nameof(CurrentViewRect_Postfix));

            Type thingsLayer = AccessTools.TypeByName("Verse.SectionLayer_ThingsGeneral");
            Patch("3D shapes", thingsLayer == null ? null : AccessTools.Method(thingsLayer, "TakePrintFrom"),
                prefix: nameof(TakePrintFrom_Prefix), finalizer: nameof(TakePrintFrom_Finalizer));

            bool anyPrintPlane = false;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Printer_Plane)))
            {
                // 1.7: PrintPlane takes the base class MapDrawLayer (SectionLayer derives from it).
                if (m.Name == "PrintPlane" && StartsWith(m, typeof(MapDrawLayer), typeof(Vector3), typeof(Vector2), typeof(Material), typeof(float)))
                {
                    anyPrintPlane |= Patch("3D shapes and upright plants", m, postfix: nameof(PrintPlane_Postfix));
                }
            }
            if (!anyPrintPlane)
            {
                ModLog.Warn("[IsoRim] Printer_Plane.PrintPlane was not found in the expected form. Nothing can be raised and plants stay flat.");
            }

            // 1.7: DrawLayer is declared on MapDrawLayer and SectionLayer does not override it, so the
            // declared method is patched; the prefix passes non-section layers through.
            Patch("lighting and fog", AccessTools.DeclaredMethod(typeof(MapDrawLayer), "DrawLayer"),
                prefix: nameof(DrawLayer_Prefix));

            try
            {
                pawnOfRenderer = AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");
            }
            catch (Exception)
            {
                pawnOfRenderer = null;
            }
            if (pawnOfRenderer == null)
            {
                missed.Add("upright pawns (PawnRenderer.pawn)");
            }
            Patch("upright pawns", AccessTools.Method(typeof(PawnRenderer), "RenderPawnAt"),
                prefix: nameof(RenderPawnAt_Prefix), finalizer: nameof(RenderPawnAt_Finalizer));

            // The game works out each pawn's sprite layers ahead of drawing, on worker threads.
            // For a pawn the mod draws as a 3D model that work is wasted, so it is skipped.
            MethodInfo preRender = FindMethod(typeof(PawnRenderer), "ParallelPreRenderPawnAt");
            if (preRender != null)
            {
                Patch("skipping sprite work for 3D pawns", preRender, prefix: nameof(PreRender_Prefix));
            }

            // Walls, rock and trees live in the mod's own layers. Whenever one appears or goes
            // (built, deconstructed, mined, cut down) those layers are redrawn round it.
            Patch("redrawing walls and trees as they change", FindMethod(typeof(Thing), "SpawnSetup"), postfix: nameof(SpawnSetup_Postfix));
            Patch("redrawing walls and trees as they go", FindMethod(typeof(Thing), "DeSpawn"), prefix: nameof(DeSpawn_Prefix));

            // The preview of what is being placed, under the pointer.
            Type ghostDrawer = AccessTools.TypeByName("Verse.GhostDrawer");
            MethodInfo ghostThing = ghostDrawer != null ? FindMethod(ghostDrawer, "DrawGhostThing") : null;
            if (ghostThing != null && (!StartsWith(ghostThing, typeof(IntVec3), typeof(Rot4), typeof(ThingDef), typeof(Graphic), typeof(Color))
                || ghostThing.GetParameters().Length < 9 || ghostThing.GetParameters()[8].ParameterType != typeof(ThingDef)))
            {
                ghostThing = null;
            }
            Patch("3D placement preview", ghostThing, postfix: nameof(DrawGhostThing_Postfix));

            // The pointer in 3D: selection brackets on the ground, and clicking anywhere on a tall
            // thing picks it. (The selection drag box is the game's own: a plain rectangle on screen.)
            MethodInfo bracket = FindMethod(typeof(SelectionDrawer), "DrawSelectionBracketFor");
            if (bracket != null && !StartsWith(bracket, typeof(object)))
            {
                bracket = null;
            }
            Patch("selection on the ground", bracket, prefix: nameof(DrawSelectionBracketFor_Prefix));
            MethodInfo under = FindMethod(typeof(GenUI), "ThingsUnderMouse");
            if (under != null && !StartsWith(under, typeof(Vector3), typeof(float), typeof(TargetingParameters)))
            {
                under = null;
            }
            Patch("clicking on 3D shapes", under, postfix: nameof(ThingsUnderMouse_Postfix));

            // Designation icons float over what they mark (their drawing is redirected above).
            Patch("floating designation icons", FindMethod(typeof(Designation), "DesignationDraw"),
                prefix: nameof(DesignationDraw_Prefix), finalizer: nameof(Scope_Finalizer));
            // Cell designations (mine, smooth ...) are drawn in one instanced batch on the ground,
            // where they would be inside the rock; in 3D they are drawn one by one like the rest.
            Patch("floating designation icons", AccessTools.Method(typeof(DesignationManager), "DrawDesignations"),
                prefix: nameof(DrawDesignations_Prefix), finalizer: nameof(DrawDesignations_Finalizer));
            Type drawBatch = AccessTools.TypeByName("Verse.DrawBatch");
            if (drawBatch != null)
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(drawBatch))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name == "DrawMesh" && ps.Length > 1 && ps[1].ParameterType == typeof(Matrix4x4) && !m.IsStatic)
                    {
                        Patch("floating batched icons", m, prefix: nameof(DrawBatch_Prefix));
                    }
                }
            }

            // The faint screen-aligned box the game draws round a designation drag is replaced by
            // one on the ground (Interaction.DrawDesignationDrag); its measurement labels are kept.
            Patch("drag area on the ground", FindMethod(typeof(DesignationDragger), "DraggerOnGUI"),
                prefix: nameof(DraggerOnGUI_Prefix), finalizer: nameof(DraggerOnGUI_Finalizer));
            MethodInfo drawBox = AccessTools.Method(typeof(Widgets), "DrawBox", new[] { typeof(Rect), typeof(int), typeof(Texture2D) });
            Patch("drag area on the ground", drawBox, prefix: nameof(WidgetsDrawBox_Prefix));

            // Mining, deconstructing and smoothing walls aim at the block the pointer shows, not the
            // ground cell behind its top (Interaction.DesignatorMouseCell).
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignatorManager), "ProcessInputEvents"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignationDragger), "StartDrag"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(DesignationDragger), "GetCurrentBoundary"), transpiler: nameof(DesignatorCell_Transpiler));
            Patch("designators aim at blocks", AccessTools.Method(typeof(GenUI), "RenderMouseoverBracket"), prefix: nameof(MouseoverBracket_Prefix));

            // The game lays dark sheets round the outside of the map to hide whatever lies past
            // its edge. In 3D they would cover the cut through the earth below the edge.
            Type clipper = AccessTools.TypeByName("Verse.MapEdgeClipDrawer");
            Patch("earth below the map edge", clipper != null ? FindMethod(clipper, "DrawClippers") : null, prefix: nameof(DrawClippers_Prefix));

            // 1.7 draws nothing past the map edge on normal maps (only maps without the edge
            // sheets, such as orbit, do; the earth edge is not shown there, see SectionLayer_IsoRimEdge).

            Diagnostics();

            // The game's flat outlines of things seen through walls are made from the sprite work
            // skipped above, and do not fit 3D figures anyway, so they are left out in 3D.
            MethodInfo silhouettes = FindMethod(typeof(DynamicDrawManager), "DrawSilhouettes");
            if (silhouettes != null)
            {
                Patch("no flat silhouettes in 3D", silhouettes, prefix: nameof(DrawSilhouettes_Prefix));
            }

            // Parts the game draws every frame on top of a building (fire, charge bars, turret
            // heads) and the status icons over things have to move up with the raised sprite.
            Patch("moving parts of raised buildings", FindMethod(typeof(Thing), "DynamicDrawPhaseAt"),
                prefix: nameof(ThingDraw_Prefix), finalizer: nameof(Scope_Finalizer));

            Type overlays = AccessTools.TypeByName("RimWorld.OverlayDrawer");
            int overlayHooks = 0;
            if (overlays != null)
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(overlays))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name.StartsWith("Render") && ps.Length > 0 && ps[0].ParameterType == typeof(Thing)
                        && !m.IsAbstract && !m.ContainsGenericParameters)
                    {
                        bool hooked = m.Name.Contains("Forbidden") && !m.Name.Contains("Big") && !m.Name.Contains("Refuel")
                            ? Patch("forbidden icons", m, prefix: nameof(ForbiddenOverlay_Prefix))
                            : Patch("status icons", m, prefix: nameof(OverlayRender_Prefix), finalizer: nameof(Scope_Finalizer));
                        if (hooked)
                        {
                            overlayHooks++;
                        }
                    }
                }
            }
            if (overlayHooks == 0)
            {
                ModLog.Warn("[IsoRim] The game's status icon drawing was not found. Icons such as \"no power\" may be hidden inside raised buildings.");
            }

            Type renderUtility = AccessTools.TypeByName("Verse.PawnRenderUtility");
            MethodInfo aiming = renderUtility != null ? FindMethod(renderUtility, "DrawEquipmentAiming") : null;
            if (aiming != null && !StartsWith(aiming, typeof(Thing), typeof(Vector3), typeof(float)))
            {
                aiming = null;
            }
            Patch("3D weapons in hand", aiming, prefix: nameof(DrawEquipmentAiming_Prefix));

            // Wind turbines: the blades are a turning 3D model.
            Type wind = AccessTools.TypeByName("RimWorld.CompPowerPlantWind");
            Patch("wind turbine blades", wind == null ? null : AccessTools.DeclaredMethod(wind, "PostDraw"),
                prefix: nameof(WindPostDraw_Prefix), finalizer: nameof(WindPostDraw_Finalizer));

            // Walls are cut down in front of roofed rooms, so they have to be redrawn when a roof changes.
            Patch("cut-down walls keeping up with roofs", AccessTools.Method(typeof(RoofGrid), "SetRoof"),
                postfix: nameof(SetRoof_Postfix));


            // The water's colour bands reach a few cells from the shore, into the next blocks of the
            // map, which the game does not redraw for a change of terrain.
            Patch("water keeping up with the shore", AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                postfix: nameof(TerrainChanged_Postfix));

            MethodInfo movers = FindMethod(typeof(Building_Door), "DrawMovers");
            if (movers != null && !StartsWith(movers, typeof(Vector3), typeof(float)))
            {
                movers = null;
            }
            Patch("3D doors", movers, prefix: nameof(DrawMovers_Prefix));
            // Doors wider than a cell with their own models (ornate door): frame, arch and two sliding leaves (Doors.DrawWide).
            Patch("3D wide doors", AccessTools.DeclaredMethod(typeof(Building_MultiTileDoor), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MultiTileDoorDrawAt_Prefix));
            // One-cell supported doors with their own models (vac barrier, ancient blast door): frame, lintel,
            // leaves and hacking terminal (Doors.DrawSupported); the game's flat terminal is then left out.
            Patch("3D supported doors", AccessTools.DeclaredMethod(typeof(Building_SupportedDoor), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(SupportedDoorDrawAt_Prefix));
            Patch("3D supported doors", AccessTools.DeclaredMethod(typeof(CompHackable), "PostDraw"),
                prefix: nameof(HackablePostDraw_Prefix));

            // Buildings the game draws every frame (campfire, torches, caskets): their model is baked
            // into the map mesh (SectionLayer_IsoRimBodies); only the body sprite is held back, and
            // the flat sprites that would double what the model shows (an open casket's lid, the
            // fire over a torch whose model has its own flame).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Thing), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(ThingDrawAt_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompEmptyStateGraphic), "PostDraw"),
                prefix: nameof(EmptyStatePostDraw_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompFireOverlay), "PostDraw"),
                prefix: nameof(FireOverlayPostDraw_Prefix), finalizer: nameof(FireOverlayPostDraw_Finalizer));
            // A bookcase draws its books and bookends itself after its body; with the model, the
            // books are baked onto its shelves (Bookcases), rebuilt when one goes in or out.
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(BookcaseDrawAt_Prefix));
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "Notify_ItemAdded"),
                postfix: nameof(BookcaseContents_Postfix));
            Patch("3D bookcases", AccessTools.DeclaredMethod(typeof(Building_Bookcase), "Notify_ItemRemoved"),
                postfix: nameof(BookcaseContents_Postfix));
            // Flat sprites drawn every frame over contents that the 3D model replaces (gibbet cage top, dryad sphere front).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Building_GibbetCage), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(GibbetDrawAt_Prefix));
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompDryadHolder), "PostDraw"),
                prefix: nameof(DryadHolderPostDraw_Prefix));
            // Glass over contents (Glass.cs): the flat glass, top and window sprites are left out, the contents placed in the chamber.
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_MechGestator), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MechGestatorDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_GrowthVat), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(GrowthVatDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(Building_WastepackAtomizer), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(AtomizerDrawAt_Prefix));
            Patch("3D glass", AccessTools.DeclaredMethod(typeof(CompBiosculpterPod), "PostDraw"),
                prefix: nameof(BiosculpterPostDraw_Prefix));
            // Holding platform with its model: the restraint chains in 3D from its eye rings (HoldingChains.cs).
            Patch("3D holding platform chains", AccessTools.DeclaredMethod(typeof(Building_HoldingPlatform), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(HoldingPlatformDrawAt_Prefix));
            // Outfit stands with their model: head model, the apparel and weapon stood upright over it (Stands.cs).
            Patch("3D outfit stands", AccessTools.DeclaredMethod(typeof(Building_OutfitStand), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(OutfitStandDrawAt_Prefix));
            // Extra per-frame sprites the model already has as parts (the ancient stockpile exit's ladder).
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(CompDrawAdditionalGraphics), "PostDraw"),
                prefix: nameof(DrawAdditionalGraphicsPostDraw_Prefix));
            // Harvester cables run up to the model's connectors (WorkParts.cs).
            Patch("3D cables", AccessTools.DeclaredMethod(typeof(CompCableConnection), "PostDraw"),
                prefix: nameof(CableConnectionPostDraw_Prefix));
            // A new paint colour: the game redraws only things in the map mesh.
            Patch("3D models of buildings drawn every frame", AccessTools.DeclaredMethod(typeof(Thing), "Notify_ColorChanged"),
                postfix: nameof(BodyColorChanged_Postfix));

            // Parts that turn in 3D (the lightball's ball, Spinners): the game's flat mote for them is left out.
            Patch("3D turning parts", AccessTools.DeclaredMethod(typeof(Mote), "DrawAt", new[] { typeof(Vector3), typeof(bool) }),
                prefix: nameof(MoteDrawAt_Prefix));

            // Shield bubbles: a dome over the circle instead of a flat ring on the ground (ShieldDomes).
            Patch("shield domes", AccessTools.DeclaredMethod(typeof(CompProjectileInterceptor), "PostDraw"), transpiler: nameof(ShieldDraw_Transpiler));

            // Smoke and dust the game throws from a building: from the model's nozzles, standing up at their height (Emitters).
            Patch("smoke from 3D nozzles", AccessTools.Method(typeof(CompThrownFleckEmitter), "Emit"), prefix: nameof(FleckEmit_Prefix));
            Patch("smoke from 3D nozzles", AccessTools.Method(typeof(FleckStatic), "Draw", new[] { typeof(float), typeof(DrawBatch) }), prefix: nameof(FleckDraw_Prefix), finalizer: nameof(FleckDraw_Finalizer));

            // Turret guns: a 3D gun turning on its 3D base.
            Type turretTop = AccessTools.TypeByName("RimWorld.TurretTop");
            if (turretTop != null)
            {
                turretParent = AccessTools.Field(turretTop, "parentTurret");
                turretRotation = AccessTools.Property(turretTop, "CurRotation");
            }
            Patch("3D turret guns", turretTop == null || turretParent == null || turretRotation == null ? null : FindMethod(turretTop, "DrawTurret"),
                prefix: nameof(DrawTurret_Prefix));

            // The buttons in the bottom right corner of the map view.
            Patch("buttons for the 3D view and roofs", AccessTools.Method(typeof(PlaySettings), "DoPlaySettingsGlobalControls"),
                postfix: nameof(PlaySettings_Postfix));

            // In 3D the sun stands in the south at noon (IsoRim's sun lights the faces turned toward
            // the camera), so the game's shadows have to fall north. This one value feeds the
            // shadow vector (_CastVect / SunDir: building and tree shadows, animal shadows, Dynamic
            // Pawn Shadows) and the area the game draws sun shadows for.
            Patch("shadows falling away from the 3D sun", AccessTools.Method(typeof(GenCelestial), "GetLightSourceInfo", new[] { typeof(Map), typeof(GenCelestial.LightType) }),
                postfix: nameof(GetLightSourceInfo_Postfix));
            // The game builds the moving edge of a building's shadow only on its west, east and
            // south sides (its shadows never point north). In 3D the north side gets one too.
            Type sunShadows = AccessTools.TypeByName("Verse.SectionLayer_SunShadows");
            Patch("shadows falling away from the 3D sun", sunShadows == null ? null : AccessTools.DeclaredMethod(sunShadows, "Regenerate"),
                postfix: nameof(SunShadowsRegenerate_Postfix));

            // Night, lamp and indoor light per face (IsoGlow): copy the corner colours of every block
            // of the game's lighting overlay it rebuilds, and hand them to the shaders before the map
            // is drawn (after this frame's rebuilds).
            Type lightingOverlay = AccessTools.TypeByName("Verse.SectionLayer_LightingOverlay");
            Patch("night and indoor light on raised things", lightingOverlay == null ? null : AccessTools.Method(lightingOverlay, "GenerateLightingOverlay"),
                postfix: nameof(GenerateLightingOverlay_Postfix));
            Patch("night and indoor light on raised things", AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"),
                prefix: nameof(DrawMapMesh_Prefix));

            // Culling (Cull): map blocks and moving model parts wholly off screen are not drawn.
            Patch("culling", AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"), transpiler: nameof(CullDrawMapMesh_Transpiler));
            Patch("culling", AccessTools.Method(typeof(LayerSubMesh), "FinalizeMesh"), postfix: nameof(CullFinalizeMesh_Postfix));
            Patch("culling", AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"),
                prefix: nameof(CullThings_Prefix), finalizer: nameof(CullThings_Finalizer));
            Patch("culling", AccessTools.Method(typeof(Thing), "DynamicDrawPhase"), prefix: nameof(CullThing_Prefix), postfix: nameof(CullThing_Postfix));
            if (ModLog.Diag)
            {
                IsoGlow.Benchmark();
            }

            RedirectDrawCalls();

            // 1.7 port: one dev-mode line listing which hooks went in and which did not.
            ModLog.Note("[IsoRim] Hooks applied (" + hooked.Count + "): " + string.Join("; ", hooked));
            if (missed.Count > 0)
            {
                ModLog.Warn("[IsoRim] Hooks missing (" + missed.Count + "): " + string.Join("; ", missed));
            }
        }

        // __0 map, __1 subMesh, __2 rect, __3 ref firstCenterInd, __4 centered, __5 filter.
        public static void GenerateLightingOverlay_Postfix(Map __0, LayerSubMesh __1, CellRect __2, bool __4, Predicate<int> __5)
        {
            try
            {
                IsoGlow.Generated(__0, __1, __2, __4, __5);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Copying the map's light failed: " + e, 93611201);
            }
        }

        public static IEnumerable<CodeInstruction> CullDrawMapMesh_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Cull.DrawMapMeshTranspiler(instructions);
        }

        public static void CullFinalizeMesh_Postfix(MeshParts __0)
        {
            Cull.MeshFinalized(__0);
        }

        public static bool CullThing_Prefix(Thing __instance, DrawPhase phase)
        {
            return Cull.ThingPhase(__instance, phase);
        }

        public static void CullThing_Postfix()
        {
            Cull.ThingPhaseDone();
        }

        public static void CullThings_Prefix()
        {
            Cull.ThingsBegin();
            PartBatches.Begin();
        }

        public static Exception CullThings_Finalizer(Exception __exception)
        {
            Cull.ThingsEnd();
            PartBatches.Flush();
            return __exception;
        }

        public static void DrawMapMesh_Prefix()
        {
            try
            {
                IsoGlow.BeginFrame(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Handing the map's light to the shaders failed: " + e, 93611202);
            }
            try
            {
                IsoCellMap.BeginFrame(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Handing the map's cell data to the shaders failed: " + e, 93611203);
            }
        }

        // The game's sun is in the north (its shadows always point south); IsoRim's is in the south.
        // East and west already agree, so in 3D only the north-south part is mirrored.
        public static void GetLightSourceInfo_Postfix(GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
        {
            if (type == GenCelestial.LightType.Shadow && View3D.Active)
            {
                __result.vector.y = -__result.vector.y;
            }
        }

        private static readonly AccessTools.FieldRef<SectionLayer, Section> sectionOf = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        // Adds the missing north edges to a block of the game's building shadows (3D only; the
        // map is rebuilt when the 3D view is switched, so top-down keeps the game's own mesh).
        // Same layout as the game's other edges: the corners on the building stay (alpha 0), the
        // outer pair slides along the cast by the building's shadow height (alpha). Wound so it
        // faces up once slid north; slid south (the game's own sun) it would face away and be culled.
        public static void SunShadowsRegenerate_Postfix(SectionLayer __instance)
        {
            if (!View3D.Active)
            {
                return;
            }
            try
            {
                Section section = sectionOf(__instance);
                Map map = section != null ? section.map : null;
                Material mat = MatBases.SunShadow;
                if (map == null || section == null || mat == null)
                {
                    return;
                }
                LayerSubMesh sm = __instance.GetSubMesh(mat);
                if (sm == null || sm.verts.Count == 0)
                {
                    return;
                }
                Building[] grid = map.edificeGrid.InnerArray;
                CellIndices idx = map.cellIndices;
                float y = AltitudeLayer.Shadows.AltitudeFor();
                Color32 low = new Color32(0, 0, 0, 0);
                int added = 0;
                int maxX = Mathf.Min(section.botLeft.x + 17, map.Size.x);
                int maxZ = Mathf.Min(section.botLeft.z + 17, map.Size.z);
                for (int i = section.botLeft.x; i < maxX; i++)
                {
                    for (int j = section.botLeft.z; j < maxZ; j++)
                    {
                        Building b = grid[idx.CellToIndex(i, j)];
                        if (b == null || !(b.def.staticSunShadowHeight > 0f) || j >= map.Size.z - 1)
                        {
                            continue;
                        }
                        float h = b.def.staticSunShadowHeight;
                        Building north = grid[idx.CellToIndex(i, j + 1)];
                        if (north != null && north.def.staticSunShadowHeight >= h)
                        {
                            continue;
                        }
                        Color32 high = new Color32(0, 0, 0, (byte)(255f * h));
                        int n = sm.verts.Count;
                        sm.verts.Add(new Vector3(i, y, j + 1));
                        sm.verts.Add(new Vector3(i + 1, y, j + 1));
                        sm.verts.Add(new Vector3(i, y, j + 1));
                        sm.verts.Add(new Vector3(i + 1, y, j + 1));
                        sm.colors.Add(low);
                        sm.colors.Add(low);
                        sm.colors.Add(high);
                        sm.colors.Add(high);
                        sm.tris.Add(n);
                        sm.tris.Add(n + 2);
                        sm.tris.Add(n + 3);
                        sm.tris.Add(n);
                        sm.tris.Add(n + 3);
                        sm.tris.Add(n + 1);
                        added++;
                    }
                }
                if (added > 0)
                {
                    sm.finalized = false;
                    sm.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
                    sm.mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Adding the north edges of building shadows failed: " + e, 93611171);
            }
        }

        private static readonly List<string> hooked = new List<string>();
        private static readonly List<string> missed = new List<string>();

        private static FieldInfo turretParent;
        private static PropertyInfo turretRotation;

        // A turret drawing its gun. If the turret has a gun model it is drawn turned to the way the
        // gun is aiming, on top of the 3D base, and the game's flat gun is not drawn. As the game's
        // own: a spraying gun (foam turret) sweeps along its spray (AimAngleOverride), the gun kicks
        // back by the recoil offset, and a gun whose sprite shows it loaded (rocketswarm launcher,
        // full) shows its loaded model.
        public static bool DrawTurret_Prefix(object __instance, Vector3 __1)
        {
            if (!View3D.Ready || !IsoRimMod.Settings.models3D || __instance == null)
            {
                return true;
            }
            try
            {
                Building_Turret turret = turretParent.GetValue(__instance) as Building_Turret;
                ModelEntry entry;
                if (turret == null || !ModelLibrary.TryGet(turret.def, out entry) || entry.top == null)
                {
                    return true;
                }
                Verb verb = turret.CurrentEffectiveVerb;
                float? over = verb != null ? verb.AimAngleOverride : null;
                float aim = over ?? (float)turretRotation.GetValue(__instance, null);
                Model gun = entry.top;
                if (entry.topLoaded != null && turret.def.building != null && turret.TurretTopMaterial != turret.def.building.turretTopMat)
                {
                    gun = entry.topLoaded;
                }
                Vector3 at = turret.DrawPos;
                Vector3 fit = entry.Fit(turret.def) * entry.def.scale;
                DynamicModels.Draw(gun, new Vector3(at.x + __1.x, entry.def.topHeight * fit.y, at.z + __1.z), aim - 90f, fit, R3DTextures.ModelTint(turret), R3DTextures.ModelSwatch(turret));
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a turret's gun failed: " + e, 93611023);
                return true;
            }
        }

        // Adds the 3D view switch, and the roof switch while the 3D view is on, to the row of
        // buttons in the bottom right corner.
        public static void PlaySettings_Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                bool view = View3D.Active;
                row.ToggleableIcon(ref view, R3DIcons.View3D, "IsoRim: 3D view (" + KeyLabel(R3DKeyDefOf.R3D_ToggleView) + ")", SoundDefOf.Mouseover_ButtonToggle);
                if (view != View3D.Active)
                {
                    View3D.Active = view;
                }
                if (View3D.Active)
                {
                    bool roofs = IsoRimMod.Settings.showRoofs;
                    row.ToggleableIcon(ref roofs, R3DIcons.Roofs, "IsoRim: show roofs (" + KeyLabel(R3DKeyDefOf.R3D_ToggleRoofs) + ")", SoundDefOf.Mouseover_ButtonToggle);
                    if (roofs != IsoRimMod.Settings.showRoofs)
                    {
                        Roofs.Set(roofs);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the corner buttons failed: " + e, 93611024);
            }
        }

        private static string KeyLabel(KeyBindingDef key)
        {
            if (key == null || key.MainKey == KeyCode.None)
            {
                return "no key set";
            }
            return key.MainKeyLabel;
        }

        private static bool Patch(string feature, MethodBase target, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
        {
            if (target == null)
            {
                ModLog.Warn("[IsoRim] The game method behind \"" + feature + "\" was not found. That part of the 3D view is off.");
                missed.Add(feature + " (not found)");
                return false;
            }
            try
            {
                harmony.Patch(target, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer));
                hooked.Add(target.DeclaringType?.Name + "." + target.Name);
                return true;
            }
            catch (Exception e)
            {
                missed.Add(target.DeclaringType?.Name + "." + target.Name + " (" + e.Message + ")");
                ModLog.Warn("[IsoRim] Could not hook " + target.DeclaringType + "." + target.Name + " for \"" + feature + "\": " + e.Message);
                return false;
            }
        }

        // Looks a method up by name without throwing if the game has several with that name.
        private static MethodInfo FindMethod(Type type, string name)
        {
            try
            {
                return AccessTools.Method(type, name);
            }
            catch (Exception)
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                {
                    if (m.Name == name)
                    {
                        return m;
                    }
                }
                return null;
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(Patches), name));
        }

        private static bool StartsWith(MethodInfo m, params Type[] types)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length < types.Length)
            {
                return false;
            }
            for (int i = 0; i < types.Length; i++)
            {
                if (ps[i].ParameterType != types[i])
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- camera

        public static void CameraUpdate_Postfix(CameraDriver __instance)
        {
            try
            {
                ApplyDrag(__instance);
                View3D.Tick();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Camera update failed: " + e, 93611010);
            }
        }

        // The direction asked for by the movement keys and by the pointer at a screen edge:
        // x is "right", y is "up the screen".
        public static void DollyVect_Postfix(ref Vector2 __result)
        {
            if (View3D.Ready && __result != Vector2.zero)
            {
                __result = View3D.ScreenMoveToGround(__result, false);
            }
        }

        // Dragging the map with the middle mouse button. The game's own drag moves the camera
        // along the map's axes and keeps some speed after the button is let go, which goes wrong
        // with a tilted, turned camera (it used to run away into the grey beyond the map). While
        // the 3D view is on the mod does it instead: the point of ground grabbed stays under the
        // pointer, and the camera stops as soon as the button is let go.
        public static void CameraUpdate_Prefix(CameraDriver __instance)
        {
            try
            {
                if (!View3D.Ready)
                {
                    dragging = false;
                    return;
                }
                bool held = Input.GetMouseButton(2);
                if (!held)
                {
                    if (dragging)
                    {
                        dragging = false;
                        StopGameDrag(__instance);
                    }
                    return;
                }
                Camera cam = Find.Camera;
                if (cam == null)
                {
                    return;
                }
                if (!dragging)
                {
                    dragging = true;
                    dragAnchor = View3D.ScreenToGround(Input.mousePosition);
                }
                dragStartPos = cam.transform.position;
                StopGameDrag(__instance);
            }
            catch (Exception e)
            {
                dragging = false;
                Log.ErrorOnce("[IsoRim] Dragging the map failed: " + e, 93611018);
            }
        }

        private static void StopGameDrag(CameraDriver driver)
        {
            for (int i = 0; i < dragFields.Count; i++)
            {
                dragFields[i].SetValue(driver, Vector2.zero);
            }
        }

        // Called after the game's camera update: moves the camera so the grabbed ground point
        // is back under the pointer.
        private static void ApplyDrag(CameraDriver driver)
        {
            if (!dragging || !View3D.Ready || driver == null || Find.CurrentMap == null)
            {
                return;
            }
            Vector3 now = View3D.ScreenToGround(Input.mousePosition);
            Vector3 target = dragStartPos + (dragAnchor - now);
            if (float.IsNaN(target.x) || float.IsNaN(target.z) || float.IsInfinity(target.x) || float.IsInfinity(target.z))
            {
                return;
            }
            IntVec3 size = Find.CurrentMap.Size;
            target.x = Mathf.Clamp(target.x, 0f, size.x);
            target.z = Mathf.Clamp(target.z, 0f, size.z);
            driver.JumpToCurrentMapLoc(new Vector3(target.x, 0f, target.z));
        }

        // A selection box dragged on screen. The game looks for things in the block of cells
        // between two of the box's corners, which only covers the box while north is up and the
        // camera looks straight down. In 3D the block reaches all four corners on the ground ...
        public static void GetMapRect_Postfix(Rect __0, ref CellRect __result)
        {
            if (!View3D.Ready)
            {
                return;
            }
            try
            {
                __result = BoxGroundRect(__0, 0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Working out a selection box failed: " + e, 93611019);
            }
        }

        // The cells under a screen box (interface coordinates), widened by margin cells.
        private static CellRect BoxGroundRect(Rect box, int margin)
        {
            float scale = Prefs.UIScale;
            float h = Verse.UI.screenHeight;
            Vector3 a = View3D.ScreenToGround(new Vector2(box.xMin, h - box.yMin) * scale);
            Vector3 b = View3D.ScreenToGround(new Vector2(box.xMax, h - box.yMin) * scale);
            Vector3 c = View3D.ScreenToGround(new Vector2(box.xMin, h - box.yMax) * scale);
            Vector3 d = View3D.ScreenToGround(new Vector2(box.xMax, h - box.yMax) * scale);
            CellRect r = new CellRect();
            r.minX = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))) - margin;
            r.maxX = Mathf.FloorToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))) + margin;
            r.minZ = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))) - margin;
            r.maxZ = Mathf.FloorToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))) + margin;
            return r;
        }

        // ... and what is found is what shows inside the box on screen: a thing counts when the box
        // catches any part of it, standing up from its cell (a colonist's head is enough), as the
        // flat game counts a thing when the box catches any part of its cell.
        public static IEnumerable<Thing> DragSelect_Postfix(IEnumerable<Thing> __result, Rect __0)
        {
            if (__result == null || !View3D.Ready || Find.CurrentMap == null)
            {
                return __result;
            }
            return ThingsShowingInBox(Find.CurrentMap, __0);
        }

        private static readonly HashSet<Thing> boxYielded = new HashSet<Thing>();

        private static IEnumerable<Thing> ThingsShowingInBox(Map map, Rect box)
        {
            Rect grown = GrownBox(box);
            // Tall things a few cells nearer the camera than the box can still reach up into it.
            int margin = Mathf.CeilToInt(2.5f / Mathf.Tan(View3D.Pitch * Mathf.Deg2Rad)) + 1;
            List<Thing> found = new List<Thing>();
            boxYielded.Clear();
            foreach (IntVec3 c in BoxGroundRect(box, margin))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAt(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!boxYielded.Contains(t) && ThingSelectionUtility.SelectableByMapClick(t) && !t.def.neverMultiSelect && ShowsInBox(t, grown))
                    {
                        boxYielded.Add(t);
                        found.Add(t);
                    }
                }
            }
            boxYielded.Clear();
            return found;
        }

        // The box (interface coordinates) grown by half a cell, so a thing counts when the box
        // only catches part of it.
        private static Rect GrownBox(Rect box)
        {
            float slack = Screen.height / (2f * Mathf.Max(1f, Find.Camera.orthographicSize)) * 0.5f / Prefs.UIScale;
            return Rect.MinMaxRect(
                Mathf.Min(box.xMin, box.xMax) - slack, Mathf.Min(box.yMin, box.yMax) - slack,
                Mathf.Max(box.xMin, box.xMax) + slack, Mathf.Max(box.yMin, box.yMax) + slack);
        }

        // Whether the upright line from a point on the ground to the height top shows inside the
        // (grown) box. Upright lines stay upright on screen, so it is a span in y at one x.
        private static bool ShowsInBox(Vector3 ground, float top, Rect grown)
        {
            float scale = Prefs.UIScale;
            float h = Verse.UI.screenHeight;
            Vector2 g = View3D.WorldToScreen(new Vector3(ground.x, 0f, ground.z));
            float x = g.x / scale;
            if (x < grown.xMin || x > grown.xMax)
            {
                return false;
            }
            float yLow = h - g.y / scale;
            float yHigh = top > 0f ? h - View3D.WorldToScreen(new Vector3(ground.x, top, ground.z)).y / scale : yLow;
            return Mathf.Max(yLow, yHigh) >= grown.yMin && Mathf.Min(yLow, yHigh) <= grown.yMax;
        }

        private static bool ShowsInBox(Thing t, Rect grown)
        {
            float top = Interaction.PickHeight(t);
            if (t is Pawn)
            {
                return ShowsInBox(t.DrawPos, top, grown);
            }
            foreach (IntVec3 c in t.OccupiedRect())
            {
                if (ShowsInBox(c.ToVector3Shifted(), top, grown))
                {
                    return true;
                }
            }
            return false;
        }

        // Zones and plans lie on the ground: they count when the middle of one of their cells shows
        // inside the box (the block of cells round a turned box reaches past it).
        public static IEnumerable<Zone> DragSelectZones_Postfix(IEnumerable<Zone> __result, Rect __0)
        {
            if (__result == null || !View3D.Ready || Find.CurrentMap == null)
            {
                return __result;
            }
            Rect grown = GrownBox(__0);
            CellRect r = BoxGroundRect(__0, 0);
            return __result.Where(z => z.Cells.Any(c => r.Contains(c) && ShowsInBox(c.ToVector3Shifted(), 0f, grown))).ToList();
        }

        public static IEnumerable<Plan> DragSelectPlans_Postfix(IEnumerable<Plan> __result, Rect __0)
        {
            if (__result == null || !View3D.Ready || Find.CurrentMap == null)
            {
                return __result;
            }
            Rect grown = GrownBox(__0);
            CellRect r = BoxGroundRect(__0, 0);
            return __result.Where(p => p.Cells.Any(c => r.Contains(c) && ShowsInBox(c.ToVector3Shifted(), 0f, grown))).ToList();
        }

        // Where a thing's name label goes. The game puts it a little to the south of the thing,
        // which is "below" only while north is up. __1 is that distance (negative).
        public static bool LabelDrawPosFor_Prefix(Thing __0, float __1, ref Vector2 __result)
        {
            if (!View3D.Ready || View3D.Yaw == 0f || __0 == null)
            {
                return true;
            }
            try
            {
                Vector3 onScreen = Find.Camera.WorldToScreenPoint(__0.DrawPos + View3D.GroundForward * __1);
                float scale = Prefs.UIScale;
                __result = new Vector2(onScreen.x / scale, Verse.UI.screenHeight - onScreen.y / scale);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing a name label failed: " + e, 93611193);
                return true;
            }
        }

        // The game assumes the camera looks straight down when it turns a mouse position into a map cell.
        public static bool UIToMapPosition_Prefix(Vector2 __0, ref Vector3 __result)
        {
            if (!View3D.Ready)
            {
                return true;
            }
            __result = View3D.ScreenToGround(__0 * Prefs.UIScale);
            return false;
        }

        // Same for the area it thinks is on screen: tilted or turned, the camera sees a different part of the map.
        public static void CurrentViewRect_Postfix(ref CellRect __result)
        {
            if (View3D.Ready)
            {
                __result = View3D.GroundRect();
            }
        }

        // ---------------------------------------------------------------- static map geometry

        // Called by the game once for each non-moving thing when it rebuilds a block of the map.
        // Decides what shape the thing takes; the sprite quads it then prints are reshaped in
        // PrintPlane_Postfix.
        public static bool TakePrintFrom_Prefix(SectionLayer __instance, Thing __0)
        {
            PrintContext.Clear();
            if (!View3D.Active || __0 == null || __0.def == null)
            {
                return true;
            }

            try
            {
                // Blueprints and frames: a see-through ghost of the finished thing. A frame keeps
                // the game's sprite of the work under way beneath it.
                if (Ghosts.Target(__0) != null)
                {
                    if (Ghosts.Print(__instance, __0))
                    {
                        return __0 is Frame;
                    }
                    return true;
                }

                if (__0 is Plant)
                {
                    float rim = Forms.BaseUnder(__0, false);
                    ThingForm plantForm = Forms.For(__0.def);
                    if (plantForm.form == Form3D.Model)
                    {
                        if (SectionLayer_IsoRimTrees.Handles(__0))
                        {
                            return false;   // drawn in the mod's own tree layer
                        }
                        if (ModelBaker.Bake(__instance, __0, plantForm.model, rim))
                        {
                            return false;   // a 3D plant replaces the sprite
                        }
                    }
                    if (IsoRimMod.Settings.uprightPlants)
                    {
                        PrintContext.mode = PrintMode.Upright;
                        PrintContext.baseHeight = rim;
                    }
                    else if (rim > 0f)
                    {
                        PrintContext.mode = PrintMode.Lift;
                        PrintContext.height = rim;
                    }
                    return true;
                }

                ThingForm form = Forms.For(__0.def);
                bool item = __0.def.category == ThingCategory.Item;
                float resting = item ? Forms.BaseUnder(__0, true) : 0f;

                switch (form.form)
                {
                    case Form3D.Model:
                        if (ModelBaker.Bake(__instance, __0, form.model, resting))
                        {
                            return false;   // the model replaces the sprite
                        }
                        break;

                    case Form3D.Block:
                    {
                        if (SectionLayer_IsoRimWalls.Handles(__0))
                        {
                            return false;   // drawn in the mod's own wall layer
                        }
                        // A wall hiding a room from the camera is drawn part height.
                        float blockHeight = WallBoxes.HeightFor(__0, form.height);
                        IsoCellMap.Dirty(__0.Map, __0.OccupiedRect());   // the fog lid lies on its top
                        GroundShadows.AroundBlock(__instance, __0);
                        if (IsoRimMod.Settings.texturedWalls && WallBoxes.PrintBlock(__instance, __0, blockHeight))
                        {
                            return false;   // drawn from the mod's own textures; the game's sprite is not used
                        }
                        PrintContext.mode = PrintMode.Block;
                        PrintContext.height = blockHeight;
                        break;
                    }

                    case Form3D.Extruded:
                        PrintContext.mode = PrintMode.Extrude;
                        PrintContext.baseHeight = resting;
                        PrintContext.height = resting + form.height;
                        break;

                    default:
                        if (resting > 0f)
                        {
                            PrintContext.mode = PrintMode.Lift;
                            PrintContext.height = resting;
                        }
                        break;
                }
                PrintContext.thing = __0;
                PrintContext.layer = __instance;
            }
            catch (Exception e)
            {
                PrintContext.Clear();
                Log.ErrorOnce("[IsoRim] Building 3D geometry failed: " + e, 93611011);
            }
            return true;
        }

        public static void TakePrintFrom_Finalizer()
        {
            // A wall that drew no top sprite still gets plain sides.
            if (PrintContext.mode == PrintMode.Block && !PrintContext.done && PrintContext.thing != null)
            {
                try
                {
                    WallBoxes.PrintPlainSides(PrintContext.layer, PrintContext.thing, PrintContext.height);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Building wall sides failed: " + e, 93611013);
                }
            }
            PrintContext.Clear();
        }

        // Runs after the game adds one flat sprite quad to a block's mesh.
        // __1 is the quad's centre, __2 its size, __3 its material and __4 its rotation.
        public static void PrintPlane_Postfix(MapDrawLayer __0, Vector3 __1, Vector2 __2, Material __3, float __4)
        {
            if (PrintContext.mode == PrintMode.None || !(__0 is SectionLayer layer))
            {
                return;
            }
            try
            {
                PlaneHandler.Handle(layer, __1, __2, __3, __4);
            }
            catch (Exception e)
            {
                PrintContext.Clear();
                Log.ErrorOnce("[IsoRim] Reshaping a sprite failed: " + e, 93611014);
            }
        }

        // The lighting overlay and the fog of war are flat sheets. The lighting is pulled in front
        // of everything so it also darkens upright things; the fog is drawn as a lid on the tops of
        // what is under it (lifted in its shader, IsoCellMap) so unexplored areas read as solid.
        public static bool DrawLayer_Prefix(MapDrawLayer __instance)
        {
            return DrawLayer_Section(__instance as SectionLayer);
        }

        private static bool DrawLayer_Section(SectionLayer __instance)
        {
            if (__instance == null)
            {
                return true;
            }
            Type type = __instance.GetType();
            int kind;
            if (!layerKinds.TryGetValue(type, out kind))
            {
                string name = type.Name;
                kind = name.Contains("LightingOverlay") ? 1 : (name.Contains("FogOfWar") ? 2 : (name == "SectionLayer_BuildingsDamage" ? 3 : 0));
                layerKinds[type] = kind;
            }
            IsoRimSettings st = IsoRimMod.Settings;

            // The fog lies on the tops of the rock and walls under it: the game's mesh drawn with
            // the lid material, whose shader lifts it. Flat (the game's own drawing) with the 3D
            // view or the setting off.
            if (kind == 2 && (!View3D.Ready || !st.fogLid))
            {
                return true;
            }
            if (!View3D.Ready)
            {
                return true;
            }
            if (kind == 0)
            {
                // IsoRim's faces that carry their own night and indoor light get a stencil mark, so
                // the pulled overlay below skips them.
                if (__instance.Visible)
                {
                    IsoGlow.DrawLayerMarks(__instance);
                }
                return true;
            }
            if (kind == 3)
            {
                return !st.damage3D;   // the mod's own marks are drawn on the 3D shapes instead
            }
            if (kind == 2)
            {
                if (!__instance.Visible)
                {
                    return false;
                }
                List<LayerSubMesh> fogSubs = __instance.subMeshes;
                for (int i = 0; i < fogSubs.Count; i++)
                {
                    LayerSubMesh sm = fogSubs[i];
                    if (sm.finalized && !sm.disabled)
                    {
                        Material lid = sm.material == MatBases.FogOfWar ? IsoCellMap.FogFor(sm.material) : null;
                        Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, lid ?? sm.material, sm.renderLayer);
                    }
                }
                return false;
            }
            if (!st.lightingFix)
            {
                return true;
            }
            if (!__instance.Visible)
            {
                return false;
            }

            // The overlay is drawn with a copy of the game's material that skips pixels of IsoRim
            // faces lit by their own cell (stencil bit 32, see IsoGlow).
            Matrix4x4 m = View3D.LightingMatrix;
            List<LayerSubMesh> subs = __instance.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled)
                {
                    Material mat = sm.material == MatBases.LightOverlay ? IsoGlow.OverlayFor(sm.material) : sm.material;
                    Graphics.DrawMesh(sm.mesh, m, mat, sm.renderLayer);   // 1.7 vanilla passes renderLayer
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- pawns

        // While a pawn is being drawn, DrawHook stands every piece of it up around its feet.
        // An animal that has a 3D model is drawn as that instead.
        // __0 is the position the pawn is drawn at.
        // Returns false (the game's own drawing of the pawn is skipped altogether) when a 3D
        // model has been drawn in its place: the game's sprite pipeline for a person is costly,
        // and skipping it rather than throwing its output away is most of what keeps a big
        // colony fast. Portraits and other off-map drawing are left to the game.
        public static bool RenderPawnAt_Prefix(PawnRenderer __instance, Vector3 __0)
        {
            DrawHook.active = false;
            DrawHook.billboard = false;
            DrawHook.skip = false;
            pawnUpright = false;
            pawnFigure = false;
            if (!View3D.Ready)
            {
                return true;
            }
            Camera current = Camera.current;
            if (current != null && current != Find.Camera)
            {
                return true;   // a portrait or some other camera: draw as usual
            }

            try
            {
                IsoRimSettings st = IsoRimMod.Settings;
                Pawn pawn = pawnOfRenderer != null ? pawnOfRenderer(__instance) : null;
                float spriteHeight = 1.5f;

                if (pawn != null && st.animals3D && st.models3D && pawn.RaceProps != null && !pawn.RaceProps.Humanlike
                    && Animals.Draw(pawn, __0))
                {
                    return false;
                }

                if (pawn != null && st.pawns3D && st.models3D && pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    bool standing;
                    if (People.Draw(pawn, __0, out pawnHandHeight, out pawnFacing, out standing))
                    {
                        if (standing)
                        {
                            DrawHeldWeapon(pawn, __0, pawnHandHeight, pawnFacing);
                        }
                        return false;
                    }
                }

                if (pawn != null)
                {
                    if (pawn.Dead || pawn.GetPosture() != PawnPosture.Standing)
                    {
                        // Lying down stays flat, but is raised onto the bed (or the couch of a building
                        // that shows who is inside, such as the gene extractor) if it has height.
                        Building_Bed bed = pawn.Dead ? null : pawn.CurrentBed();
                        Building holder = pawn.Dead ? null : Glass.HolderOf(pawn);
                        float bedTop = bed != null ? Forms.HeightOf(bed) : (holder != null ? Forms.HeightOf(holder) : 0f);
                        Building_GibbetCage gibbet = pawn.Dead ? People.GibbetOf(pawn) : null;
                        if (gibbet != null)
                        {
                            bedTop = Forms.HeightOf(gibbet);   // on the cage floor
                        }
                        if (bedTop > 0f)
                        {
                            DrawHook.xform = View3D.LiftMatrix(bedTop);
                            DrawHook.active = true;
                        }
                        return true;
                    }

                    if (!pawn.RaceProps.Humanlike && pawn.ageTracker != null)
                    {
                        PawnKindLifeStage stage = pawn.ageTracker.CurKindLifeStage;
                        if (stage != null && stage.bodyGraphicData != null)
                        {
                            spriteHeight = stage.bodyGraphicData.drawSize.y;
                        }
                    }
                }

                if (!st.uprightPawns)
                {
                    return true;
                }

                // The feet are where the bottom of the sprite's figure is on the flat sprite. The
                // sprite is stood up about that point, then slid so the feet come down on the
                // side of the pawn's spot nearest the camera, whichever way the camera faces.
                Vector3 pivot = __0;
                pivot.z -= spriteHeight * st.feetOffset;
                Vector3 feet = __0 - View3D.GroundForward * (spriteHeight * st.feetOffset);
                DrawHook.xform = Matrix4x4.Translate(new Vector3(feet.x - pivot.x, 0f, feet.z - pivot.z)) * View3D.UprightMatrix(pivot);
                DrawHook.active = true;
                pawnUpright = true;
                pawnFeet = feet;
                pawnSize = spriteHeight / 1.5f;
            }
            catch (Exception e)
            {
                DrawHook.active = false;
            DrawHook.billboard = false;
                Log.ErrorOnce("[IsoRim] Standing a pawn upright failed: " + e, 93611012);
            }
            return true;
        }

        // True for a pawn that will certainly be drawn as a 3D model while the view is on.
        public static bool DrawnAsModel(Pawn pawn)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (pawn == null || !View3D.Ready || !st.models3D || pawn.RaceProps == null || pawn.def == null)
            {
                return false;
            }
            if (pawn.RaceProps.Humanlike)
            {
                return st.pawns3D && pawn.def.defName == "Human" && pawn.story != null && !People.Hidden(pawn);
            }
            ModelEntry entry;
            return st.animals3D && ModelLibrary.TryGet(pawn.def, out entry) && entry.def.animal && entry.HasModel;
        }

        public static void SpawnSetup_Postfix(Thing __instance)
        {
            NoteShapeChange(__instance, __instance != null ? __instance.Map : null);
        }

        public static void DeSpawn_Prefix(Thing __instance)
        {
            NoteShapeChange(__instance, __instance != null ? __instance.MapHeld : null);
        }

        private static void NoteShapeChange(Thing t, Map map)
        {
            if (t == null || map == null || t.def == null || !View3D.Active || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                if (t.def.category == ThingCategory.Building)
                {
                    IsoCellMap.Dirty(map, t.OccupiedRect());   // the fog lid lies on what stands there
                    if (t is Building_Door && IsoRimMod.Settings.cutRooms && ShaderCut.On)
                    {
                        IsoCellMap.Dirty(map, CellRect.CenteredOn(t.Position, WallBoxes.RockReach + 1));   // (shader cut-away)
                    }
                    else if (t is Building_Door && IsoRimMod.Settings.cutRooms)
                    {
                        // A door makes (or unmakes) a room: the walls and rock in front of it may change.
                        MapSections.QueueAround(map, t.Position, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                    }
                    if (SectionLayer_IsoRimBodies.Handles(t))
                    {
                        // The game does not redraw the map mesh for things it draws every frame.
                        MapSections.QueueAround(map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                    }
                    if (Forms.For(t.def).form == Form3D.Block)
                    {
                        // Its own block and the ones beside it: a neighbour's side shows or hides.
                        MapSections.QueueAround(map, t.Position, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                        if (IsoRimMod.Settings.showRoofs)
                        {
                            MapSections.QueueAround(map, t.Position, SectionLayer_IsoRimRoofs.Reach, typeof(SectionLayer_IsoRimRoofs), true);
                        }
                    }
                }
                else if (t is Plant && SectionLayer_IsoRimTrees.Handles(t))
                {
                    MapSections.QueueAround(map, t.Position, 0, typeof(SectionLayer_IsoRimTrees), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Noting a change to the map failed: " + e, 93611121);
            }
        }

        public static void DrawGhostThing_Postfix(IntVec3 __0, Rot4 __1, ThingDef __2, Color __4, ThingDef __8)
        {
            if (!View3D.Ready)
            {
                return;
            }
            try
            {
                // The style the designator places (a Morbid altar): its own model, as the game's ghost sprite.
                Designator_Place place = Find.DesignatorManager != null ? Find.DesignatorManager.SelectedDesignator as Designator_Place : null;
                Ghosts.DrawPreview(__2, __0, __1, __4, __8, place != null && place.PlacingDef == __2 ? place.ThingStyleDefForPreview : null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the placement preview failed: " + e, 93611131);
            }
        }

        private static bool inDraggerGUI;

        public static void DraggerOnGUI_Prefix()
        {
            inDraggerGUI = true;
        }

        public static void DraggerOnGUI_Finalizer()
        {
            inDraggerGUI = false;
        }

        public static bool WidgetsDrawBox_Prefix()
        {
            return !(inDraggerGUI && View3D.Ready);
        }

        public static bool DrawSelectionBracketFor_Prefix(object __0)
        {
            Thing t = __0 as Thing;
            if (!View3D.Ready || t == null || !t.Spawned || t.Map != Find.CurrentMap)
            {
                return true;
            }
            try
            {
                Interaction.Bracket(t);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a selection bracket failed: " + e, 93611161);
                return true;
            }
        }

        // Puts whatever 3D shape is under the pointer first in the list of things clicked on.
        public static void ThingsUnderMouse_Postfix(TargetingParameters __2, ref List<Thing> __result)
        {
            if (!View3D.Ready || __result == null)
            {
                return;
            }
            try
            {
                List<Thing> hits = Interaction.PickUnderPointer(Find.CurrentMap);
                for (int i = hits.Count - 1; i >= 0; i--)
                {
                    Thing t = hits[i];
                    if (__2 != null && !__2.CanTarget(new TargetInfo(t)))
                    {
                        continue;
                    }
                    __result.Remove(t);
                    __result.Insert(0, t);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Picking a 3D shape under the pointer failed: " + e, 93611162);
            }
        }

        // With the -isorimdiag switch (see ModLog.Diag), writes what the 3D view depends on to the
        // log, so a report from a new game version shows straight away what has changed.
        private static void Diagnostics()
        {
            if (!ModLog.Diag)
            {
                return;
            }
            try
            {
                Material solid = R3DMaterials.Solid;
                Shader cutout = ShaderDatabase.Cutout;
                ModLog.Note("[IsoRim] Diagnostics: game " + VersionControl.CurrentVersionStringWithRev
                    + " | solid shader " + (solid != null && solid.shader != null ? solid.shader.name : "MISSING")
                    + " (has _Color " + (solid != null && solid.HasProperty("_Color")) + ")"
                    + " | cutout shader " + (cutout != null ? cutout.name : "MISSING")
                    + " | game shadow material " + (R3DMaterials.GameShadow != null ? R3DMaterials.GameShadow.shader.name : "MISSING"));
                List<string> layers = new List<string>();
                foreach (Type t in typeof(SectionLayer).AllSubclassesNonAbstract())
                {
                    layers.Add(t.Name);
                }
                ModLog.Note("[IsoRim] Diagnostics: map layers: " + string.Join(", ", layers));
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Diagnostics failed: " + e.Message);
            }
        }

        public static bool DrawClippers_Prefix()
        {
            return !(View3D.Ready && IsoRimMod.Settings.mapEdge3D);
        }

        public static bool DrawSilhouettes_Prefix()
        {
            return !View3D.Ready;
        }

        public static bool PreRender_Prefix(PawnRenderer __instance)
        {
            try
            {
                // Only people: the game still uses an animal's prepared sprite in a few places
                // the mod does not take over (the sprite then came out garbled when the animal
                // turned), and animals are cheap to prepare anyway.
                Pawn pawn = pawnOfRenderer != null ? pawnOfRenderer(__instance) : null;
                if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
                {
                    return true;
                }
                return !DrawnAsModel(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Skipping sprite work for a 3D pawn failed: " + e, 93611190);
                return true;
            }
        }

        // The weapon of a 3D figure, drawn here because the game's own drawing (which would
        // otherwise do it) is skipped. Shown when the person is drafted or aiming, pointed at
        // what they are aiming at, otherwise the way they face.
        private static void DrawHeldWeapon(Pawn pawn, Vector3 feet, float handHeight, float facing)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!st.heldWeapons3D || pawn.equipment == null || pawn.equipment.Primary == null)
            {
                return;
            }
            Thing weapon = pawn.equipment.Primary;
            float aim = facing;
            bool aiming = false;
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy != null && busy.focusTarg.IsValid)
            {
                Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
                to -= pawn.DrawPos;
                if (to.x * to.x + to.z * to.z > 0.001f)
                {
                    aim = to.AngleFlat();
                    aiming = true;
                }
            }
            if (!aiming && !pawn.Drafted)
            {
                return;
            }
            float artYaw = aim - 90f;
            Vector3 artDir = Quaternion.AngleAxis(artYaw, Vector3.up) * Vector3.right;
            if (st.itemsFromArt && SpriteSolids.DrawHeld(weapon,
                new Vector3(feet.x + artDir.x * 0.30f, handHeight, feet.z + artDir.z * 0.30f), aim))
            {
                return;
            }
            ModelEntry entry;
            if (!ModelLibrary.TryGet(weapon.def, out entry) || !entry.HasModel)
            {
                return;
            }
            float yaw = aim - 90f;
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right;
            Vector3 hand = new Vector3(0f, handHeight, 0f) + dir * 0.30f;
            DynamicModels.Draw(entry.Pick(weapon), new Vector3(feet.x + hand.x, hand.y, feet.z + hand.z), yaw, Vector3.one * entry.def.scale,
                weapon.DrawColor, R3DTextures.StuffSwatch(weapon));
        }

        public static void RenderPawnAt_Finalizer()
        {
            DrawHook.active = false;
            DrawHook.billboard = false;
            DrawHook.skip = false;
            pawnFigure = false;
            pawnUpright = false;
        }

        // The weapon in a standing pawn's hands. __0 is the weapon and __2 the direction it
        // points, in degrees clockwise from north. If the weapon has a model it is drawn as a
        // real object lying level and pointing that way, at the place on the pawn's sprite
        // where its hands are; otherwise the game draws its usual sprite.
        public static bool DrawEquipmentAiming_Prefix(Thing __0, float __2)
        {
            if ((!pawnUpright && !pawnFigure) || __0 == null || !View3D.Ready)
            {
                return true;
            }
            try
            {
                IsoRimSettings st = IsoRimMod.Settings;
                if (!st.heldWeapons3D)
                {
                    return true;
                }
                float yaw = __2 - 90f;
                Vector3 aim = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right;
                // A flat sprite holds it part of the way up the sprite; a 3D figure at its side.
                Vector3 hand = pawnFigure
                    ? new Vector3(0f, pawnHandHeight, 0f) + aim * 0.30f
                    : View3D.CardUp * (HandHeight * pawnSize) + aim * HandReach;
                Vector3 position = new Vector3(pawnFeet.x + hand.x, hand.y, pawnFeet.z + hand.z);
                if (st.itemsFromArt && SpriteSolids.DrawHeld(__0, position, __2))
                {
                    return false;
                }
                ModelEntry entry;
                if (!st.models3D || !ModelLibrary.TryGet(__0.def, out entry) || !entry.HasModel)
                {
                    return true;
                }
                DynamicModels.Draw(entry.Pick(__0), position, yaw, Vector3.one * entry.def.scale, __0.DrawColor, R3DTextures.StuffSwatch(__0));
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a held weapon failed: " + e, 93611016);
                return true;
            }
        }

        // A wind turbine drawing its blades. If the turbine has a 3D model, a turning 3D rotor is
        // drawn and the game's flat blades are thrown away.
        public static void WindPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            if (!View3D.Ready)
            {
                return;
            }
            try
            {
                if (Turbines.Draw(__instance))
                {
                    __state = true;
                    DrawHook.skip = true;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wind turbine's blades failed: " + e, 93611021);
            }
        }

        public static void WindPostDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                DrawHook.skip = false;
            }
        }

        private static readonly AccessTools.FieldRef<TerrainGrid, Map> terrainMap = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");


        // The terrain at __0 changed from __1 to __2.
        public static void TerrainChanged_Postfix(TerrainGrid __instance, IntVec3 __0, TerrainDef __1, TerrainDef __2)
        {
            if (!View3D.Ready || !IsoRimMod.Settings.waterStyle || !(Ground.IsWater(__1) || Ground.IsWater(__2)))
            {
                return;
            }
            MapSections.QueueAround(terrainMap(__instance), __0, SectionLayer_IsoRimWater.Reach, typeof(SectionLayer_IsoRimWater));
        }

        // A roof was added or removed at __0.
        public static void SetRoof_Postfix(RoofGrid __instance, IntVec3 __0)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            if (!View3D.Active || (!st.cutRooms && !st.showRoofs) || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i].roofGrid == __instance)
                    {
                        if (st.cutRooms && ShaderCut.On)
                        {
                            IsoCellMap.Dirty(maps[i], CellRect.CenteredOn(__0, WallBoxes.RockReach + 1));   // (shader cut-away)
                        }
                        else if (st.cutRooms)
                        {
                            Cutaway.RoofChanged(maps[i], __0);
                            if (st.cutRock)
                            {
                                MapSections.QueueAround(maps[i], __0, WallBoxes.RockReach + 1, typeof(SectionLayer_IsoRimWalls), true);
                            }
                        }
                        if (st.showRoofs)
                        {
                            // A roof's slopes are worked out from the cells around it, so the
                            // blocks next door may need their part of the roof redrawn too.
                            MapSections.QueueAround(maps[i], __0, SectionLayer_IsoRimRoofs.Reach, typeof(SectionLayer_IsoRimRoofs));
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Noting a roof change failed: " + e, 93611022);
            }
        }

        // The sliding leaves of a door. __1 is how far they have slid open.
        public static bool DrawMovers_Prefix(Building_Door __instance, float __1)
        {
            if (!View3D.Ready || __instance == null || !IsoRimMod.Settings.doors3D)
            {
                return true;
            }
            try
            {
                if (__instance.def.size.x != 1 || __instance.def.size.z != 1)
                {
                    return true;   // wide doors without their own models (MultiTileDoorDrawAt_Prefix) keep the game's drawing
                }
                return !Doors.Draw(__instance, __1);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a door failed: " + e, 93611017);
                return true;
            }
        }

        private static Func<Building_Door, float> doorOpenPct;
        private static Func<Building_Door, bool> doorStuckOpen;
        private static Action<ThingWithComps> compsPostDraw;

        // A wide door with its own models: drawn in 3D in place of the game's movers, frame and top
        // sprites; its comps still draw (the rest of what Building_Door.DrawAt does).
        public static bool MultiTileDoorDrawAt_Prefix(Building_MultiTileDoor __instance)
        {
            if (!View3D.Ready || __instance == null || !IsoRimMod.Settings.doors3D || !IsoRimMod.Settings.models3D)
            {
                return true;
            }
            try
            {
                ModelEntry entry;
                if (!ModelLibrary.TryGet(__instance.def, out entry) || entry.doorLeaf == null || !entry.HasModel)
                {
                    return true;
                }
                if (doorOpenPct == null)
                {
                    doorOpenPct = AccessTools.MethodDelegate<Func<Building_Door, float>>(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"));
                    doorStuckOpen = AccessTools.MethodDelegate<Func<Building_Door, bool>>(AccessTools.PropertyGetter(typeof(Building_Door), "StuckOpen"));
                    compsPostDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
                }
                if (!Doors.DrawWide(__instance, entry, doorOpenPct(__instance), doorStuckOpen(__instance)))
                {
                    return true;
                }
                compsPostDraw(__instance);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wide door failed: " + e, 93611231);
                return true;
            }
        }

        private static Action<Building_Door> doorPreDraw;

        // The supported door's model entry while it is drawn by Doors.DrawSupported; else null.
        private static ModelEntry SupportedDoorModel(Thing t)
        {
            if (!View3D.Ready || t == null || !IsoRimMod.Settings.doors3D || !IsoRimMod.Settings.models3D || !(t is Building_SupportedDoor)
                || t.def.size.x != 1 || t.def.size.z != 1)
            {
                return null;
            }
            ModelEntry entry;
            return ModelLibrary.TryGet(t.def, out entry) && entry.HasModel ? entry : null;
        }

        // Building_SupportedDoor.DrawAt with the door's own models: the game's support and top sprites and
        // movers are left out; its comps still draw (a vac barrier then draws its field as the game does).
        public static bool SupportedDoorDrawAt_Prefix(Building_SupportedDoor __instance)
        {
            try
            {
                ModelEntry entry = SupportedDoorModel(__instance);
                if (entry == null)
                {
                    return true;
                }
                if (doorOpenPct == null)
                {
                    doorOpenPct = AccessTools.MethodDelegate<Func<Building_Door, float>>(AccessTools.PropertyGetter(typeof(Building_Door), "OpenPct"));
                    doorStuckOpen = AccessTools.MethodDelegate<Func<Building_Door, bool>>(AccessTools.PropertyGetter(typeof(Building_Door), "StuckOpen"));
                    compsPostDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
                }
                if (doorPreDraw == null)
                {
                    doorPreDraw = AccessTools.MethodDelegate<Action<Building_Door>>(AccessTools.Method(typeof(Building_Door), "DoorPreDraw"));
                }
                doorPreDraw(__instance);   // the game's rotation for a one-cell door, from the walls beside it
                if (!Doors.DrawSupported(__instance, entry, doorOpenPct(__instance)))
                {
                    return true;
                }
                compsPostDraw(__instance);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a supported door failed: " + e, 93611281);
                return true;
            }
        }

        // The hacking terminal of a door drawn by Doors.DrawSupported is its 3D model (hackTerminalModel).
        public static bool HackablePostDraw_Prefix(CompHackable __instance)
        {
            try
            {
                ModelEntry entry = SupportedDoorModel(__instance.parent);
                return entry == null || entry.hackTerminal == null;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a door's hacking terminal failed: " + e, 93611282);
                return true;
            }
        }

        public static bool ThingDrawAt_Prefix(Thing __instance)
        {
            try
            {
                if (__instance.def.drawerType == DrawerType.RealtimeOnly)
                {
                    bool held = SectionLayer_IsoRimBodies.StandsIn(__instance);
                    if (Probe.On)
                    {
                        Probe.CountBody(__instance, held);
                    }
                    if (held)
                    {
                        SectionLayer_IsoRimBodies.Track(__instance);
                        return false;   // the baked model stands in for the body sprite
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a building's 3D model failed: " + e, 93611022);
            }
            return true;
        }

        // ThingWithComps.DrawAt called directly (not virtually): what Building_Bookcase.DrawAt
        // runs as base.DrawAt before its books and bookends (the body, held back by
        // ThingDrawAt_Prefix, then the comps).
        private static Action<Thing, Vector3, bool> bookcaseBaseDraw;
        private static bool bookcaseBaseTried;

        private static Action<Thing, Vector3, bool> BookcaseBaseDraw()
        {
            if (!bookcaseBaseTried)
            {
                bookcaseBaseTried = true;
                MethodInfo target = AccessTools.DeclaredMethod(typeof(ThingWithComps), "DrawAt", new[] { typeof(Vector3), typeof(bool) });
                if (target != null)
                {
                    DynamicMethod dm = new DynamicMethod("IsoRim_BookcaseBaseDraw", null, new[] { typeof(Thing), typeof(Vector3), typeof(bool) },
                        typeof(Patches).Module, true);
                    ILGenerator il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, typeof(ThingWithComps));
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, target);
                    il.Emit(OpCodes.Ret);
                    bookcaseBaseDraw = (Action<Thing, Vector3, bool>)dm.CreateDelegate(typeof(Action<Thing, Vector3, bool>));
                }
            }
            return bookcaseBaseDraw;
        }

        public static bool BookcaseDrawAt_Prefix(Building_Bookcase __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (!SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                Action<Thing, Vector3, bool> baseDraw = BookcaseBaseDraw();
                if (baseDraw == null)
                {
                    return true;
                }
                // As the game's own: base.DrawAt a little lower, then (left out) the books and bookends.
                baseDraw(__instance, drawLoc - Altitudes.AltIncVect * 2f, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a bookcase failed: " + e, 93611121);
                return true;
            }
        }

        // True while the thing is drawn as its 3D model.
        private static bool ModelStandsIn(Thing t)
        {
            return t != null && View3D.Ready && Forms.For(t.def).form == Form3D.Model;
        }

        // The gibbet cage's model has its own cage top: the game's flat top sprite (drawn after
        // base.DrawAt) is left out, the comps still drawn. The corpse is drawn by DynamicDrawPhaseAt.
        public static bool GibbetDrawAt_Prefix(Building_GibbetCage __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                Action<Thing, Vector3, bool> baseDraw = ModelStandsIn(__instance) ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                baseDraw(__instance, drawLoc, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a gibbet cage failed: " + e, 93611251);
                return true;
            }
        }

        // A dryad cocoon or healing pod with its 3D shell: the dryad inside is drawn as the game
        // does (raised to the shell's floor by the draw scope), the flat front of the sphere left out.
        public static bool DryadHolderPostDraw_Prefix(CompDryadHolder __instance)
        {
            try
            {
                if (__instance.parent == null || !ModelStandsIn(__instance.parent))
                {
                    return true;
                }
                if (__instance.Props.drawContents)
                {
                    ThingOwner held = __instance.GetDirectlyHeldThings();
                    Vector3 at = __instance.parent.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.BuildingOnTop);
                    for (int i = 0; i < held.Count; i++)
                    {
                        held[i].DrawNowAt(at);
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a dryad cocoon failed: " + e, 93611252);
                return true;
            }
        }

        public static bool MechGestatorDrawAt_Prefix(Building_MechGestator __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawMechGestator(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a mech gestator failed: " + e, 93611262);
                return true;
            }
        }

        public static bool GrowthVatDrawAt_Prefix(Building_GrowthVat __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawGrowthVat(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a growth vat failed: " + e, 93611263);
                return true;
            }
        }

        // An occupied holding platform drawn as its model: the body (held back) and comps as the game's
        // base.DrawAt, then the chains in 3D. An empty one keeps the game's drawing (loose chains on the deck).
        public static bool HoldingPlatformDrawAt_Prefix(Building_HoldingPlatform __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (__instance.HeldPawn == null || !SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(__instance.def).model;
                Action<Thing, Vector3, bool> baseDraw = entry != null && entry.def.chainAnchors != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                baseDraw(__instance, drawLoc, flip);
                HoldingChains.Draw(__instance, entry);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a holding platform's chains failed: " + e, 93611291);
                return true;
            }
        }

        // An outfit stand drawn as its model (stand and body baked, SectionLayer_IsoRimBodies): its head model
        // and what it shows (Stands.Draw) in place of the game's flat base, body, head and apparel.
        public static bool OutfitStandDrawAt_Prefix(Building_OutfitStand __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (!SectionLayer_IsoRimBodies.StandsIn(__instance))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(__instance.def).model;
                return entry == null || !Stands.Draw(__instance, entry, drawLoc);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing an outfit stand failed: " + e, 93611292);
                return true;
            }
        }

        // CompDrawAdditionalGraphics.PostDraw: left out while the thing is drawn as a model with hideExtraGraphics.
        public static bool DrawAdditionalGraphicsPostDraw_Prefix(ThingComp __instance)
        {
            try
            {
                Thing t = __instance.parent;
                if (!ModelStandsIn(t))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(t.def).model;
                return entry == null || !entry.def.hideExtraGraphics;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking a building's extra sprites failed: " + e, 93611293);
                return true;
            }
        }

        public static bool AtomizerDrawAt_Prefix(Building_WastepackAtomizer __instance, Vector3 drawLoc, bool flip)
        {
            try
            {
                ModelEntry entry = Glass.EntryFor(__instance);
                Action<Thing, Vector3, bool> baseDraw = entry != null ? BookcaseBaseDraw() : null;
                if (baseDraw == null)
                {
                    return true;
                }
                Glass.DrawAtomizer(__instance, entry, drawLoc, flip, baseDraw);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a wastepack atomizer failed: " + e, 93611264);
                return true;
            }
        }

        public static bool CableConnectionPostDraw_Prefix(CompCableConnection __instance)
        {
            try
            {
                Thing t = __instance.parent;
                if (t == null || !View3D.Ready || !IsoRimMod.Settings.models3D)
                {
                    return true;
                }
                ThingForm form = Forms.For(t.def);
                if (form.form != Form3D.Model || form.model == null || form.model.def.cableAnchors.NullOrEmpty())
                {
                    return true;
                }
                return !Cables.Draw(__instance, form.model);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing harvester cables failed: " + e, 93611272);
                return true;
            }
        }

        public static bool BiosculpterPostDraw_Prefix(CompBiosculpterPod __instance)
        {
            try
            {
                ModelEntry entry = __instance.parent != null ? Glass.EntryFor(__instance.parent) : null;
                if (entry == null)
                {
                    return true;
                }
                Glass.DrawBiosculpter(__instance, entry);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a biosculpter pod failed: " + e, 93611265);
                return true;
            }
        }

        public static void BookcaseContents_Postfix(Building_Bookcase __instance)
        {
            try
            {
                if (SectionLayer_IsoRimBodies.Handles(__instance))
                {
                    MapSections.QueueAround(__instance.Map, __instance.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Updating a bookcase's books failed: " + e, 93611122);
            }
        }

        public static void BodyColorChanged_Postfix(Thing __instance)
        {
            try
            {
                if (SectionLayer_IsoRimBodies.Handles(__instance))
                {
                    MapSections.QueueAround(__instance.Map, __instance.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Repainting a building's 3D model failed: " + e, 93611123);
            }
        }

        public static bool MoteDrawAt_Prefix(Mote __instance)
        {
            return Spinners.MoteDrawAt_Prefix(__instance);
        }

        public static bool EmptyStatePostDraw_Prefix(ThingComp __instance)
        {
            return __instance.parent == null || !(SectionLayer_IsoRimBodies.StandsIn(__instance.parent) || StateModels.StandsIn(__instance.parent));
        }

        public static void FireOverlayPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            if (__instance.parent != null && !DrawHook.skip && SectionLayer_IsoRimBodies.FlameStandsIn(__instance.parent))
            {
                __state = true;
                DrawHook.skip = true;   // the fire sprite goes through DrawHook (Graphic draws are redirected)
            }
        }

        public static void FireOverlayPostDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                DrawHook.skip = false;
            }
        }

        // ---------------------------------------------------------------- raised things

        // What DrawHook was doing before a scope started, so it can be put back afterwards.
        public struct DrawScope
        {
            public bool changed;
            public bool wasActive;
            public Matrix4x4 wasXform;
            public bool wasBillboard;
            public float wasHeight;
            public bool wasAnchored;
            public Vector3 wasAnchor;
        }

        // Wraps the game's per-frame drawing of one thing. __0 is the drawing phase; only the
        // final phase runs on the main thread and actually draws.
        public static void ThingDraw_Prefix(Thing __instance, DrawPhase __0, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (__0 != DrawPhase.Draw || !View3D.Ready || __instance == null || __instance is Pawn)
            {
                return;
            }
            try
            {
                StateModels.Track(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Checking whether a building was emptied failed: " + e, 93611211);
            }
            // Speech bubbles and icons over people's heads stand up and float above them.
            Mote mote = __instance as Mote;
            if (mote != null && (mote.def.altitudeLayer == AltitudeLayer.MetaOverlays || mote.def.altitudeLayer == AltitudeLayer.MoteOverhead))
            {
                BeginBillboard(ref __state, OverheadHeight, false, Vector3.zero);
                return;
            }
            BeginLift(__instance, ref __state);
        }

        private const float OverheadHeight = 1.45f;   // above a standing colonist's head, in cells
        private const float IconGap = 0.30f;          // how far above a thing its icons float

        // Stands every mesh drawn until the scope ends upright, facing the camera, raised by height cells.
        // With anchored, the icons gather over anchor (see DrawHook.hasAnchor) and bob gently,
        // like the diamond over a Sim's head.
        private static void BeginBillboard(ref DrawScope state, float height, bool anchored, Vector3 anchor)
        {
            if (DrawHook.active)
            {
                return;
            }
            state.changed = true;
            state.wasActive = DrawHook.active;
            state.wasXform = DrawHook.xform;
            state.wasBillboard = DrawHook.billboard;
            state.wasHeight = DrawHook.billboardHeight;
            state.wasAnchored = DrawHook.hasAnchor;
            state.wasAnchor = DrawHook.billboardAnchor;
            if (anchored)
            {
                height += 0.05f * Mathf.Sin(Time.realtimeSinceStartup * 2.4f + anchor.x * 1.3f + anchor.z * 0.7f);
            }
            DrawHook.billboard = true;
            DrawHook.billboardHeight = height;
            DrawHook.hasAnchor = anchored;
            DrawHook.billboardAnchor = anchor;
            DrawHook.active = true;
        }


        // The forbidden cross: the game may draw it in a batch the mod cannot reach, so in 3D
        // it is drawn here instead, upright over the thing.
        public static bool ForbiddenOverlay_Prefix(Thing __0)
        {
            if (!View3D.Ready || __0 == null || __0 is Pawn)
            {
                return true;
            }
            try
            {
                Material forbiddenMat = ForbiddenIcon.Mat;
                if (forbiddenMat == null)
                {
                    return true;
                }
                DrawScope scope = default(DrawScope);
                Vector3 at = __0.TrueCenter();
                BeginBillboard(ref scope, Interaction.PickHeight(__0) + IconGap, true, at);
                DrawHook.DrawMesh(MeshPool.plane05, Matrix4x4.TRS(new Vector3(at.x, AltitudeLayer.MetaOverlays.AltitudeFor(), at.z), Quaternion.identity, Vector3.one), forbiddenMat, 0);
                Scope_Finalizer(scope);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a forbidden icon failed: " + e, 93611171);
                return true;
            }
        }

        private static readonly List<DesignationDef> unbatched = new List<DesignationDef>();

        public static void DrawDesignations_Prefix()
        {
            unbatched.Clear();
            if (!View3D.Ready)
            {
                return;
            }
            List<DesignationDef> defs = DefDatabase<DesignationDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].shouldBatchDraw)
                {
                    defs[i].shouldBatchDraw = false;
                    unbatched.Add(defs[i]);
                }
            }
        }

        public static void DrawDesignations_Finalizer()
        {
            for (int i = 0; i < unbatched.Count; i++)
            {
                unbatched[i].shouldBatchDraw = true;
            }
            unbatched.Clear();
        }

        // A designation (mine, haul, cut, deconstruct ...): its icon floats over what it marks.
        public static void DesignationDraw_Prefix(Designation __instance, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (!View3D.Ready || __instance == null)
            {
                return;
            }
            try
            {
                float h = 0f;
                LocalTargetInfo target = __instance.target;
                Map map = __instance.designationManager != null ? __instance.designationManager.map : Find.CurrentMap;
                if (target.HasThing)
                {
                    h = Interaction.PickHeight(target.Thing);
                }
                else if (target.Cell.IsValid && map != null)
                {
                    h = WallBoxes.BlockHeightAt(map, target.Cell);
                    Building b = target.Cell.InBounds(map) ? target.Cell.GetEdifice(map) : null;
                    if (b != null)
                    {
                        h = Mathf.Max(h, Interaction.PickHeight(b));
                    }
                }
                Vector3 anchor = target.HasThing ? target.Thing.TrueCenter() : target.Cell.ToVector3Shifted();
                BeginBillboard(ref __state, h + IconGap, true, anchor);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Raising a designation icon failed: " + e, 93611170);
            }
        }

        // The game also draws some icons in batches; the same transform is applied there.
        public static bool DrawBatch_Prefix(ref Matrix4x4 __1)
        {
            if (Emitters.On)
            {
                __1 = Emitters.Stand(__1);   // a puff from a model's nozzle (any thread)
                return true;
            }
            if (!DrawHook.active)
            {
                return true;
            }
            if (DrawHook.skip)
            {
                return false;
            }
            __1 = DrawHook.Transform(__1);
            return true;
        }

        // Wraps the drawing of one status icon. __0 is the thing the icon belongs to.
        public static void OverlayRender_Prefix(Thing __0, out DrawScope __state)
        {
            __state = default(DrawScope);
            if (!View3D.Ready || __0 == null || __0 is Pawn)
            {
                return;
            }
            // Status icons (no power, forbidden, out of fuel ...) stand up and float above the thing.
            float h = 0f;
            try
            {
                h = Interaction.PickHeight(__0);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Finding the height for a status icon failed: " + e, 93611191);
            }
            BeginBillboard(ref __state, h + IconGap, true, __0.TrueCenter());
        }

        public static void Scope_Finalizer(DrawScope __state)
        {
            if (__state.changed)
            {
                DrawHook.active = __state.wasActive;
                DrawHook.xform = __state.wasXform;
                DrawHook.billboard = __state.wasBillboard;
                DrawHook.billboardHeight = __state.wasHeight;
                DrawHook.hasAnchor = __state.wasAnchored;
                DrawHook.billboardAnchor = __state.wasAnchor;
            }
        }

        private static void BeginLift(Thing t, ref DrawScope state)
        {
            // Already inside another scope, such as an item in a pawn's hands: leave that in charge.
            if (DrawHook.active)
            {
                return;
            }
            try
            {
                float h = Forms.HeightOf(t);
                Fire fire = t as Fire;
                if (h <= 0f && fire != null && fire.parent == null)
                {
                    h = FireBaseHeight(fire);
                }
                if (h <= 0f)
                {
                    return;
                }
                state.changed = true;
                state.wasActive = DrawHook.active;
                state.wasXform = DrawHook.xform;
                DrawHook.xform = View3D.LiftMatrix(h);
                // What the game draws inside it (a reliquary's relic) moved to where the model holds it.
                ThingForm form = Forms.For(t.def);
                if (form.form == Form3D.Model && form.model != null && form.model.def.contentOffset != Vector3.zero)
                {
                    Vector3 shift = Quaternion.AngleAxis(t.Rotation.AsAngle + form.model.def.rotation, Vector3.up) * form.model.def.contentOffset;
                    DrawHook.xform = Matrix4x4.Translate(new Vector3(shift.x, 0f, shift.z)) * DrawHook.xform;
                }
                DrawHook.active = true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Raising a thing's moving parts failed: " + e, 93611015);
            }
        }

        // A fire burning on a wall, a bed or anything else raised burns on its top, not inside it.
        private static float FireBaseHeight(Fire fire)
        {
            Map map = fire.Map;
            if (map == null || !fire.Position.InBounds(map))
            {
                return 0f;
            }
            float h = 0f;
            List<Thing> things = fire.Position.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing o = things[i];
                if (o != fire && !(o is Pawn) && !(o is Fire))
                {
                    h = Mathf.Max(h, Forms.HeightOf(o));
                }
            }
            return h;
        }

        // Finds every method in the pawn drawing code that hands a mesh to Unity and points it at
        // DrawHook instead. Shadows are left out so they stay on the ground.
        private static void RedirectDrawCalls()
        {
            List<Type> types = new List<Type>();
            AddTypeAndSubclasses(types, "Verse.PawnRenderer");
            AddTypeAndSubclasses(types, "Verse.PawnRenderTree");
            AddTypeAndSubclasses(types, "Verse.PawnRenderUtility");
            AddTypeAndSubclasses(types, "Verse.PawnRenderNodeWorker");
            AddTypeAndSubclasses(types, "Verse.Graphic");
            AddTypeAndSubclasses(types, "RimWorld.OverlayDrawer");
            AddTypeAndSubclasses(types, "RimWorld.TurretTop");
            AddTypeAndSubclasses(types, "RimWorld.CompPowerPlantWind");
            AddTypeAndSubclasses(types, "Verse.Designation");

            HarmonyMethod transpiler = Hook(nameof(Redirect_Transpiler));
            int count = 0;

            foreach (Type type in types)
            {
                if (type.ContainsGenericParameters || type.Name.Contains("Shadow"))
                {
                    continue;
                }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                {
                    if (TryRedirect(m, transpiler))
                    {
                        count++;
                    }
                }
            }

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(GenDraw)))
            {
                if ((m.Name == "DrawMeshNowOrLater" || m.Name == "DrawFillableBar") && TryRedirect(m, transpiler))
                {
                    count++;
                }
            }
            // The band node's tuning bar is drawn straight to Unity: through DrawHook it rises onto
            // the 3D node with the rest of what is drawn over it each frame, as other bars do.
            MethodInfo bandBar = AccessTools.DeclaredMethod(typeof(CompBandNode), "PostDraw");
            if (bandBar != null && TryRedirect(bandBar, transpiler))
            {
                count++;
            }

            if (count == 0)
            {
                ModLog.Warn("[IsoRim] No drawing code could be redirected, so pawns will stay flat.");
            }
            else
            {
                ModLog.Note("[IsoRim] Drawing redirected in " + count + " methods.");
            }
        }

        private static void AddTypeAndSubclasses(List<Type> types, string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                return;
            }
            types.Add(type);
            foreach (Type sub in type.AllSubclasses())
            {
                types.Add(sub);
            }
        }

        private static bool TryRedirect(MethodInfo m, HarmonyMethod transpiler)
        {
            try
            {
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null)
                {
                    return false;
                }
                if (m.Name.Contains("Shadow") || m.Name.Contains("Debug"))
                {
                    return false;
                }

                bool draws = false;
                foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(m))
                {
                    if (IsRedirectable(instruction.Value as MethodInfo))
                    {
                        draws = true;
                        break;
                    }
                }
                if (!draws)
                {
                    return false;
                }

                harmony.Patch(m, null, null, transpiler, null);
                return true;
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not redirect drawing in " + m.DeclaringType + "." + m.Name + ": " + e.Message);
                return false;
            }
        }

        private static bool IsRedirectable(MethodInfo callee)
        {
            return callee != null
                && callee.DeclaringType == typeof(Graphics)
                && callee.Name == "DrawMesh"
                && DrawHook.ReplacementFor(callee) != null;
        }

        public static bool FleckEmit_Prefix(CompThrownFleckEmitter __instance)
        {
            try
            {
                return Emitters.Emit(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Throwing smoke from a model failed: " + e, 93611221);
                return true;
            }
        }

        // (FleckStatic.Draw(altitude, batch): every puff, thrown or not, ends up here; the one-line
        // FleckThrown.Draw is inlined by the runtime and cannot be hooked.)
        public static void FleckDraw_Prefix(ref FleckStatic __instance, out bool __state)
        {
            __state = Emitters.Begin(ref __instance);
        }

        public static void FleckDraw_Finalizer(bool __state)
        {
            if (__state)
            {
                Emitters.End();
            }
        }

        public static IEnumerable<CodeInstruction> DesignatorCell_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(Verse.UI), "MouseCell");
            MethodInfo to = AccessTools.Method(typeof(Interaction), nameof(Interaction.DesignatorMouseCell));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && from != null && ci.operand as MethodInfo == from)
                {
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        public static bool MouseoverBracket_Prefix()
        {
            try
            {
                return !Interaction.DrawBlockBracket();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the designator bracket failed: " + e, 93611164);
                return true;
            }
        }

        public static IEnumerable<CodeInstruction> ShieldDraw_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(Graphics), "DrawMesh", new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock) });
            MethodInfo to = AccessTools.Method(typeof(ShieldDomes), nameof(ShieldDomes.DrawMesh));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call && from != null && ci.operand as MethodInfo == from)
                {
                    ci.operand = to;
                }
                yield return ci;
            }
        }

        public static IEnumerable<CodeInstruction> Redirect_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Call)
                {
                    MethodInfo callee = ci.operand as MethodInfo;
                    if (IsRedirectable(callee))
                    {
                        ci.operand = DrawHook.ReplacementFor(callee);
                    }
                }
                yield return ci;
            }
        }
    }
}

namespace IsoRim
{
    // The game's forbidden cross, looked up once on the main thread at startup.
    [StaticConstructorOnStartup]
    public static class ForbiddenIcon
    {
        public static readonly Material Mat = Find();

        private static Material Find()
        {
            Type overlays = AccessTools.TypeByName("RimWorld.OverlayDrawer");
            FieldInfo f = overlays != null ? AccessTools.Field(overlays, "ForbiddenMat") : null;
            return f != null ? f.GetValue(null) as Material : null;
        }
    }
}
