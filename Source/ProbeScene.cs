using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only scene keys for the probe file (see Probe): things the screenshot harness's
    // layouts cannot set up, for the 3D render checks. Cells are map cells (x,z).
    //   set=<setting>,<value>        an IsoRim setting (bool 0/1, int, float or text, e.g. lookName,Noir)
    //   delay=<frames>               apply this pass that many frames after the clock jump (default 10)
    //   fire=x,z                     a fire in the cell (also on a wall)
    //   gas=x,z,<GasType>,<radius>   a gas cloud
    //   snow=<depth>,x0,z0,x1,z1     snow over a rectangle
    //   terrain=<Def>,x0,z0,x1,z1    terrain over a rectangle
    //   spawn=<Def>,x,z[,<Stuff>|-[,<count>]]  a thing (the player's), optionally a stack
    //   roof=0|1,x0,z0,x1,z1         built roof off or on over a rectangle
    //   active=0|1                   the 3D view off or on
    //   fog=x0,z0,x1,z1              fog of war over a rectangle
    //   mine=x,z                     the rock in the cell is mined out at once
    //   zone=x0,z0,x1,z1             a stockpile
    //   home=x0,z0,x1,z1             home area
    //   designate=mine|cut|deconstruct,x,z
    //   select=x,z | select=none     kept selected through the shots (the harness clears the selection)
    //   overlay=power|temperature|beauty|roof|fertility|none
    //   darkness=1                   Anomaly's unnatural darkness
    //   fleck=smoke|dust|glow,x,z,<count>
    //   ramp=0|1                     the grade's test band of known colours (Grade.TestPattern)
    //   gradegpu=N                   the grade pass drawn N more times a frame (to time it on the GPU)
    //   gradebench=N                 builds a grade table N times (alternating looks) and logs the times
    //   spritesweep=1                checks the CPU sprite sampling over every extruded / colour-sampled def
    //                                (SpriteSweep; writes IsoRimSpriteSweep.tsv/.txt, IsoRimSpriteColours.tsv)
    //   spritestats=1                logs the sprite sampling counters (time, reads, cache size)
    //   itemsheet=k,x0,z0,cols,rows  lays batch k of the extruded items out in a grid (contact sheets)
    //   modelsweep=1                 lists the 3D form, model entry and drawing of every vanilla/DLC def
    //                                (ModelSweep; writes IsoRimModelSweep.tsv/.txt)
    //   perf=N[,label]               logs frame times (median, mean, p95) and IsoRim's tick over the next N frames,
    //                                with the realtime building bodies the game drew / IsoRim held back per frame
    //   uncap=1                      lifts the game's frame cap (Application.targetFrameRate) for perf
    //   fuel=x,z,0|1                 the refuelable building in the cell emptied or filled
    //   casket=x,z,0|1               the casket in the cell emptied, or given a new colonist to hold
    //   books=x,z,N[,Def:Def...]     the bookcase in the cell holds exactly N new books, kinds taken in turn
    //                                (default every book kind there is); books=x,z,+<Def> adds one,
    //                                books=x,z,- destroys the last, books=x,z,drop drops the first beside it
    //   haulbook=<Def>,bx,bz,cx,cz   a new book at (bx,bz) and the first free colonist told to haul it
    //                                into the bookcase at (cx,cz) (a real job: run tick= after it)
    //   tick=N                       N game ticks at once (N <= 900)
    //   bookcheck=1                  logs, for each bookcase, the books held against the books baked
    //   paint=x,z,<ColorDef>|-        the building in the cell painted (Building.ChangePaint, as the paint job does)
    //   power=x,z                    the building in the cell counts as powered from now on
    //   blueprint=<Def>,x,z[,<Stuff>|-[,rot]]  the player's blueprint to build it there
    //   tickuntil=bursting,N         game ticks until some turret fires a burst (at most N <= 900)
    //   interact=x,z                 the nearest free colonist is ordered to activate the building (as its float menu option)
    //   turretcheck=1                logs every turret's verb state, aim override, gun rotation and which gun model it shows
    //   ui=1                         map overlays the screenshot mode hides (selection, temperature,
    //                                roof, fertility, areas) are drawn in the shots; overlay=home
    //                                shows the home area
    public static partial class Probe
    {
        private static readonly List<IntVec3> keepSelected = new List<IntVec3>();
        private static string overlay = "none";

        private static int DelayOf(int k)
        {
            if (passes == null || k < 0 || k >= passes.Count)
            {
                return ApplyDelay;
            }
            foreach (string part in passes[k].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("delay="))
                {
                    return int.Parse(part.Substring(6), CultureInfo.InvariantCulture);
                }
            }
            return ApplyDelay;
        }

        private static int I(string s)
        {
            return int.Parse(s, CultureInfo.InvariantCulture);
        }

        private static float F(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        // Times the two halves of a grade table rebuild: working out the table, and the upload.
        private static void GradeBench(int n)
        {
            List<double> build = new List<double>(), upload = new List<double>();
            Color32[] px = null;
            Texture2D tex = new Texture2D(Grade.Size * Grade.Size, Grade.Size, TextureFormat.RGBA32, false, true);
            for (int i = 0; i < n; i++)
            {
                LookPreset p = Looks.Presets[i % (Looks.Presets.Count - 1)];
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                px = LookMath.Strip(p, Grade.Size, px);
                build.Add(w.Elapsed.TotalMilliseconds);
                w.Restart();
                tex.SetPixels32(px);
                tex.Apply(false);
                upload.Add(w.Elapsed.TotalMilliseconds);
            }
            UnityEngine.Object.Destroy(tex);
            build.Sort();
            upload.Sort();
            Log.Message("[IsoRim probe] grade table x" + n + ": build median " + build[n / 2].ToString("0.000") + " max " + build[n - 1].ToString("0.000")
                + " ms, upload median " + upload[n / 2].ToString("0.000") + " max " + upload[n - 1].ToString("0.000") + " ms");
        }

        private static CellRect Rect(string[] a, int i)
        {
            return CellRect.FromLimits(I(a[i]), I(a[i + 1]), I(a[i + 2]), I(a[i + 3]));
        }

        private static void Scene(string key, string[] a)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            switch (key)
            {
                case "delay":
                    return;
                case "set":
                {
                    FieldInfo f = AccessTools.Field(typeof(IsoRimSettings), a[0]);
                    object v = f.FieldType == typeof(bool) ? (object)(a[1] == "1" || a[1] == "true")
                        : f.FieldType == typeof(int) ? (object)I(a[1])
                        : f.FieldType == typeof(string) ? (object)a[1] : (object)F(a[1]);
                    if (a[0] == "showRoofs")
                    {
                        Roofs.Set((bool)v);   // as the button does: the roofs are rebuilt
                    }
                    else
                    {
                        f.SetValue(IsoRimMod.Settings, v);
                    }
                    break;
                }
                case "fire":
                {
                    Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = 1f;
                    GenSpawn.Spawn(fire, new IntVec3(I(a[0]), 0, I(a[1])), map, Rot4.North);
                    break;
                }
                case "gas":
                    GasUtility.AddGas(new IntVec3(I(a[0]), 0, I(a[1])), map, (GasType)Enum.Parse(typeof(GasType), a[2]), F(a[3]));
                    break;
                case "snow":
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.snowGrid.SetDepth(c, F(a[0]));
                        }
                    }
                    break;
                case "terrain":
                {
                    TerrainDef def = DefDatabase<TerrainDef>.GetNamed(a[0]);
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.terrainGrid.SetTerrain(c, def);
                        }
                    }
                    break;
                }
                case "spawn":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(a[0]);
                    ThingDef stuff = a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    if (a.Length > 4)
                    {
                        t.stackCount = Mathf.Clamp(I(a[4]), 1, def.stackLimit);
                    }
                    if (t.def.CanHaveFaction)
                    {
                        t.SetFaction(Faction.OfPlayer);
                    }
                    GenSpawn.Spawn(t, new IntVec3(I(a[1]), 0, I(a[2])), map, Rot4.North);
                    break;
                }
                case "roof":
                {
                    RoofDef def = a[0] == "0" ? null : RoofDefOf.RoofConstructed;
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.roofGrid.SetRoof(c, def);
                        }
                    }
                    break;
                }
                case "active":
                    View3D.Active = a[0] == "1";
                    break;
                case "fog":
                    map.fogGrid.Refog(Rect(a, 0));
                    break;
                case "mine":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetEdifice(map);
                    if (b != null)
                    {
                        b.Destroy(DestroyMode.KillFinalize);
                    }
                    break;
                }
                case "zone":
                {
                    Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(z);
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map) && map.zoneManager.ZoneAt(c) == null && c.Standable(map))
                        {
                            z.AddCell(c);
                        }
                    }
                    break;
                }
                case "home":
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map))
                        {
                            map.areaManager.Home[c] = true;
                        }
                    }
                    break;
                case "designate":
                {
                    IntVec3 c = new IntVec3(I(a[1]), 0, I(a[2]));
                    if (a[0] == "mine")
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    else if (a[0] == "cut")
                    {
                        Plant p = c.GetPlant(map);
                        if (p != null)
                        {
                            map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.CutPlant));
                        }
                    }
                    else
                    {
                        Building b = c.GetEdifice(map);
                        if (b != null)
                        {
                            map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                        }
                    }
                    break;
                }
                case "select":
                    if (a[0] == "none")
                    {
                        keepSelected.Clear();
                    }
                    else
                    {
                        keepSelected.Add(new IntVec3(I(a[0]), 0, I(a[1])));
                    }
                    break;
                case "overlay":
                {
                    overlay = a[0];
                    PlaySettings ps = Find.PlaySettings;
                    ps.showTemperatureOverlay = overlay == "temperature";
                    ps.showBeauty = overlay == "beauty";
                    ps.showRoofOverlay = overlay == "roof";
                    ps.showFertilityOverlay = overlay == "fertility";
                    break;
                }
                case "ramp":
                    Grade.TestPattern = a[0] == "1";
                    break;
                case "gradegpu":
                    Grade.ExtraDraws = I(a[0]);
                    break;
                case "gradebench":
                    GradeBench(I(a[0]));
                    break;
                case "spritesweep":
                    SpriteSweep.Run();
                    break;
                case "perf":
                    PerfHooks();
                    perfLeft = I(a[0]);
                    perfLabel = a.Length > 1 ? a[1] : "";
                    perfDt.Clear();
                    perfTick.Clear();
                    perfUpdate.Clear();
                    perfDynamic.Clear();
                    perfMesh.Clear();
                    bodiesDrawn = 0;
                    bodiesHeld = 0;
                    break;
                case "uncap":
                    Application.targetFrameRate = a[0] == "1" ? 1000 : Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
                    break;
                case "modelsweep":
                    ModelSweep.Run();
                    break;
                case "fuel":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompRefuelable fuel = b != null ? b.TryGetComp<CompRefuelable>() : null;
                    if (fuel == null)
                    {
                        Log.Warning("[IsoRim probe] no refuelable building at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (a[2] == "0")
                    {
                        fuel.ConsumeFuel(fuel.Fuel);
                    }
                    else
                    {
                        fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
                    }
                    break;
                }
                case "casket":
                {
                    Building_Casket casket = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_Casket;
                    if (casket == null)
                    {
                        Log.Warning("[IsoRim probe] no casket at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (a[2] == "0")
                    {
                        casket.GetDirectlyHeldThings().ClearAndDestroyContents();
                    }
                    else if (!casket.HasAnyContents)
                    {
                        Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        casket.TryAcceptThing(p, false);
                    }
                    break;
                }
                case "spritestats":
                    SpriteSweep.Stats();
                    break;
                case "itemsheet":
                    SpriteSweep.PlaceSheet(map, I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4]));
                    break;
                case "ui":
                    if (!uiPatched)
                    {
                        uiPatched = true;
                        Harmony h = new Harmony("peren.isorim17.probe");
                        h.Patch(AccessTools.Method(typeof(MapInterface), "MapInterfaceUpdate"),
                            prefix: new HarmonyMethod(typeof(Probe), nameof(OverlaysOn)), finalizer: new HarmonyMethod(typeof(Probe), nameof(OverlaysOff)));
                    }
                    break;
                case "darkness":
                {
                    GameCondition dark = GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.UnnaturalDarkness);
                    map.gameConditionManager.RegisterCondition(dark);
                    dark.startTick = Find.TickManager.TicksGame - 2000;      // past its fade-in
                    map.gameConditionManager.SetTargetBrightness(0f, 100000f);   // the darkness sheet at once
                }
                    break;
                case "fleck":
                {
                    Vector3 at = new IntVec3(I(a[1]), 0, I(a[2])).ToVector3Shifted();
                    int n = a.Length > 3 ? I(a[3]) : 4;
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 p = at + new Vector3(Rand.Range(-0.4f, 0.4f), 0f, Rand.Range(-0.4f, 0.4f));
                        if (a[0] == "smoke")
                        {
                            FleckMaker.ThrowSmoke(p, map, 1.5f);
                        }
                        else if (a[0] == "dust")
                        {
                            FleckMaker.ThrowDustPuff(p, map, 2f);
                        }
                        else
                        {
                            FleckMaker.ThrowFireGlow(p, map, 1f);
                        }
                    }
                    break;
                }
                case "books":
                    Books(map, a);
                    break;
                case "haulbook":
                    HaulBook(map, a);
                    break;
                case "tick":
                {
                    int n = Mathf.Clamp(I(a[0]), 1, 900);   // under the clock jump that starts a pass
                    for (int i = 0; i < n; i++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    break;
                }
                case "bookcheck":
                    bookCheckAt = Time.frameCount + 30;   // once the queued rebuilds are done
                    break;
                case "power":
                {
                    Building pw = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (pw == null || pw.TryGetComp<CompPowerTrader>() == null)
                    {
                        Log.Warning("[IsoRim probe] no powered building at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (!powerPatched)
                    {
                        powerPatched = true;
                        new Harmony("peren.isorim17.probe.power").Patch(AccessTools.PropertyGetter(typeof(CompPowerTrader), nameof(CompPowerTrader.PowerOn)),
                            postfix: new HarmonyMethod(typeof(Probe), nameof(PoweredPostfix)));
                    }
                    powered.Add(pw);
                    break;
                }
                case "blueprint":
                {
                    ThingDef bd = DefDatabase<ThingDef>.GetNamed(a[0]);
                    ThingDef bs = a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : (bd.MadeFromStuff ? GenStuff.DefaultStuffFor(bd) : null);
                    GenConstruct.PlaceBlueprintForBuild(bd, new IntVec3(I(a[1]), 0, I(a[2])), map, a.Length > 4 ? new Rot4(I(a[4])) : Rot4.North, Faction.OfPlayer, bs);
                    break;
                }
                case "tickuntil":
                {
                    // tickuntil=bursting,N: game ticks one at a time until some turret is firing a burst (at most N).
                    int limit = Mathf.Clamp(I(a[1]), 1, 900), done = 0;
                    while (done < limit && !map.listerThings.AllThings.OfType<Building_Turret>().Any(t => t.CurrentEffectiveVerb != null && t.CurrentEffectiveVerb.state == VerbState.Bursting))
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    Log.Message("[IsoRim probe] tickuntil " + a[0] + ": " + done + " ticks");
                    break;
                }
                case "interact":
                {
                    Building ib = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompInteractable ic = ib != null ? ib.TryGetComp<CompInteractable>() : null;
                    Pawn ip = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.Position.DistanceToSquared(ib != null ? ib.Position : IntVec3.Zero)).FirstOrDefault();
                    if (ic == null || ip == null)
                    {
                        Log.Warning("[IsoRim probe] interact: nothing to interact with at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    if (ip.drafter != null)
                    {
                        ip.drafter.Drafted = false;
                    }
                    ic.OrderForceTarget(ip);   // as the float menu option does: the colonist walks over and activates it
                    Log.Message("[IsoRim probe] interact: " + ip.LabelShort + " at " + ip.Position + " activates " + ib.def.defName + " (" + (ip.CurJob != null ? ip.CurJob.def.defName : "no job") + ")");
                    break;
                }
                case "turretcheck":
                    foreach (Building_Turret tt in map.listerThings.AllThings.OfType<Building_Turret>())
                    {
                        Verb v = tt.CurrentEffectiveVerb;
                        ModelEntry te;
                        bool hasEntry = ModelLibrary.TryGet(tt.def, out te);
                        bool loaded = tt.def.building != null && tt.TurretTopMaterial != tt.def.building.turretTopMat;
                        TurretTop top = tt is Building_TurretGun g ? Traverse.Create(g).Field("top").GetValue<TurretTop>() : null;
                        Log.Message("[IsoRim probe] turretcheck " + tt.def.defName + " " + tt.Position + " verb " + (v != null ? v.state.ToString() : "-")
                            + " override " + (v != null && v.AimAngleOverride.HasValue ? v.AimAngleOverride.Value.ToString("0.0") : "-")
                            + " rot " + (top != null ? top.CurRotation.ToString("0.0") : "-") + " loaded " + loaded
                            + " gun " + (!hasEntry || te.top == null ? "none" : loaded && te.topLoaded != null ? "topModelLoaded" : "topModel")
                            + " fuel " + (tt.TryGetComp<CompRefuelable>()?.Fuel.ToString("0.0") ?? "-") + " power " + (tt.TryGetComp<CompPowerTrader>()?.PowerOn.ToString() ?? "-"));
                    }
                    break;
                case "paint":
                {
                    Building pb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (pb != null)
                    {
                        pb.ChangePaint(a[2] == "-" ? null : DefDatabase<ColorDef>.GetNamed(a[2]));
                    }
                    break;
                }
                default:
                    Log.Warning("[IsoRim probe] unknown key " + key);
                    return;
            }
            Log.Message("[IsoRim probe] " + key + "=" + string.Join(",", a));
        }

        private static readonly string[] BookOrder = { "TextBook", "Novel", "Schematic", "Tome", "Map" };

        private static List<ThingDef> BookKinds(string list)
        {
            List<ThingDef> kinds = new List<ThingDef>();
            string[] names = list != null ? list.Split(':') : BookOrder;
            foreach (string name in names)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (d != null && typeof(Book).IsAssignableFrom(d.thingClass))
                {
                    kinds.Add(d);
                }
            }
            return kinds;
        }

        private static Building_Bookcase BookcaseAt(Map map, int x, int z)
        {
            Building_Bookcase b = new IntVec3(x, 0, z).GetFirstBuilding(map) as Building_Bookcase;
            if (b == null)
            {
                Log.Warning("[IsoRim probe] no bookcase at " + x + "," + z);
            }
            return b;
        }

        private static void Books(Map map, string[] a)
        {
            Building_Bookcase b = BookcaseAt(map, I(a[0]), I(a[1]));
            if (b == null)
            {
                return;
            }
            ThingOwner held = b.GetDirectlyHeldThings();
            string what = a[2];
            if (what == "-")
            {
                if (b.HeldBooks.Count > 0)
                {
                    b.HeldBooks[b.HeldBooks.Count - 1].Destroy();
                }
            }
            else if (what == "drop")
            {
                if (b.HeldBooks.Count > 0)
                {
                    Thing dropped;
                    held.TryDrop(b.HeldBooks[0], b.Position, map, ThingPlaceMode.Near, out dropped);
                }
            }
            else if (what.StartsWith("+"))
            {
                List<ThingDef> kinds = BookKinds(what.Substring(1));
                if (kinds.Count > 0)
                {
                    held.TryAdd(BookUtility.MakeBook(kinds[0], ArtGenerationContext.Colony));
                }
            }
            else
            {
                held.ClearAndDestroyContents();
                List<ThingDef> kinds = BookKinds(a.Length > 3 ? a[3] : null);
                int n = I(what);
                for (int i = 0; i < n && kinds.Count > 0; i++)
                {
                    held.TryAdd(BookUtility.MakeBook(kinds[i % kinds.Count], ArtGenerationContext.Colony));
                }
            }
            Log.Message("[IsoRim probe] bookcase " + b.Position + " holds " + b.HeldBooks.Count + ": "
                + string.Join(" ", b.HeldBooks.Select(k => k.def.defName)));
        }

        private static void HaulBook(Map map, string[] a)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(a[0]);
            Building_Bookcase b = BookcaseAt(map, I(a[3]), I(a[4]));
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed);
            if (b == null || pawn == null)
            {
                Log.Warning("[IsoRim probe] haulbook: no bookcase or no colonist");
                return;
            }
            Book book = BookUtility.MakeBook(def, ArtGenerationContext.Colony);
            GenSpawn.Spawn(book, new IntVec3(I(a[1]), 0, I(a[2])), map);
            if (pawn.drafter != null)
            {
                pawn.drafter.Drafted = false;
            }
            Verse.AI.Job job = Verse.AI.HaulAIUtility.HaulToContainerJob(pawn, book, b);
            pawn.jobs.StartJob(job, Verse.AI.JobCondition.InterruptForced);
            Log.Message("[IsoRim probe] haulbook: " + pawn.LabelShort + " at " + pawn.Position + " hauls " + book.def.defName + " from " + book.Position
                + " into the bookcase at " + b.Position + " (" + job.def.defName + ")");
        }

        private static int bookCheckAt = -1;

        private static void BookCheckDue()
        {
            if (bookCheckAt >= 0 && Time.frameCount >= bookCheckAt)
            {
                bookCheckAt = -1;
                if (Find.CurrentMap != null)
                {
                    BookCheck(Find.CurrentMap);
                }
            }
        }

        private static void BookCheck(Map map)
        {
            Log.Message("[IsoRim probe] book colours: " + Bookcases.ColourList());
            foreach (Building_Bookcase b in map.listerThings.AllThings.OfType<Building_Bookcase>())
            {
                ModelEntry entry = Forms.For(b.def).model;
                bool stands = SectionLayer_IsoRimBodies.StandsIn(b);
                int expect = stands ? Mathf.Min(b.HeldBooks.Count, Bookcases.Room(entry)) : 0;
                int printed;
                if (!Bookcases.Printed.TryGetValue(b.thingIDNumber, out printed) || !stands)
                {
                    printed = 0;
                }
                Log.Message("[IsoRim probe] bookcheck " + b.def.defName + " " + b.Position + " rot " + b.Rotation.ToStringHuman()
                    + " model " + stands + " held " + b.HeldBooks.Count + " baked " + printed + (printed == expect ? " OK" : " MISMATCH"));
            }
        }

        private static bool uiPatched;
        private static bool powerPatched;
        private static readonly HashSet<Thing> powered = new HashSet<Thing>();

        // power=x,z: the building counts as powered (the harness scenes have no power grid).
        public static void PoweredPostfix(CompPowerTrader __instance, ref bool __result)
        {
            if (!__result && powered.Contains(__instance.parent))
            {
                __result = true;
            }
        }

        private static int perfLeft;
        private static string perfLabel;
        private static readonly List<float> perfDt = new List<float>();
        private static readonly List<float> perfTick = new List<float>();
        private static readonly List<float> perfUpdate = new List<float>();
        private static readonly List<float> perfDynamic = new List<float>();
        private static readonly List<float> perfMesh = new List<float>();
        private static double updMs, dynMs, meshMs;
        private static long updStart, dynStart, meshStart;
        private static bool perfHooked;

        // CPU time of the game's whole frame update (Root_Play.Update), of the per-frame thing drawing
        // (DynamicDrawManager.DrawDynamicThings) and of the map mesh drawing (MapDrawer.DrawMapMesh):
        // the frame time itself is held at the screen's refresh rate by the swapchain.
        private static void PerfHooks()
        {
            if (perfHooked)
            {
                return;
            }
            perfHooked = true;
            Harmony h = new Harmony("peren.isorim17.probeperf");
            h.Patch(AccessTools.Method(typeof(Root_Play), "Update"), new HarmonyMethod(typeof(Probe), nameof(UpdBegin)), new HarmonyMethod(typeof(Probe), nameof(UpdEnd)));
            h.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"), new HarmonyMethod(typeof(Probe), nameof(DynBegin)), new HarmonyMethod(typeof(Probe), nameof(DynEnd)));
            h.Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"), new HarmonyMethod(typeof(Probe), nameof(MeshBegin)), new HarmonyMethod(typeof(Probe), nameof(MeshEnd)));
        }

        public static void UpdBegin() { updStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void UpdEnd() { updMs += (System.Diagnostics.Stopwatch.GetTimestamp() - updStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        public static void DynBegin() { dynStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void DynEnd() { dynMs += (System.Diagnostics.Stopwatch.GetTimestamp() - dynStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        public static void MeshBegin() { meshStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void MeshEnd() { meshMs += (System.Diagnostics.Stopwatch.GetTimestamp() - meshStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        // The realtime-body layer's meshes over the whole map: sections holding any, draws (sub-meshes), vertices.
        private static string BodyMeshes()
        {
            Map map = Find.CurrentMap;
            Array secs = map != null ? MapSections.SectionsOf(map) : null;
            MethodInfo gl = AccessTools.Method(AccessTools.TypeByName("Verse.Section"), "GetLayer", new[] { typeof(Type) });
            int sections = 0, subs = 0, verts = 0;
            if (secs != null && gl != null)
            {
                foreach (object s in secs)
                {
                    SectionLayer l = gl.Invoke(s, new object[] { typeof(SectionLayer_IsoRimBodies) }) as SectionLayer;
                    if (l == null)
                    {
                        continue;
                    }
                    int before = subs;
                    foreach (LayerSubMesh sm in l.subMeshes)
                    {
                        if (sm.finalized && sm.mesh != null && sm.mesh.vertexCount > 0)
                        {
                            subs++;
                            verts += sm.mesh.vertexCount;
                        }
                    }
                    if (subs > before)
                    {
                        sections++;
                    }
                }
            }
            return "body layer: " + sections + " sections, " + subs + " sub-meshes, " + verts + " vertices";
        }

        private static float Median(List<float> l)
        {
            List<float> c = new List<float>(l);
            c.Sort();
            return c.Count > 0 ? c[c.Count / 2] : 0f;
        }

        private static int bodiesDrawn;
        private static int bodiesHeld;

        // Called by Patches.ThingDrawAt_Prefix for every realtime building body (harness counter;
        // only things with a hand-made model count).
        public static void CountBody(Thing t, bool held)
        {
            ModelEntry e;
            if (perfLeft > 0 && ModelLibrary.TryGet(t.def, out e) && e.HasModel)
            {
                if (held)
                {
                    bodiesHeld++;
                }
                else
                {
                    bodiesDrawn++;
                }
            }
        }

        private static void PerfFrame(float dt, float tickMs)
        {
            if (perfLeft <= 0)
            {
                updMs = dynMs = meshMs = 0;
                return;
            }
            perfDt.Add(dt);
            perfTick.Add(tickMs);
            perfUpdate.Add((float)updMs);
            perfDynamic.Add((float)dynMs);
            perfMesh.Add((float)meshMs);
            updMs = dynMs = meshMs = 0;
            if (--perfLeft > 0)
            {
                return;
            }
            int n = perfDt.Count;
            List<float> d = new List<float>(perfDt);
            d.Sort();
            List<float> k = new List<float>(perfTick);
            k.Sort();
            float mean = 0f;
            for (int i = 0; i < n; i++)
            {
                mean += d[i];
            }
            mean /= n;
            Log.Message("[IsoRim probe] perf " + perfLabel + ": " + n + " frames, frame median " + d[n / 2].ToString("0.00") + " ms, mean "
                + mean.ToString("0.00") + ", p95 " + d[Mathf.Min(n - 1, (int)(n * 0.95f))].ToString("0.00") + ", IsoRim tick median "
                + k[n / 2].ToString("0.000") + " ms; CPU medians: frame update " + Median(perfUpdate).ToString("0.000") + " ms, dynamic things "
                + Median(perfDynamic).ToString("0.000") + " ms, map mesh " + Median(perfMesh).ToString("0.000") + " ms; modelled realtime bodies per frame: game drew " + (bodiesDrawn / (float)n).ToString("0.0")
                + ", held back " + (bodiesHeld / (float)n).ToString("0.0") + "; " + BodyMeshes() + "; 3D " + View3D.Active + ", targetFrameRate " + Application.targetFrameRate);
        }

        public static void OverlaysOn()
        {
            Find.UIRoot.screenshotMode.Active = false;
            if (overlay == "home" && Find.CurrentMap != null)
            {
                Find.CurrentMap.areaManager.Home.MarkForDraw();
            }
        }

        public static void OverlaysOff()
        {
            Find.UIRoot.screenshotMode.Active = true;
        }

        // Every frame: what has to be kept up through the shots.
        private static void Sticky()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            if (overlay == "power")
            {
                OverlayDrawHandler.DrawPowerGridOverlayThisFrame();
            }
            foreach (IntVec3 c in keepSelected)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = c.GetThingList(map);
                Thing t = null;
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Pawn || things[i].def.selectable)
                    {
                        t = things[i];
                        if (t is Pawn)
                        {
                            break;
                        }
                    }
                }
                if (t != null && !Find.Selector.IsSelected(t))
                {
                    Find.Selector.Select(t, false, false);
                }
            }
        }
    }
}
