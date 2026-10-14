using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for performance runs (see Probe; measure with perf=N,label in the same pass):
    //   window=<seconds>                      how long each pass's own change is measured (default 1.5)
    //   campath=x0,z0,s0,x1,z1,s1,frames      the camera glides from one place and root size to the other
    //   spin=frames,every                     the camera turns an eighth every that many frames (Q/E held)
    //   tilt=p0,p1,frames                     the isometric pitch swept from p0 to p1 (tilting with the mouse)
    //   walls=x0,z0,x1,z1[,<Stuff>]           walls built at once over a rectangle (its outline)
    //   clear=x0,z0,x1,z1                     every building in a rectangle destroyed (deconstructed at once)
    //   save=<name>                           the game saved (temp file in the save folder: delete it after the run)
    //   loadtest=<name>                       that save loaded with 3D on, then off, then the game quits (see LoadWatch)
    public static partial class Probe
    {
        private static Vector3 camFrom, camTo;
        private static int camFrames, camFrame = -1;
        private static int spinLeft, spinEvery, spinFrame;
        private static float tiltFrom, tiltTo;
        private static int tiltFrames, tiltFrame = -1;

        private static void PerfKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "window":
                    Window = F(a[0]);
                    break;
                case "campath":
                    camFrom = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                    camTo = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                    camFrames = Math.Max(1, I(a[6]));
                    camFrame = 0;
                    break;
                case "spin":
                    spinLeft = I(a[0]);
                    spinEvery = Math.Max(1, I(a[1]));
                    spinFrame = 0;
                    break;
                case "tilt":
                    tiltFrom = F(a[0]);
                    tiltTo = F(a[1]);
                    tiltFrames = Math.Max(1, I(a[2]));
                    tiltFrame = 0;
                    break;
                case "walls":
                {
                    ThingDef stuff = DefDatabase<ThingDef>.GetNamed(a.Length > 4 ? a[4] : "BlocksGranite");
                    CellRect r = Rect(a, 0);
                    int n = 0;
                    foreach (IntVec3 c in r.EdgeCells)
                    {
                        if (c.InBounds(map) && c.GetEdifice(map) == null)
                        {
                            Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
                            w.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(w, c, map, Rot4.North);
                            n++;
                        }
                    }
                    Log.Message("[IsoRim probe] walls: " + n + " built");
                    break;
                }
                case "save":
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    GameDataSaveLoader.SaveGame(a[0]);
                    Log.Message("[IsoRim probe] saved " + a[0] + " in " + sw.ElapsedMilliseconds + " ms");
                    break;
                }
                case "loadtest":
                    loadName = a[0];
                    loadSeq = 0;
                    StartLoad(true);
                    break;
                case "clear":
                {
                    List<Thing> gone = new List<Thing>();
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map))
                        {
                            gone.AddRange(c.GetThingList(map).Where(t => t is Building && t.def.destroyable));
                        }
                    }
                    foreach (Thing t in gone.Distinct().ToList())
                    {
                        if (!t.Destroyed)
                        {
                            t.Destroy(DestroyMode.Deconstruct);
                        }
                    }
                    Log.Message("[IsoRim probe] clear: " + gone.Distinct().Count() + " buildings destroyed");
                    break;
                }
            }
        }

        // Every frame (from Sticky).
        private static void MotionTick()
        {
            if (camFrame >= 0)
            {
                float f = camFrame / (float)camFrames;
                Vector3 p = Vector3.Lerp(camFrom, camTo, f);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(p.x, 0f, p.y), p.z);
                if (++camFrame > camFrames)
                {
                    camFrame = -1;
                }
            }
            if (spinLeft > 0)
            {
                if (spinFrame++ % spinEvery == 0)
                {
                    IsoRimMod.Settings.turn++;
                }
                spinLeft--;
            }
            if (tiltFrame >= 0)
            {
                IsoRimMod.Settings.isoPitch = Mathf.Lerp(tiltFrom, tiltTo, tiltFrame / (float)tiltFrames);
                if (++tiltFrame > tiltFrames)
                {
                    tiltFrame = -1;
                }
            }
        }

        private static bool loadLogged;

        // Once, at the first frame of play: when the map was ready (map load time = this minus the log's
        // "Initializing new game" stamp) and the memory then.
        private static void LogLoaded()
        {
            LoadWatch();
            if (loadLogged || Find.CurrentMap == null)
            {
                return;
            }
            loadLogged = true;
            System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Message("[IsoRim probe] first frame of play at " + Time.realtimeSinceStartup.ToString("0.00") + " s, " + DateTime.Now.ToString("HH:mm:ss.fff")
                + ", map " + Find.CurrentMap.Size + ", 3D " + View3D.Active + ", things " + Find.CurrentMap.listerThings.AllThings.Count
                + ", pawns " + Find.CurrentMap.mapPawns.AllPawnsSpawnedCount + ", managed " + (GC.GetTotalMemory(false) / 1048576.0).ToString("0")
                + " MB, working set " + (proc.WorkingSet64 / 1048576.0).ToString("0") + " MB");
        }

        // loadtest=<save>: the save loaded with the 3D view on, then again with it off, then the game quits
        // (the harness would otherwise rebuild its scene over the loaded map). Logged per load: seconds
        // to the first frame of play, the slowest of the next 120 frames and their total, memory.
        private static string loadName;
        private static int loadSeq = -1;
        private static Game loadGame;
        private static float loadStart;
        private static float loadReady;
        private static int loadFrames = -1;
        private static float loadMax;
        private static float loadSum;

        private static void StartLoad(bool on)
        {
            // The harness would rebuild its scene over the loaded map: switched off for the rest of the run.
            System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
            if (cap != null)
            {
                cap.SetValue(null, false);
            }
            View3D.Active = on;
            loadGame = Current.Game;
            loadStart = Time.realtimeSinceStartup;
            loadFrames = -1;
            Log.Message("[IsoRim probe] loading " + loadName + " with 3D " + on);
            GameDataSaveLoader.LoadGame(loadName);
        }

        private static void LoadWatch()
        {
            if (loadSeq < 0 || Current.Game == loadGame || LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null
                || Find.CurrentMap.listerThings.AllThings.Count == 0)
            {
                return;
            }
            if (loadFrames < 0)
            {
                loadReady = Time.realtimeSinceStartup;
                loadFrames = 0;
                loadMax = 0f;
                loadSum = 0f;
                return;
            }
            float dt = Time.unscaledDeltaTime * 1000f;
            loadMax = Mathf.Max(loadMax, dt);
            loadSum += dt;
            if (++loadFrames < 120)
            {
                return;
            }
            System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Message("[IsoRim probe] load (3D " + View3D.Active + "): " + (loadReady - loadStart).ToString("0.00") + " s to the first frame of play; next 120 frames "
                + (loadSum / 1000f).ToString("0.00") + " s, slowest " + loadMax.ToString("0") + " ms; things " + Find.CurrentMap.listerThings.AllThings.Count
                + ", managed " + (GC.GetTotalMemory(false) / 1048576.0).ToString("0") + " MB, working set " + (proc.WorkingSet64 / 1048576.0).ToString("0")
                + " MB, private " + (proc.PrivateMemorySize64 / 1048576.0).ToString("0") + " MB");
            if (loadSeq == 0)
            {
                loadSeq = 1;
                StartLoad(false);
            }
            else
            {
                loadSeq = -1;
                Log.Message("[IsoRim probe] loadtest done, quitting");
                Root.Shutdown();
            }
        }
    }
}
