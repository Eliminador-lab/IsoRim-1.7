using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The last things IsoRim noted before a crash (3D view switched, dialogs opened in 3D, meshes left out), kept in
    // a short ring and added to the engine's crash report (<log folder>\Crashes\crash-*.txt), so a "graphics device
    // was lost" report shows what the 3D view was doing. Lines can also go to the player's log as they happen.
    public static class CrashTrail
    {
        private const int Keep = 16;
        private static readonly Queue<string> lines = new Queue<string>();
        private static bool registered;

        public static void Note(string line, bool toLog = true)
        {
            int frame = Time.frameCount;
            if (toLog)
            {
                Log.Message("[IsoRim] " + line + " (frame " + frame + ")");
            }
            lock (lines)
            {
                if (lines.Count >= Keep)
                {
                    lines.Dequeue();
                }
                lines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " f" + frame + " " + line);
            }
            if (!registered)
            {
                registered = true;
                try
                {
                    Waylith.Diagnostics.CrashDiagnostics.Register(Report);
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] Could not add to the crash report: " + e.Message);
                }
            }
        }

        // The crash report's IsoRim block (called by the engine while it writes the report, maybe off the main thread).
        public static string Report()
        {
            lock (lines)
            {
                return "IsoRim: 3D " + (View3D.Active ? "on" : "off") + "; meshes checked " + MeshGuard.Checked + ", left out " + MeshGuard.Dropped
                    + "; live textures made " + LiveTexture.Created + ", rewritten in place " + LiveTexture.InPlace + ", remade " + LiveTexture.Fallbacks
                    + "\nIsoRim trail (newest last):\n  " + string.Join("\n  ", lines);
            }
        }
    }
}
