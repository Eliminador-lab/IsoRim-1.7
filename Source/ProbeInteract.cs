using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only pointer tests for the probe file (see Probe): the pointer is moved by the probe
    // (Input.mousePosition is overridden while a test runs) and the game's own selection code is
    // called as the mouse events would call it (Selector.HandleMapClicks is skipped only because the
    // harness window gets no events). Points are given on the map (x, z and a height in cells) and
    // turned into screen points with the camera of the moment, so one probe line works at any yaw.
    // The camera is held at cam= while tests run; a test waits for a turn to end. Screenshots are
    // taken with the game's interface on (renders\<harness tag>\ix_<tag>_*.png).
    //   cam=x,z,size                 where the camera is held while the tests run
    //   drag=ax,az,bx,bz,tag[,ah,bh] a selection box dragged from one map point to the other (heights ah, bh):
    //                                shot of the box, then the selection (Selector.SelectInsideDragBox) is logged
    //                                with every pawn whose body shows inside the box on screen, then a shot of it
    //   dragpx=x,z,h,dx0,dy0,dx1,dy1,tag  the same between two screen offsets (pixels, y up) round one map point
    //   click=x,z,h,tag,mode[,sx,sz] the pointer over a map point at height h for a moment (cut-away settles), then
    //                                mode sel = a click (Selector.SelectUnderMouse), dbl = a double click,
    //                                menu = right click with the pawn at (sx,sz) selected (options logged and shown),
    //                                target = what an attack targeter offers, hover = nothing (shot only);
    //                                every mode logs the cell under the pointer and the 3D pick list
    //   zdrag=ax,az,bx,bz,tag        a stockpile zone dragged out with the game's designator between two ground points (shot of the
    //                                drag outline mid-drag, then the zone is made and its cells logged)
    //   labels=1                     pawn name labels drawn in the shots (the harness hides them)
    public static partial class Probe
    {
        private class IxJob
        {
            public string kind;
            public string tag;
            public string mode;
            public Vector3 a;
            public Vector3 b;
            public IntVec3 sel = IntVec3.Invalid;
            public int frame = -1;
            public Rect box;
            public Vector2 offA;
            public Vector2 offB;
        }

        private static readonly List<IxJob> ixJobs = new List<IxJob>();
        private static Vector3 ixCam;
        private static Vector2? fakeMouse;
        private static bool mouseHooked;
        private static int ixSteady;

        private static readonly MethodInfo selectInsideBox = AccessTools.Method(typeof(Selector), "SelectInsideDragBox");
        private static readonly MethodInfo selectUnderMouse = AccessTools.Method(typeof(Selector), "SelectUnderMouse");
        private static readonly MethodInfo selectAllMatching = AccessTools.Method(typeof(Selector), "SelectAllMatchingObjectUnderMouseOnScreen");

        private static void IxKey(string key, string[] a)
        {
            switch (key)
            {
                case "cam":
                    ixCam = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                    break;
                case "drag":
                    HookMouse();
                    ixJobs.Add(new IxJob
                    {
                        kind = "drag", tag = a[4],
                        a = new Vector3(F(a[0]), a.Length > 5 ? F(a[5]) : 0f, F(a[1])),
                        b = new Vector3(F(a[2]), a.Length > 6 ? F(a[6]) : 0f, F(a[3]))
                    });
                    break;
                case "zdrag":
                    HookMouse();
                    ixJobs.Add(new IxJob { kind = "zdrag", tag = a[4], a = new Vector3(F(a[0]), 0f, F(a[1])), b = new Vector3(F(a[2]), 0f, F(a[3])) });
                    break;
                case "dragpx":
                {
                    // dragpx=x,z,h,dx0,dy0,dx1,dy1,tag: from and to screen offsets (pixels, y up) round a map point
                    HookMouse();
                    Vector3 at = new Vector3(F(a[0]), F(a[2]), F(a[1]));
                    ixJobs.Add(new IxJob
                    {
                        kind = "drag", tag = a[7], a = at, b = at,
                        offA = new Vector2(F(a[3]), F(a[4])), offB = new Vector2(F(a[5]), F(a[6]))
                    });
                    break;
                }
                case "click":
                {
                    HookMouse();
                    IxJob j = new IxJob { kind = "click", a = new Vector3(F(a[0]), F(a[2]), F(a[1])), tag = a[3], mode = a[4] };
                    if (a.Length > 6)
                    {
                        j.sel = new IntVec3(I(a[5]), 0, I(a[6]));
                    }
                    ixJobs.Add(j);
                    break;
                }
                case "labels":
                {
                    foreach (MethodInfo mi in typeof(GenMapUI).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "DrawPawnLabel"))
                    {
                        new Harmony("peren.isorim17.probe.labels").Unpatch(mi, HarmonyPatchType.Prefix, "Peren.Capture17");
                    }
                    break;
                }
            }
        }

        private static void HookMouse()
        {
            if (mouseHooked)
            {
                return;
            }
            mouseHooked = true;
            PropertyInfo pi = AccessTools.Property(typeof(Input), "mousePosition");
            MethodInfo getter = pi != null ? pi.GetGetMethod() : null;
            if (getter == null)
            {
                Log.Warning("[IsoRim probe] Input.mousePosition not found; pointer tests will not work");
                return;
            }
            string post = pi.PropertyType == typeof(Vector2) ? nameof(FakeMouse2) : nameof(FakeMouse3);
            new Harmony("peren.isorim17.probe.mouse").Patch(getter, postfix: new HarmonyMethod(typeof(Probe), post));
            Log.Message("[IsoRim probe] pointer override on " + pi.DeclaringType.FullName + ".mousePosition (" + pi.PropertyType.Name + ")");
        }

        public static void FakeMouse3(ref Vector3 __result)
        {
            if (fakeMouse.HasValue)
            {
                __result = new Vector3(fakeMouse.Value.x, fakeMouse.Value.y, 0f);
            }
        }

        public static void FakeMouse2(ref Vector2 __result)
        {
            if (fakeMouse.HasValue)
            {
                __result = fakeMouse.Value;
            }
        }

        // Every frame (from Sticky).
        private static void IxTick(Map map)
        {
            if (ixJobs.Count == 0)
            {
                return;
            }
            if (ixCam.z > 0f)
            {
                Find.CameraDriver.SetRootPosAndSize(new Vector3(ixCam.x, 0f, ixCam.y), ixCam.z);
            }
            IxJob j = ixJobs[0];
            if (j.frame < 0)
            {
                // Wait for the camera to come to rest (a turn, a tilt, the jump to cam=).
                ixSteady = View3D.Turning || !View3D.Ready ? 0 : ixSteady + 1;
                if (ixSteady < 6)
                {
                    return;
                }
                j.frame = 0;
            }
            try
            {
                if (j.kind == "drag")
                {
                    DragStep(map, j);
                }
                else if (j.kind == "zdrag")
                {
                    ZoneDragStep(map, j);
                }
                else
                {
                    ClickStep(map, j);
                }
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] ix " + j.tag + " failed: " + e);
                j.frame = 1000;
            }
            j.frame++;
            if (j.frame > (j.kind == "click" ? 72 : 40))
            {
                fakeMouse = null;
                Find.Selector.dragBox.active = false;
                Find.Selector.ClearSelection();
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = true;
                ixJobs.RemoveAt(0);
                ixSteady = 0;
            }
        }

        private static void CloseWindows()
        {
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
        }

        private static Vector2 ScreenOf(Vector3 p)
        {
            return View3D.WorldToScreen(p);
        }

        // Screen pixels (bottom left) to the interface's coordinates (top left, UI scale).
        private static Vector2 UiOf(Vector2 px)
        {
            return new Vector2(px.x / Prefs.UIScale, Verse.UI.screenHeight - px.y / Prefs.UIScale);
        }

        private static void IxShot(IxJob j, string what)
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "ix_" + j.tag + "_" + what + ".png"));
        }

        private static string Yp()
        {
            return "yaw " + View3D.Yaw.ToString("0") + " pitch " + View3D.Pitch.ToString("0.0");
        }

        private static string Desc(object o)
        {
            Thing t = o as Thing;
            if (t == null)
            {
                return o == null ? "null" : o.GetType().Name + " " + o;
            }
            string who = t is Pawn ? ((Pawn)t).LabelShort + "(" + t.def.defName + ")" : t.def.defName;
            return who + "@" + t.Position.x + "," + t.Position.z;
        }

        private static void DragStep(Map map, IxJob j)
        {
            Selector sel = Find.Selector;
            Vector2 pa = ScreenOf(j.a) + j.offA;
            Vector2 pb = ScreenOf(j.b) + j.offB;
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                sel.ClearSelection();
                fakeMouse = pa;
                // As Selector.HandleMapClicks on a left mouse-down.
                sel.dragBox.active = true;
                sel.dragBox.start = Verse.UI.MouseMapPosition();
            }
            else if (j.frame <= 8)
            {
                fakeMouse = Vector2.Lerp(pa, pb, j.frame / 8f);
            }
            if (j.frame == 12)
            {
                j.box = sel.dragBox.ScreenRect;
                IxShot(j, "box");
            }
            if (j.frame == 16)
            {
                // As Selector.HandleMapClicks on the mouse-up of a drag.
                sel.dragBox.active = false;
                bool valid = sel.dragBox.IsValid;
                selectInsideBox.Invoke(sel, null);
                Rect r = j.box;
                List<object> got = sel.SelectedObjects.ToList();
                Log.Message("[IsoRim probe] ix " + j.tag + " drag (" + Yp() + "): screen A " + pa + " B " + pb + ", box (UI) " + r
                    + ", valid " + valid + ", selected " + got.Count + ": " + string.Join("; ", got.Select(Desc)));
                // Every pawn whose body shows inside the box on screen: it should be selected when
                // any pawn is (pawns win over everything else).
                List<string> missing = new List<string>();
                List<string> extra = new List<string>();
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (!ThingSelectionUtility.SelectableByMapClick(p))
                    {
                        continue;
                    }
                    Vector3 d = p.DrawPos;
                    float top = Interaction.PickHeight(p);
                    bool shows = false;
                    for (float h = 0.1f; h <= top; h += 0.1f)
                    {
                        shows |= r.Contains(UiOf(ScreenOf(new Vector3(d.x, h, d.z))));
                    }
                    bool feet = r.Contains(UiOf(ScreenOf(new Vector3(d.x, 0f, d.z))));
                    bool picked = sel.IsSelected(p);
                    if (shows && !picked)
                    {
                        missing.Add(Desc(p) + (feet ? " (feet in box)" : " (only body in box)"));
                    }
                    if (picked && !shows)
                    {
                        extra.Add(Desc(p) + (feet ? " (feet in box)" : " (outside)"));
                    }
                }
                Log.Message("[IsoRim probe] ix " + j.tag + " drag check: pawns showing in the box but not selected: "
                    + (missing.Count == 0 ? "none" : string.Join("; ", missing)) + "; selected pawns not showing in it: "
                    + (extra.Count == 0 ? "none" : string.Join("; ", extra)));
                fakeMouse = pb;
            }
            if (j.frame == 26)
            {
                IxShot(j, "sel");
            }
        }

        private static void ClickStep(Map map, IxJob j)
        {
            Selector sel = Find.Selector;
            Vector2 p = ScreenOf(j.a);
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                sel.ClearSelection();
                fakeMouse = p;
            }
            if (j.frame == 60)
            {
                IntVec3 cell = Verse.UI.MouseCell();
                List<Thing> picks = Interaction.PickUnderPointer(map);
                string head = "[IsoRim probe] ix " + j.tag + " " + j.mode + " (" + Yp() + "): point " + j.a + " screen " + p + ", cell under pointer "
                    + cell + ", 3D pick " + string.Join(", ", picks.Take(5).Select(Desc));
                switch (j.mode)
                {
                    case "sel":
                        selectUnderMouse.Invoke(sel, null);
                        Log.Message(head + " -> selected " + string.Join("; ", sel.SelectedObjects.Select(Desc)));
                        break;
                    case "dbl":
                        selectAllMatching.Invoke(sel, null);
                        Log.Message(head + " -> double click selected " + sel.SelectedObjects.Count + ": "
                            + string.Join("; ", sel.SelectedObjects.Take(12).Select(Desc)));
                        break;
                    case "menu":
                    {
                        Pawn who = j.sel.IsValid ? j.sel.GetFirstPawn(map) : map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                        if (who == null)
                        {
                            Log.Warning(head + " -> no pawn to give orders");
                            break;
                        }
                        sel.Select(who, false, false);
                        FloatMenuContext ctx;
                        List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(sel.SelectedPawns, Verse.UI.MouseMapPosition(), out ctx);
                        Log.Message(head + " -> " + Desc(who) + " right click: clicked things "
                            + (ctx != null ? string.Join(", ", ctx.ClickedThings.Take(5).Select(Desc)) : "-") + "; options "
                            + (opts == null ? "none" : string.Join(" | ", opts.Take(10).Select(o => o.Label))));
                        if (!opts.NullOrEmpty())
                        {
                            Find.WindowStack.Add(new FloatMenuMap(opts, who.LabelCap, Verse.UI.MouseMapPosition()));
                        }
                        break;
                    }
                    case "target":
                    {
                        List<LocalTargetInfo> ts = GenUI.TargetsAtMouse(TargetingParameters.ForAttackAny()).Take(5).ToList();
                        Log.Message(head + " -> attack targets " + string.Join(", ", ts.Select(t => t.HasThing ? Desc(t.Thing) : t.Cell.ToString())));
                        break;
                    }
                    default:
                        Log.Message(head);
                        break;
                }
            }
            if (j.frame == 66)
            {
                IxShot(j, j.mode);
            }
        }

        private static void ZoneDragStep(Map map, IxJob j)
        {
            DesignatorManager dm = Find.DesignatorManager;
            Vector2 pa = ScreenOf(j.a);
            Vector2 pb = ScreenOf(j.b);
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                fakeMouse = pa;
                dm.Select(new Designator_ZoneAddStockpile_Resources());
            }
            if (j.frame == 2)
            {
                dm.Dragger.StartDrag();   // as Designator.ProcessInput on a mouse-down over the map
            }
            else if (j.frame > 2 && j.frame <= 10)
            {
                fakeMouse = Vector2.Lerp(pa, pb, (j.frame - 2) / 8f);
            }
            if (j.frame == 16)
            {
                IxShot(j, "drag");
            }
            if (j.frame == 20)
            {
                int before = map.zoneManager.AllZones.Count;
                dm.SelectedDesignator.DesignateMultiCell(dm.Dragger.DragCells);   // as DesignatorManager on the mouse-up
                dm.Dragger.EndDrag();
                Zone z = map.zoneManager.ZoneAt(Verse.UI.MouseCell());
                Log.Message("[IsoRim probe] ix " + j.tag + " zone drag (" + Yp() + "): from cell " + j.a.ToIntVec3() + " to " + j.b.ToIntVec3()
                    + ", mouse cell " + Verse.UI.MouseCell() + ", zones " + before + " -> " + map.zoneManager.AllZones.Count
                    + (z != null ? ", zone under pointer " + z.cells.Count + " cells, x " + z.cells.Min(c => c.x) + ".." + z.cells.Max(c => c.x) + " z " + z.cells.Min(c => c.z) + ".." + z.cells.Max(c => c.z) : ""));
                dm.Deselect();
            }
            if (j.frame == 26)
            {
                IxShot(j, "zone");
            }
        }
    }
}
