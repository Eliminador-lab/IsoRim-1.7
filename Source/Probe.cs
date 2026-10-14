using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only camera driver and frame-time log (the 1.7 screenshot harness cannot press keys).
    //
    // Active only with dev mode on, "-isorim3d" on the command line AND a file IsoRimProbe.txt in
    // the save data folder (e.g. D:\RimWorld17Shot\IsoRimProbe.txt). Players never have all three.
    // Each line of the file is one pass: "key=value ..." with keys turn (eighth turns), snap45
    // (0/1) and pitch (isometric camera angle). Pass k is applied a few frames after the k-th jump
    // of the game clock (Capture17 jumps it at the start of every "time" of its layout); '#' lines
    // are comments. Example: Mods\Capture17\layout_isorim_quick.probe.txt (copy it to the save data
    // folder for a run, delete it after). For about 1.5 s after each change it logs the frame times
    // and IsoRim's own per-frame work (View3D.Tick, which includes any map rebuilds), as
    // "[IsoRim probe] ..." lines.
    public static class Probe
    {
        private const int ApplyDelay = 10;     // frames after the clock jump (the harness respawns pawns then)
        private const float Window = 1.5f;     // seconds measured after a change
        private const int Before = 8;          // frames measured before a change, for the usual frame time

        public static readonly bool On = Prefs.DevMode && GenCommandLine.CommandLineArgPassed("isorim3d") && File.Exists(FilePath);

        private static List<string> passes;
        private static int pass = -1;
        private static int lastTicks = -1;
        private static int applyFrame = -1;
        private static string label;

        private static readonly List<float> beforeDt = new List<float>();
        private static bool measuring;
        private static float measureStart;
        private static int frames;
        private static float maxDt;
        private static float maxTick;
        private static int maxTickFrame;
        private static double sumTick;
        private static int lastBusyFrame;
        private static long tickStart;

        private static string FilePath
        {
            get { return Path.Combine(GenFilePaths.SaveDataFolderPath, "IsoRimProbe.txt"); }
        }

        // Start of View3D.Tick.
        public static void Begin()
        {
            if (!On)
            {
                return;
            }
            tickStart = Stopwatch.GetTimestamp();
            if (passes == null)
            {
                passes = new List<string>();
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith("#"))
                    {
                        passes.Add(line);
                    }
                }
                Log.Message("[IsoRim probe] " + passes.Count + " passes");
            }
            if (Current.Game == null || Find.TickManager == null)
            {
                return;
            }
            int ticks = Find.TickManager.TicksGame;
            if (lastTicks >= 0 && Math.Abs(ticks - lastTicks) > 1000)
            {
                pass++;
                applyFrame = Time.frameCount + ApplyDelay;
            }
            lastTicks = ticks;

            if (applyFrame >= 0 && Time.frameCount >= applyFrame)
            {
                applyFrame = -1;
                if (pass < passes.Count)
                {
                    Apply(passes[pass]);
                }
            }
        }

        private static void Apply(string line)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            foreach (string part in line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2)
                {
                    continue;
                }
                switch (kv[0])
                {
                    case "turn":
                        st.turn = int.Parse(kv[1]);
                        break;
                    case "snap45":
                        st.snap45 = kv[1] == "1";
                        break;
                    case "pitch":
                        st.isoPitch = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "hour":
                        SetLocalHour(float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }
            label = "pass " + pass + " (" + line + ")";
            measuring = true;
            measureStart = Time.realtimeSinceStartup;
            frames = 0;
            maxDt = 0f;
            maxTick = 0f;
            maxTickFrame = 0;
            sumTick = 0;
            lastBusyFrame = -1;
        }

        // Moves the clock to the given local hour of the shown map (the harness only sets whole
        // hours), without counting as the start of a new pass.
        private static void SetLocalHour(float hour)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            TickManager tm = Find.TickManager;
            int delta = Mathf.RoundToInt((hour - GenLocalDate.HourFloat(map)) * GenDate.TicksPerHour);
            tm.DebugSetTicksGame(tm.TicksGame + delta);
            lastTicks = tm.TicksGame;
            Log.Message("[IsoRim probe] local hour " + GenLocalDate.HourFloat(map).ToString("0.000")
                + ", sun glow " + GenCelestial.CurCelestialSunGlow(map).ToString("0.000"));
        }

        // End of View3D.Tick.
        public static void End()
        {
            if (!On || tickStart == 0)
            {
                return;
            }
            float tickMs = (float)((Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency);
            float dt = Time.unscaledDeltaTime * 1000f;
            if (!measuring)
            {
                beforeDt.Add(dt);
                if (beforeDt.Count > Before)
                {
                    beforeDt.RemoveAt(0);
                }
                return;
            }
            frames++;
            sumTick += tickMs;
            if (frames > 1 && dt > maxDt)
            {
                maxDt = dt;   // frame 1's delta time is the frame before the change
            }
            if (tickMs > maxTick)
            {
                maxTick = tickMs;
                maxTickFrame = frames;
            }
            if (tickMs > 1f)
            {
                lastBusyFrame = frames;
            }
            if (Time.realtimeSinceStartup - measureStart >= Window)
            {
                measuring = false;
                List<float> b = new List<float>(beforeDt);
                b.Sort();
                float usual = b.Count > 0 ? b[b.Count / 2] : 0f;
                Log.Message("[IsoRim probe] " + label + ": frames " + frames
                    + ", usual frame " + usual.ToString("0.0") + " ms, max frame " + maxDt.ToString("0.0") + " ms"
                    + ", IsoRim tick max " + maxTick.ToString("0.0") + " ms (frame " + maxTickFrame + ")"
                    + ", sum " + sumTick.ToString("0.0") + " ms, last frame > 1 ms: " + lastBusyFrame
                    + ", yaw " + View3D.Yaw.ToString("0") + ", pitch " + View3D.Pitch.ToString("0.0"));
                beforeDt.Clear();
            }
        }
    }
}
