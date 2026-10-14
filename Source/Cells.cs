using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Per-cell data of a map that IsoRim's vertex shaders read (step 5 of the 1.7 port, design §14).
    //
    // One texel per cell, RGBA32, point-sampled (shaders use texelFetch on the integer cell):
    //   r, g = the top of what stands in the cell, in 1/4096 cells above the ground as a 16-bit number
    //       (the highest block, or the edifice's 3D height): the fog lid lies on it (IsoRimFog.vert.glsl);
    //   b, a = 0, kept free for the shader cut-away (pointer/colonist/room cut flags) and other
    //       per-cell states to come.
    // (A float texture would be simpler, but an RGBAFloat texture filled with SetPixels reads as 0 in
    // the shader on 1.7.4977.)
    // The game's glow lives in its own texture (IsoGlowMap): a corner grid sampled bilinear, copied
    // wholesale from the game's overlay mesh, where this one is per cell and exact.
    //
    // Cells are marked dirty when something that changes their top changes (buildings spawned or gone,
    // a block of walls rebuilt, which covers cut-away and settings changes); just before the map is
    // drawn the dirty cells are worked out again and, if any changed, the texture is uploaded once
    // (the engine has no partial upload). Owned per map, like the glow texture (design §11).
    [StaticConstructorOnStartup]
    public sealed class IsoCellMap
    {
        private static readonly Dictionary<Map, IsoCellMap> maps = new Dictionary<Map, IsoCellMap>();

        public readonly Map map;
        public readonly int width;
        public readonly int height;
        public Vector2 origin;        // where cell (0, 0) is in the world (0 today; per-map placement later)
        public float baseY;           // the map's ground height in cells (0 today; reserved for stacked maps)
        private readonly Color32[] cells;
        private readonly bool[] dirty;
        private readonly List<int> dirtyList = new List<int>();
        private bool all = true;      // everything is to be worked out (new, or after a full rebuild)
        private bool changed;
        private Texture2D tex;

        // Heights are stored in steps of 1/4096 cell as a 16-bit number (red high byte, green low byte):
        // up to 16 cells, far below a pixel at any zoom.
        public const float HeightSteps = 4096f;
        private int uploads;

        private IsoCellMap(Map map)
        {
            this.map = map;
            width = map.Size.x;
            height = map.Size.z;
            cells = new Color32[width * height];
            dirty = new bool[width * height];
            cutTarget = new byte[width * height];
            fading = new bool[width * height];
        }

        // The cell data of a map, made on first use. Maps that are gone are dropped.
        public static IsoCellMap For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            IsoCellMap c;
            if (maps.TryGetValue(map, out c))
            {
                return c;
            }
            Prune();
            c = new IsoCellMap(map);
            maps[map] = c;
            return c;
        }

        // The cell data of a map if it has been made (null otherwise); never makes it.
        public static IsoCellMap Existing(Map map)
        {
            IsoCellMap c;
            return map != null && maps.TryGetValue(map, out c) ? c : null;
        }

        // The tallest top of any cell, as last sent to the GPU (culling: the fog lid is lifted onto it).
        public float MaxTop
        {
            get { return maxTop; }
        }

        private float maxTop = 16f;

        private static readonly List<Map> gone = new List<Map>();

        private static void Prune()
        {
            List<Map> live = Find.Maps;
            foreach (Map m in maps.Keys)
            {
                if (live == null || !live.Contains(m))
                {
                    gone.Add(m);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                maps[gone[i]].Dispose();
                maps.Remove(gone[i]);
            }
            gone.Clear();
        }

        private void Dispose()
        {
            if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);
                tex = null;
            }
        }

        // Marks cells to be worked out again before the next draw. Ignored for a map that has no
        // cell data yet (it is worked out in full when first used).
        public static void Dirty(Map map, CellRect rect)
        {
            IsoCellMap c;
            if (map == null || !maps.TryGetValue(map, out c) || c.all)
            {
                return;
            }
            rect = rect.ClipInsideRect(new CellRect(0, 0, c.width, c.height));
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    int i = z * c.width + x;
                    if (!c.dirty[i])
                    {
                        c.dirty[i] = true;
                        c.dirtyList.Add(i);
                    }
                }
            }
        }

        // The whole map is being rebuilt: every cell is worked out again before the next draw.
        public static void Rebuilt(Map map)
        {
            IsoCellMap c;
            if (map != null && maps.TryGetValue(map, out c))
            {
                c.all = true;
            }
        }

        // The top of what stands in a cell, in cells above the ground: the highest block (as cut
        // down), or else the edifice's 3D height. The fog lid lies on it.
        public static float TopOf(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return 0f;
            }
            float h = WallBoxes.BlockHeightAt(map, c);
            Building b = c.GetEdifice(map);
            if (b != null && h <= 0f)
            {
                h = Forms.HeightOf(b);
            }
            return h;
        }

        private void Work(int i)
        {
            IntVec3 c = new IntVec3(i % width, 0, i / width);
            bool shaderCut = ShaderCut.On;
            float top;
            byte cut = 0, bits = 0;
            if (shaderCut)
            {
                // (shader cut-away) the whole height; b = how far the block is cut, a = tree thinned (1), roof open (2).
                WallBoxes.FullHeights = true;
                try
                {
                    top = TopOf(map, c);
                }
                finally
                {
                    WallBoxes.FullHeights = false;
                }
                Building b = c.GetEdifice(map);
                if (b != null && Forms.For(b.def).form == Form3D.Block && WallBoxes.IsCut(map, c, b))
                {
                    cut = 255;
                }
                if (Cutaway.TreeThinned(map, c))
                {
                    bits |= 1;
                }
                if (Cutaway.RoofOpen(map, c))
                {
                    bits |= 2;
                }
            }
            else
            {
                top = TopOf(map, c);
            }
            int v = Mathf.Clamp(Mathf.RoundToInt(top * HeightSteps), 0, 65535);
            byte hi = (byte)(v >> 8), lo = (byte)(v & 255);
            if (cells[i].r != hi || cells[i].g != lo)
            {
                cells[i].r = hi;
                cells[i].g = lo;
                changed = true;
            }
            if (cells[i].a != bits)
            {
                cells[i].a = bits;
                changed = true;
            }
            if (cutTarget[i] != cut || cells[i].b != cut)
            {
                cutTarget[i] = cut;
                if (ShaderCut.Fade <= 0f || all)
                {
                    if (cells[i].b != cut)
                    {
                        cells[i].b = cut;
                        changed = true;
                    }
                }
                else if (!fading[i])
                {
                    fading[i] = true;
                    fadingList.Add(i);
                }
            }
        }

        // (shader cut-away) cells on their way down or up (ShaderCut.Fade), one step a frame.
        private readonly byte[] cutTarget;
        private readonly bool[] fading;
        private readonly List<int> fadingList = new List<int>();

        private void Fade()
        {
            if (fadingList.Count == 0)
            {
                return;
            }
            float fade = ShaderCut.Fade;
            int step = fade <= 0f ? 255 : Mathf.Max(1, Mathf.RoundToInt(255f * Time.unscaledDeltaTime / fade));
            for (int k = fadingList.Count - 1; k >= 0; k--)
            {
                int i = fadingList[k];
                int b = cells[i].b;
                int t = cutTarget[i];
                b = b < t ? Mathf.Min(t, b + step) : Mathf.Max(t, b - step);
                cells[i].b = (byte)b;
                changed = true;
                if (b == t)
                {
                    fading[i] = false;
                    fadingList[k] = fadingList[fadingList.Count - 1];
                    fadingList.RemoveAt(fadingList.Count - 1);
                }
            }
        }

        // Works out the dirty cells and sends the texture to the GPU if anything changed, at most
        // once a frame.
        public void Update()
        {
            Stopwatch watch = uploads < 3 && ModLog.Diag ? Stopwatch.StartNew() : null;
            if (all)
            {
                all = false;
                for (int i = 0; i < cells.Length; i++)
                {
                    Work(i);
                }
                for (int i = 0; i < dirtyList.Count; i++)
                {
                    dirty[dirtyList[i]] = false;
                }
                dirtyList.Clear();
                changed = true;
            }
            else if (dirtyList.Count > 0)
            {
                for (int i = 0; i < dirtyList.Count; i++)
                {
                    dirty[dirtyList[i]] = false;
                    Work(dirtyList[i]);
                }
                dirtyList.Clear();
            }
            Fade();
            if (!changed && tex != null)
            {
                return;
            }
            if (tex == null)
            {
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                tex.name = "IsoRim_Cells";
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
            }
            tex.SetPixels32(cells);
            tex.Apply(false);
            int top = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                top = Math.Max(top, (cells[i].r << 8) | cells[i].g);
            }
            maxTop = top / HeightSteps;
            changed = false;
            uploads++;
            if (watch != null)
            {
                ModLog.Note("[IsoRim] Cell texture " + width + "x" + height + " updated in " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            }
        }

        public Texture2D Texture
        {
            get { return tex; }
        }

        // ---- per frame

        private static readonly int texId = Shader.PropertyToID("_IsoCellTex");
        private static readonly int mapId = Shader.PropertyToID("_IsoCellMap");
        private static readonly int levelId = Shader.PropertyToID("_IsoLevel");

        // Before the map's meshes are drawn: brings the shown map's cell texture up to date and hands
        // it to the shaders. Only the fog lid reads it today.
        public static void BeginFrame(Map map)
        {
            if (!View3D.Active || map == null || !(IsoRimMod.Settings.fogLid || ShaderCut.On))
            {
                return;
            }
            IsoCellMap c = For(map);
            long t0 = Probe.On ? Stopwatch.GetTimestamp() : 0;
            c.Update();
            if (Probe.On)
            {
                Probe.AddCellTime((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            Shader.SetGlobalTexture(texId, c.Texture);
            Shader.SetGlobalVector(mapId, new Vector4(c.origin.x, c.origin.y, c.width, c.height));
            Shader.SetGlobalVector(levelId, new Vector4(c.baseY, WallBoxes.CutFraction, 0f, 0f));   // y: what is left of a cut wall
        }

        // ---- the fog lid

        public const string FogDef = "IsoRimFog";
        private static Material fog;
        private static bool fogTried;

        // The material a block of the game's fog is drawn with in 3D: the game's fog material on the
        // IsoRimFog shader (the lid is lifted in its vertex stage). Null if the def is missing.
        public static Material FogFor(Material gameMat)
        {
            if (!fogTried)
            {
                fogTried = true;
                Shader s = R3DMaterials.ShaderOf(FogDef);
                if (s != null && gameMat != null)
                {
                    // A fresh material on the lid shader with the game's properties copied in (a copy made
                    // with new Material(gameMat) of a material the game has not finished loading would take
                    // the game's shader back when it is resolved).
                    fog = new Material(s);
                    fog.CopyPropertiesFromMaterial(gameMat);
                    fog.renderQueue = gameMat.renderQueue;
                    fog.name = "IsoRim_FogLid";
                }
            }
            if (fog != null && gameMat != null && fog.color != gameMat.color)
            {
                fog.color = gameMat.color;
            }
            return fog;
        }
    }
}
