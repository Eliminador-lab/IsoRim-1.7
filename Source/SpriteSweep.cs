using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness only (probe keys spritesweep=1, itemsheet=..., spritestats=1): checks the CPU sprite
    // sampling (SpritePixels) over every def the mod extrudes or colour-samples, and lays items out
    // in a grid for contact-sheet renders. Never runs in normal play (Probe.On needs dev mode,
    // -isorim3d and a probe file).
    public static class SpriteSweep
    {
        private static readonly AccessTools.FieldRef<Graphic_Collection, Graphic[]> subRef =
            AccessTools.FieldRefAccess<Graphic_Collection, Graphic[]>("subGraphics");

        private static readonly List<Thing> placed = new List<Thing>();
        private static readonly StringBuilder pixelLog = new StringBuilder();

        private static void LogPixels(string what, Color32[] a, Color32[] b, int n)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (Solid(a[i]) != Solid(b[i]) || Math.Abs(a[i].r - b[i].r) > 1 || Math.Abs(a[i].g - b[i].g) > 1 || Math.Abs(a[i].b - b[i].b) > 1 || Math.Abs(a[i].a - b[i].a) > 1)
                {
                    pixelLog.Append(what).Append('\t').Append(i % n).Append(',').Append(i / n).Append("\tcpu ").Append(a[i].ToString()).Append("\tgpu ").Append(b[i].ToString()).Append('\n');
                }
            }
        }

        private static string Dir
        {
            get { return GenFilePaths.SaveDataFolderPath; }
        }

        // The defs drawn extruded (with the current settings), items first by name.
        public static List<ThingDef> ExtrudedDefs()
        {
            return DefDatabase<ThingDef>.AllDefs.Where(d => d.graphicData != null && Forms.For(d).form == Form3D.Extruded)
                .OrderBy(d => d.category == ThingCategory.Item ? 0 : 1).ThenBy(d => d.defName, StringComparer.Ordinal).ToList();
        }

        // Every leaf graphic of a def: stack-count sizes, random variants, all collection members.
        private static void Leaves(Graphic g, List<Graphic> into, int depth = 0)
        {
            if (g == null || depth > 4)
            {
                return;
            }
            Graphic_Collection col = g as Graphic_Collection;
            Graphic[] subs = col != null ? subRef(col) : null;
            if (subs != null && subs.Length > 0)
            {
                foreach (Graphic s in subs)
                {
                    Leaves(s, into, depth + 1);
                }
                return;
            }
            into.Add(g);
        }

        private static bool Solid(Color32 c)
        {
            return c.a >= 128;
        }

        private static int MaskDiff(Color32[] a, Color32[] b, out int colMax)
        {
            int diff = 0;
            colMax = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool sa = Solid(a[i]);
                if (sa != Solid(b[i]))
                {
                    diff++;
                }
                else if (sa)
                {
                    colMax = Math.Max(colMax, Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b))));
                }
            }
            return diff;
        }

        private static string Hex(Color c)
        {
            return ((Color32)c).r.ToString("X2") + ((Color32)c).g.ToString("X2") + ((Color32)c).b.ToString("X2");
        }

        public static void Run()
        {
            Stopwatch total = Stopwatch.StartNew();
            int N = Extruder.MaskSize;
            List<ThingDef> defs = ExtrudedDefs();
            StringBuilder tsv = new StringBuilder();
            tsv.AppendLine("def\tcategory\tgraphic\tvariant\tflip\ttexture\tw\th\tformat\tsheet\treadableBefore\tstatus\tsolid\truns\tlids\tborder\tmean\tsheetVsDirect\tgpuMaskDiff\tgpuColMax\tms");
            Dictionary<string, List<string>> anomalies = new Dictionary<string, List<string>>();
            Action<string, string> flag = (kind, what) =>
            {
                List<string> l;
                if (!anomalies.TryGetValue(kind, out l))
                {
                    anomalies[kind] = l = new List<string>();
                }
                if (!l.Contains(what))
                {
                    l.Add(what);
                }
            };
            List<KeyValuePair<double, string>> slow = new List<KeyValuePair<double, string>>();
            int rows = 0, exceptions = 0, oracleRows = 0, oracleExact = 0, sheetRows = 0, sheetExact = 0;
            int maxOracleMask = 0, maxOracleCol = 0;
            HashSet<Texture> textures = new HashSet<Texture>();
            int u0 = SpritePixels.Unreadable;

            foreach (ThingDef def in defs)
            {
                List<Graphic> leaves = new List<Graphic>();
                try
                {
                    Leaves(def.graphic, leaves);
                }
                catch (Exception e)
                {
                    exceptions++;
                    flag("exception", def.defName + " (graphic: " + e.GetType().Name + ")");
                    continue;
                }
                HashSet<Material> seen = new HashSet<Material>();
                double defMs = 0;
                for (int v = 0; v < leaves.Count; v++)
                {
                    for (int rot = 0; rot < 4; rot++)
                    {
                        Material mat;
                        try
                        {
                            mat = leaves[v].MatAt(new Rot4(rot));
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (mat == null || !seen.Add(mat))
                        {
                            continue;
                        }
                        for (int f = 0; f < 2; f++)
                        {
                            bool flip = f == 1;
                            string status = "ok";
                            string texName = "", format = "";
                            int tw = 0, th = 0, solid = 0, runs = 0, lids = 0;
                            bool border = false, sheet = false, readable = false;
                            string mean = "", sheetVsDirect = "", gpuMask = "", gpuCol = "";
                            Stopwatch w = Stopwatch.StartNew();
                            try
                            {
                                Texture tex = mat.mainTexture;
                                if (tex == null)
                                {
                                    status = "notexture";
                                    flag("no texture", def.defName);
                                }
                                else
                                {
                                    textures.Add(tex);
                                    texName = tex.name;
                                    tw = tex.width;
                                    th = tex.height;
                                    Texture2D t2 = tex as Texture2D;
                                    if (t2 != null && t2.CoreTexture != null)
                                    {
                                        readable = t2.CoreTexture.IsReadable;
                                        format = t2.CoreTexture.CpuData != null ? t2.CoreTexture.CpuData.Format.ToString() : "none";
                                    }
                                    // As the map mesh would print it: through the texture sheet if the sprite is on one.
                                    Material printed;
                                    Vector2[] uvs;
                                    Color32 vc;
                                    Graphic.TryGetTextureAtlasReplacementInfo(mat, def.category.ToAtlasGroup(), flip, true, out printed, out uvs, out vc);
                                    Vector2 dScale = flip ? new Vector2(-1f, 1f) : Vector2.one;
                                    Vector2 dOffset = flip ? new Vector2(1f, 0f) : Vector2.zero;
                                    Color32[] direct = SpritePixels.Sample(tex, dScale, dOffset, N);
                                    Color32[] used = direct;
                                    if (printed != null && printed != mat && uvs != null && printed.mainTexture != null)
                                    {
                                        sheet = true;
                                        Vector2 across = uvs[3] - uvs[0];
                                        Vector2 up = uvs[1] - uvs[0];
                                        Vector2 ts = printed.mainTextureScale;
                                        Vector2 to = printed.mainTextureOffset;
                                        Vector2 scale = new Vector2(across.x * ts.x, up.y * ts.y);
                                        Vector2 offset = new Vector2(uvs[0].x * ts.x + to.x, uvs[0].y * ts.y + to.y);
                                        used = SpritePixels.Sample(printed.mainTexture, scale, offset, N);
                                        if (used != null && direct != null)
                                        {
                                            sheetRows++;
                                            int cm;
                                            int d = MaskDiff(used, direct, out cm);
                                            sheetVsDirect = d + "/" + cm;
                                            if (d == 0 && cm == 0)
                                            {
                                                sheetExact++;
                                            }
                                            else
                                            {
                                                flag("sheet part differs from its sprite", def.defName + " " + texName);
                                            }
                                        }
                                    }
                                    if (!flip && direct != null)
                                    {
                                        // Oracle: the 1.5 method (GPU Blit + ReadPixels) is still right for a whole texture.
                                        Color32[] gpu = SpritePixels.GpuSample(tex, N);
                                        if (gpu != null)
                                        {
                                            oracleRows++;
                                            int cm;
                                            int d = MaskDiff(direct, gpu, out cm);
                                            gpuMask = d.ToString();
                                            gpuCol = cm.ToString();
                                            maxOracleMask = Math.Max(maxOracleMask, d);
                                            maxOracleCol = Math.Max(maxOracleCol, cm);
                                            if (d == 0 && cm <= 1)
                                            {
                                                oracleExact++;
                                            }
                                            else
                                            {
                                                flag("CPU vs GPU sample differs", def.defName + " " + texName + " mask " + d + " col " + cm);
                                                LogPixels(def.defName + "\t" + texName + "\t" + tw + "x" + th, direct, gpu, N);
                                            }
                                        }
                                    }
                                    if (used == null)
                                    {
                                        status = "unreadable";
                                        flag("unreadable", def.defName + " " + texName + " (" + format + ")");
                                    }
                                    else
                                    {
                                        for (int k = 0; k < used.Length; k++)
                                        {
                                            if (Solid(used[k]))
                                            {
                                                solid++;
                                                int x = k % N, y = k / N;
                                                if (x == 0 || y == 0 || x == N - 1 || y == N - 1)
                                                {
                                                    border = true;
                                                }
                                            }
                                        }
                                        Extruder.Mask m = Extruder.FromPixels(used);
                                        if (m == null)
                                        {
                                            status = "empty";
                                            flag("empty mask", def.defName + " " + texName);
                                        }
                                        else
                                        {
                                            runs = m.runs.Count;
                                            lids = m.lids.Count;
                                            mean = Hex(m.mean);
                                            Color c = m.mean;
                                            if (solid == N * N)
                                            {
                                                flag("full mask", def.defName + " " + texName);
                                            }
                                            if (border)
                                            {
                                                flag("touches border", def.defName + " " + texName);
                                            }
                                            if (c.r < 0.03f && c.g < 0.03f && c.b < 0.03f)
                                            {
                                                flag("black mean", def.defName + " " + texName);
                                            }
                                            else if (c.r > 0.97f && c.g > 0.97f && c.b > 0.97f)
                                            {
                                                flag("white mean", def.defName + " " + texName);
                                            }
                                            else if (c.r > 0.85f && c.b > 0.85f && c.g < 0.15f)
                                            {
                                                flag("magenta mean", def.defName + " " + texName);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                exceptions++;
                                status = "exception " + e.GetType().Name;
                                flag("exception", def.defName + " " + e.GetType().Name + ": " + e.Message);
                            }
                            double ms = w.Elapsed.TotalMilliseconds;
                            defMs += ms;
                            rows++;
                            tsv.Append(def.defName).Append('\t').Append(def.category).Append('\t').Append(leaves[v].GetType().Name).Append('\t').Append(v + "/" + rot)
                                .Append('\t').Append(flip ? 1 : 0).Append('\t').Append(texName).Append('\t').Append(tw).Append('\t').Append(th).Append('\t').Append(format)
                                .Append('\t').Append(sheet ? 1 : 0).Append('\t').Append(readable ? 1 : 0).Append('\t').Append(status).Append('\t').Append(solid)
                                .Append('\t').Append(runs).Append('\t').Append(lids).Append('\t').Append(border ? 1 : 0).Append('\t').Append(mean)
                                .Append('\t').Append(sheetVsDirect).Append('\t').Append(gpuMask).Append('\t').Append(gpuCol)
                                .Append('\t').Append(ms.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine();
                        }
                    }
                }
                slow.Add(new KeyValuePair<double, string>(defMs, def.defName));
            }
            File.WriteAllText(Path.Combine(Dir, "IsoRimSpriteSweep.tsv"), tsv.ToString());

            // SpriteColors: the whole-sprite mean of every def with a graphic, CPU vs the 1.5 GPU way.
            StringBuilder ctsv = new StringBuilder("def\tcategory\ttexture\tcpu\tgpu\tmaxDiff\n");
            int colourDefs = 0, colourExact = 0, colourNull = 0, colourMax = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                if (def.graphicData == null)
                {
                    continue;
                }
                try
                {
                    Graphic g = def.graphic;
                    Material mat = g != null ? g.MatSingle : null;
                    Texture tex = mat != null ? mat.mainTexture : null;
                    if (tex == null)
                    {
                        continue;
                    }
                    colourDefs++;
                    Color32[] cpuPx = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, SpriteColors.SampleSize);
                    string cpuInfo = SpritePixels.LastInfo;
                    Color32[] gpuPx = SpritePixels.GpuSample(tex, SpriteColors.SampleSize);
                    if (cpuPx == null)
                    {
                        colourNull++;
                        flag("colour: unreadable", def.defName + " " + tex.name);
                        continue;
                    }
                    Color32 a = SpriteColors.MeanOf(cpuPx);
                    Color32 b = gpuPx != null ? (Color32)SpriteColors.MeanOf(gpuPx) : new Color32(0, 0, 0, 0);
                    int d = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
                    colourMax = Math.Max(colourMax, d);
                    if (d <= 1)
                    {
                        colourExact++;
                    }
                    else
                    {
                        flag("colour: CPU vs GPU differs", def.defName + " " + tex.name + " " + d);
                        if (gpuPx != null) LogPixels(def.defName + "\t" + tex.name + "\t" + tex.width + "x" + tex.height + "\tcolour " + cpuInfo, cpuPx, gpuPx, SpriteColors.SampleSize);
                    }
                    ctsv.Append(def.defName).Append('\t').Append(def.category).Append('\t').Append(tex.name).Append('\t').Append(Hex(a)).Append('\t').Append(Hex(b))
                        .Append('\t').Append(d).Append('\n');
                }
                catch (Exception e)
                {
                    exceptions++;
                    flag("exception", def.defName + " (colour) " + e.GetType().Name + ": " + e.Message);
                }
            }
            File.WriteAllText(Path.Combine(Dir, "IsoRimSpriteColours.tsv"), ctsv.ToString());
            File.WriteAllText(Path.Combine(Dir, "IsoRimSpritePixels.tsv"), pixelLog.ToString());
            pixelLog.Length = 0;

            slow.Sort((x, y) => y.Key.CompareTo(x.Key));
            StringBuilder s = new StringBuilder();
            s.AppendLine("[IsoRim probe] sprite sweep: " + defs.Count + " extruded defs (" + defs.Count(d => d.category == ThingCategory.Item) + " items), "
                + rows + " samples, " + textures.Count + " textures, " + exceptions + " exceptions, unreadable " + (SpritePixels.Unreadable - u0)
                + ", in " + total.Elapsed.TotalSeconds.ToString("0.0") + " s");
            s.AppendLine("  sheet part vs its sprite: " + sheetExact + "/" + sheetRows + " identical");
            s.AppendLine("  CPU vs GPU (whole texture, 1.5 method): " + oracleExact + "/" + oracleRows + " same mask and colours within 1/255; worst mask " + maxOracleMask
                + " px, worst colour " + maxOracleCol + "/255");
            s.AppendLine("  SpriteColors CPU vs GPU: " + colourExact + "/" + colourDefs + " within 1/255, worst " + colourMax + "/255, unreadable " + colourNull);
            s.AppendLine("  slowest: " + string.Join(", ", slow.Take(8).Select(p => p.Value + " " + p.Key.ToString("0.0") + " ms")));
            foreach (KeyValuePair<string, List<string>> kv in anomalies.OrderBy(k => k.Key))
            {
                s.AppendLine("  " + kv.Key + " (" + kv.Value.Count + "): " + string.Join("; ", kv.Value.Take(60)) + (kv.Value.Count > 60 ? "; ..." : ""));
            }
            File.WriteAllText(Path.Combine(Dir, "IsoRimSpriteSweep.txt"), s.ToString());
            Log.Message(s.ToString());
            Stats();
        }

        public static void Stats()
        {
            Log.Message("[IsoRim probe] sprite pixels: " + SpritePixels.Samples + " samples in " + SpritePixels.SampleMs.ToString("0.0") + " ms (texture reads "
                + SpritePixels.LevelReads + " in " + SpritePixels.ReadMs.ToString("0.0") + " ms, reloaded " + SpritePixels.Reloaded + ", unreadable "
                + SpritePixels.Unreadable + ", no sheet tile " + SpritePixels.NoTile + "), cache now " + (SpritePixels.CachedBytes / 1024) + " KB, peak "
                + (SpritePixels.PeakBytes / 1024) + " KB; masks " + Extruder.CacheCount);
        }

        // Lays batch k of the extruded items out in a cols x rows grid from (x0, z0), one per cell,
        // replacing the last batch. Stackable items lie as half stacks.
        public static void PlaceSheet(Map map, int batch, int x0, int z0, int cols, int rows)
        {
            foreach (Thing t in placed)
            {
                if (t != null && t.Spawned)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
            placed.Clear();
            List<ThingDef> items = ExtrudedDefs().Where(d => d.category == ThingCategory.Item).ToList();
            int per = cols * rows;
            int first = batch * per;
            string firstName = null, lastName = null;
            List<string> failed = new List<string>();
            for (int k = 0; k < per && first + k < items.Count; k++)
            {
                ThingDef def = items[first + k];
                IntVec3 cell = new IntVec3(x0 + k % cols, 0, z0 + (rows - 1 - k / cols));
                try
                {
                    ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    if (def.stackLimit > 1)
                    {
                        t.stackCount = Math.Max(1, def.stackLimit / 2);
                    }
                    GenSpawn.Spawn(t, cell, map, Rot4.North);
                    placed.Add(t);
                }
                catch (Exception e)
                {
                    failed.Add(def.defName + " (" + e.GetType().Name + ")");
                }
                firstName = firstName ?? def.defName;
                lastName = def.defName;
            }
            string line = "sheet " + batch + "\t" + first + "\t" + (first + per - 1) + "\t" + firstName + "\t" + lastName + "\tfailed: " + string.Join(", ", failed);
            File.AppendAllText(Path.Combine(Dir, "IsoRimSheets.tsv"), line + "\n");
            Log.Message("[IsoRim probe] " + line + " (of " + items.Count + " items)");
        }
    }
}
