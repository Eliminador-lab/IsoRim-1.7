using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only scene keys for the probe file (see Probe): things the screenshot harness's
    // layouts cannot set up, for the 3D render checks. Cells are map cells (x,z).
    //   set=<setting>,<value>        an IsoRim setting (bool 0/1, int or float)
    //   delay=<frames>               apply this pass that many frames after the clock jump (default 10)
    //   fire=x,z                     a fire in the cell (also on a wall)
    //   gas=x,z,<GasType>,<radius>   a gas cloud
    //   snow=<depth>,x0,z0,x1,z1     snow over a rectangle
    //   terrain=<Def>,x0,z0,x1,z1    terrain over a rectangle
    //   spawn=<Def>,x,z[,<Stuff>]    a thing (the player's)
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
                        : f.FieldType == typeof(int) ? (object)I(a[1]) : (object)F(a[1]);
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
                    ThingDef stuff = a.Length > 3 ? DefDatabase<ThingDef>.GetNamed(a[3]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Thing t = ThingMaker.MakeThing(def, stuff);
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
                default:
                    Log.Warning("[IsoRim probe] unknown key " + key);
                    return;
            }
            Log.Message("[IsoRim probe] " + key + "=" + string.Join(",", a));
        }

        private static bool uiPatched;

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
