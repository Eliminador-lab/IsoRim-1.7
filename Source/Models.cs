using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace IsoRim
{
    // XML settings for how one thing is drawn in 3D. Any mod can add these:
    //
    //   <IsoRim.ModelDef>
    //     <defName>R3D_TorchLamp</defName>
    //     <thingDef>TorchLamp</thingDef>
    //     <model>Models/torch_lamp.obj</model>   (path inside the mod that contains this def)
    //   </IsoRim.ModelDef>
    //
    // Leave <model> out to keep the automatic shape and only change it with <height> or <flat>.
    public class ModelDef : Def
    {
        public string thingDef;
        public string model;
        public float scale = 1f;
        public Vector3 offset = Vector3.zero;
        public float rotation = 0f;         // extra turn around the vertical axis, degrees
        public float surfaceHeight = -1f;   // where a pawn lying on it rests; -1 uses the top of the model
        public float height = -1f;          // height in cells of the automatic shape; -1 lets the mod decide
        public bool flat = false;           // true keeps the thing as a flat sprite

        public List<string> models;         // several models: each thing picks one and keeps it
        public string thingCategory;        // apply to every thing in this category instead of one thingDef
        public bool randomRotation = false; // give each thing its own turn
        public float scaleJitter = 0f;      // vary each thing's size by up to this fraction
        public bool growthScale = false;    // scale with how grown a plant is
        public bool layFlat = false;        // the model is built as held; on the ground it is tipped onto its side
        public float scatter = 0f;          // move each thing up to this far (in cells) off the middle of its cell
        public bool spriteTint = false;     // colour "stuff" parts from the thing's own sprite instead of its material
        public string fallback;             // "SmallPlants": every plant that is not a tree and has no other entry

        public bool wallMounted = false;    // fixed to the wall the thing faces (wall lamps)
        public float mountHeight = 0.5f;    // how far up that wall, as a fraction of the wall's height
        public bool alongRun = false;       // turned a quarter turn where its row runs north-south (sandbags)
        public bool fullVariant = false;    // the second model is used once the thing holds something (graves)
        public bool groundShadow = true;    // soft shading on the ground under it
        public float outline = 1f;          // how bold the dark outline is; 0 for none
        public bool linked = false;         // the first model stands in the cell; the second is added toward each neighbour it joins

        // Matching by name: every building or item whose defName has one of these as a whole word or
        // run of words (ignoring case, plural allowed; see ModelLibrary.NameMatches) and that has no entry of its own or of its category. Used for the wrecks and junk
        // that other content scatters over maps, whose exact names vary.
        public List<string> match;
        public Vector2 builtFor = Vector2.zero; // the footprint (x, z) in cells the model was built for; it is stretched to the thing's real size
        public string topModel;             // turrets: the gun that turns, drawn every frame
        public float topHeight = 0.5f;      // height of the turret's pivot, in cells

        // Animals. thingDef is the animal's race; model is its body, facing north.
        public bool animal = false;
        public string legModel;             // one leg, standing on the ground with its hip at the top
        public List<Vector3> legs;          // where each hip is on the body
        public float legLength = 0f;        // height of the hip above the foot
        public float legSwing = 26f;        // how far the legs swing when walking, degrees
        public float legSplay = 0f;         // how far the legs lean outward (lizards, insects), degrees
        public float stride = 1f;           // cells walked for one full swing of the legs
        public float bodyCentre = 0f;       // height of the middle of the trunk
        public float bodyHalfWidth = 0.2f;  // half the width of the trunk; where it rests when on its side
        public float bodyHalfLength = 0.4f; // half the length of the animal, for the shading under it
    }

    // Triangle soup: every three entries make one triangle, already in game axes
    // (X east, Y up, Z north), one cell per unit, centred on the thing's footprint.
    public class Model
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public Color[] colors;
        public bool[] tinted;   // true where the colour is multiplied by the thing's stuff colour
        public Vector2[] uvs;   // texture coordinates
        public int[] material;  // which entry of materials each vertex uses
        public ModelMaterial[] materials;
        public Vector3[] hull;  // how far each vertex moves outward to form the dark outline
        public float maxY;
        public float halfDepth; // furthest the model reaches north or south of its centre
        public float minX, maxX, minZ, maxZ;   // the box the model covers on the ground
        public int id;          // unique per loaded model
        public ShadowShape shadow;   // worked out the first time the model has to cast a shadow
        public bool shadowTried;
        public Vector2[] footprint;      // outline of the model seen from above, and of the model
        public Vector2[] footprintLaid;  // tipped onto its side; worked out when first needed
        public bool footprintTried;
    }

    // A simple stand-in for a model when it casts a shadow: the outline of its leaves seen from
    // above, held at one height, on top of a square post for the trunk. Flat pieces like these
    // never lie over themselves however the sun moves them, which the real model's hundreds of
    // faces would.
    public class ShadowShape
    {
        public Vector2[] crown;     // corners of the leaf outline (x, z), anticlockwise; null if there are no leaves
        public float crownHeight;
        public float trunkHalf;     // half the width of the trunk
        public float trunkTop;      // the height of everything that is not a leaf

        public static ShadowShape For(Model m)
        {
            if (!m.shadowTried)
            {
                m.shadowTried = true;
                try
                {
                    m.shadow = Build(m);
                }
                catch (Exception e)
                {
                    m.shadow = null;
                    Log.ErrorOnce("[IsoRim] Working out a model's shadow failed: " + e, 93611061);
                }
            }
            return m.shadow;
        }

        // The outline of the ground a model covers, corners anticlockwise in (x, z); null if it has none.
        public static Vector2[] Footprint(Model m, bool laid)
        {
            if (!m.footprintTried)
            {
                m.footprintTried = true;
                try
                {
                    List<Vector2> standing = new List<Vector2>(m.positions.Length);
                    List<Vector2> tipped = new List<Vector2>(m.positions.Length);
                    for (int i = 0; i < m.positions.Length; i++)
                    {
                        Vector3 p = m.positions[i];
                        standing.Add(new Vector2(p.x, p.z));
                        tipped.Add(new Vector2(p.x, p.y));   // tipped over, what was up points north
                    }
                    Vector2[] a = Hull(standing);
                    Vector2[] b = Hull(tipped);
                    m.footprint = a.Length >= 3 ? a : null;
                    m.footprintLaid = b.Length >= 3 ? b : null;
                }
                catch (Exception e)
                {
                    m.footprint = null;
                    m.footprintLaid = null;
                    Log.ErrorOnce("[IsoRim] Working out a model's footprint failed: " + e, 93611062);
                }
            }
            return laid ? m.footprintLaid : m.footprint;
        }

        private static ShadowShape Build(Model m)
        {
            int n = m.positions.Length;
            bool[] leaf = new bool[n];
            List<Vector2> leaves = new List<Vector2>();
            float leafLow = float.MaxValue;
            float leafHigh = 0f;
            float woodTop = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                ModelMaterial mm = m.materials != null && m.material != null ? m.materials[m.material[i]] : null;
                leaf[i] = mm != null && mm.leaf;
                if (leaf[i])
                {
                    leaves.Add(new Vector2(p.x, p.z));
                    leafLow = Mathf.Min(leafLow, p.y);
                    leafHigh = Mathf.Max(leafHigh, p.y);
                }
                else
                {
                    woodTop = Mathf.Max(woodTop, p.y);
                }
            }

            // The trunk is as wide as the woody part is in its bottom third.
            float limit = Mathf.Max(0.05f, woodTop * 0.33f);
            float half = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                if (!leaf[i] && p.y <= limit)
                {
                    half = Mathf.Max(half, Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z)));
                }
            }

            ShadowShape s = new ShadowShape();
            s.trunkHalf = Mathf.Clamp(half * 0.8f, 0.02f, 0.25f);
            s.trunkTop = woodTop;
            if (leaves.Count >= 3)
            {
                Vector2[] hull = Hull(leaves);
                if (hull.Length >= 3)
                {
                    s.crown = hull;
                    s.crownHeight = leafLow + (leafHigh - leafLow) * 0.45f;
                }
            }
            if (s.crown == null && woodTop <= 0.01f)
            {
                return null;   // nothing to cast a shadow
            }
            return s;
        }

        // The smallest outline with no dents that holds every point, corners in anticlockwise order.
        private static Vector2[] Hull(List<Vector2> points)
        {
            points.Sort(delegate (Vector2 a, Vector2 b)
            {
                int c = a.x.CompareTo(b.x);
                return c != 0 ? c : a.y.CompareTo(b.y);
            });
            int n = points.Count;
            Vector2[] h = new Vector2[2 * n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(h[k - 2], h[k - 1], points[i]) <= 1e-9f)
                {
                    k--;
                }
                h[k++] = points[i];
            }
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Cross(h[k - 2], h[k - 1], points[i]) <= 1e-9f)
                {
                    k--;
                }
                h[k++] = points[i];
            }
            int count = Mathf.Max(0, k - 1);   // the last point repeats the first
            Vector2[] result = new Vector2[count];
            Array.Copy(h, result, count);
            return result;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }
    }

    public class ModelMaterial
    {
        public string name = "";
        public string texture;   // path under Textures/ without extension; null for a plain colour
        public Color color = new Color(0.8f, 0.8f, 0.8f);
        public bool tinted;
        public bool leaf;        // left out while a tree is bare
        public bool twig;        // only drawn while a tree is bare (fine branches the leaves would hide)
        public bool glow;        // gives off its own light (flames, embers, lamps): never shaded
        public bool fire;        // a flame or embers: left out while the thing's fire is out
    }

    public class ModelEntry
    {
        public ModelDef def;
        public Model[] variants;
        public Model leg;   // animals only
        public Model top;   // turrets only

        public bool HasModel
        {
            get { return variants != null && variants.Length > 0; }
        }

        // True if the model was built long east-west but the thing is long north-south (or the
        // other way round): it is then turned a quarter turn to fit.
        public bool Swapped(ThingDef thing)
        {
            if (def.builtFor.x <= 0f || def.builtFor.y <= 0f || thing == null || Mathf.Approximately(def.builtFor.x, def.builtFor.y)
                || thing.size.x == thing.size.z)
            {
                return false;
            }
            return (def.builtFor.x > def.builtFor.y) != (thing.size.x > thing.size.z);
        }

        // How much the model is stretched along x, up and along z to fit a thing of this kind:
        // (1, 1, 1) unless the model was built for a different footprint.
        public Vector3 Fit(ThingDef thing)
        {
            if (def.builtFor.x <= 0f || def.builtFor.y <= 0f || thing == null)
            {
                return Vector3.one;
            }
            bool swap = Swapped(thing);
            float sx = (swap ? thing.size.z : thing.size.x) / def.builtFor.x;
            float sz = (swap ? thing.size.x : thing.size.z) / def.builtFor.y;
            if (Mathf.Abs(sx - 1f) < 0.01f && Mathf.Abs(sz - 1f) < 0.01f)
            {
                return Vector3.one;
            }
            return new Vector3(sx, Mathf.Sqrt(sx * sz), sz);
        }

        // The same thing always gets the same variant.
        public Model Pick(Thing t)
        {
            if (!HasModel)
            {
                return null;
            }
            int n = t != null ? t.thingIDNumber : 0;
            if (n < 0)
            {
                n = -n;
            }
            return variants[n % variants.Length];
        }

        public float SurfaceHeight
        {
            get
            {
                if (def.surfaceHeight >= 0f)
                {
                    return def.surfaceHeight;
                }
                return HasModel ? variants[0].maxY * def.scale : 0f;
            }
        }
    }

    public static class ModelLibrary
    {
        private static readonly Dictionary<ThingDef, ModelEntry> byThing = new Dictionary<ThingDef, ModelEntry>();
        private static readonly Dictionary<string, Model> byPath = new Dictionary<string, Model>();
        private static readonly Dictionary<ThingDef, string> how = new Dictionary<ThingDef, string>();

        public static bool TryGet(ThingDef def, out ModelEntry entry)
        {
            return byThing.TryGetValue(def, out entry);
        }

        // How a thing got its entry: "thingDef", "category:<name>", "match:<name part>" or
        // "fallback:SmallPlants"; null if it has none (for the harness's model sweep).
        public static string HowBound(ThingDef def)
        {
            string h;
            return how.TryGetValue(def, out h) ? h : null;
        }

        // Whether a <match> name part names a word (or run of words) of the defName: it must start
        // where a capitalised word starts and end where one ends, ignoring case. "Car" finds
        // AncientCar and RustedCarWreck but not AncientCardboardBox; "APC" finds AncientAPC but not
        // ScrapCubeSculpture. Plain substring matching put a car wreck in place of a cardboard box.
        public static bool NameMatches(string defName, string part)
        {
            if (defName.NullOrEmpty() || part.NullOrEmpty())
            {
                return false;
            }
            int from = 0;
            while (true)
            {
                int at = defName.IndexOf(part, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    return false;
                }
                int end = at + part.Length;
                bool starts = at == 0 || !char.IsLetter(defName[at - 1]) || char.IsUpper(defName[at]);
                // A plural counts too: "AncientPipe" finds AncientPipes.
                bool ends = end == defName.Length || !char.IsLower(defName[end])
                    || (defName[end] == 's' && (end + 1 == defName.Length || !char.IsLower(defName[end + 1])));
                if (starts && ends)
                {
                    return true;
                }
                from = at + 1;
            }
        }

        // Loads a model file once; null if it is missing or unreadable.
        public static Model LoadFile(string path)
        {
            Model model;
            if (byPath.TryGetValue(path, out model))
            {
                return model;
            }
            try
            {
                model = ObjLoader.Load(path);
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not read model " + path + ": " + e.Message);
                model = null;
            }
            byPath[path] = model;
            return model;
        }

        private static ModelEntry MakeEntry(ModelDef md)
        {
            List<string> files = new List<string>();
            if (!md.model.NullOrEmpty())
            {
                files.Add(md.model);
            }
            if (md.models != null)
            {
                files.AddRange(md.models);
            }

            List<Model> loaded = new List<Model>();
            if (md.modContentPack != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    Model m = LoadFile(Path.Combine(md.modContentPack.RootDir, files[i]));
                    if (m != null)
                    {
                        loaded.Add(m);
                    }
                }
            }

            ModelEntry entry = new ModelEntry();
            entry.def = md;
            entry.variants = loaded.ToArray();
            if (!md.legModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.leg = LoadFile(Path.Combine(md.modContentPack.RootDir, md.legModel));
            }
            if (!md.topModel.NullOrEmpty() && md.modContentPack != null)
            {
                entry.top = LoadFile(Path.Combine(md.modContentPack.RootDir, md.topModel));
            }
            return entry;
        }

        public static void LoadAll()
        {
            List<ModelDef> all = DefDatabase<ModelDef>.AllDefsListForReading;

            // Entries naming one thing come first, so they win over a category the thing is in.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.thingDef.NullOrEmpty())
                {
                    continue;
                }
                // A thing from a DLC or mod that is not installed is skipped quietly.
                ThingDef thing = DefDatabase<ThingDef>.GetNamedSilentFail(md.thingDef);
                if (thing != null)
                {
                    byThing[thing] = MakeEntry(md);
                    how[thing] = "thingDef";
                }
            }

            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.thingCategory.NullOrEmpty())
                {
                    continue;
                }
                ThingCategoryDef category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(md.thingCategory);
                if (category == null)
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (thing.thingCategories == null || !thing.thingCategories.Contains(category) || byThing.ContainsKey(thing))
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "category:" + md.thingCategory;
                }
            }

            // Then names: the wrecks, junk and ruins other content scatters about. Earlier entries win.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.match == null || md.match.Count == 0)
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (byThing.ContainsKey(thing) || thing.defName == null || thing.IsBlueprint || thing.IsFrame
                        || (thing.category != ThingCategory.Building && thing.category != ThingCategory.Item))
                    {
                        continue;
                    }
                    string hit = null;
                    for (int k = 0; k < md.match.Count && hit == null; k++)
                    {
                        if (NameMatches(thing.defName, md.match[k]))
                        {
                            hit = md.match[k];
                        }
                    }
                    if (hit == null)
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "match:" + hit;
                }
            }

            // Last, the catch-all for small plants: grass, flowers and crops from DLC and other mods.
            for (int i = 0; i < all.Count; i++)
            {
                ModelDef md = all[i];
                if (md.fallback != "SmallPlants")
                {
                    continue;
                }
                ModelEntry entry = null;
                foreach (ThingDef thing in DefDatabase<ThingDef>.AllDefs)
                {
                    if (thing.category != ThingCategory.Plant || thing.plant == null || thing.plant.IsTree || byThing.ContainsKey(thing))
                    {
                        continue;
                    }
                    // Tree stumps and burned trees are plants too, but a clump of leaves would not suit them.
                    if (thing.defName.Contains("Stump") || thing.defName.Contains("Burned"))
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = MakeEntry(md);
                    }
                    byThing[thing] = entry;
                    how[thing] = "fallback:SmallPlants";
                }
            }

            int models = 0;
            foreach (KeyValuePair<ThingDef, ModelEntry> pair in byThing)
            {
                if (pair.Value.HasModel)
                {
                    models++;
                }
            }
            ModLog.Note("[IsoRim] " + models + " things have a hand-made 3D model.");
        }
    }

    // Reads Wavefront OBJ, the plain-text format Blender exports with its default axes
    // (Y up, -Z forward), with its .mtl file. Per material: Kd is the colour, map_Kd an optional
    // texture, and a name starting with "stuff" takes the colour of whatever the thing is made of.
    public static class ObjLoader
    {
        private static readonly char[] Separators = { ' ', '\t' };
        private static readonly Color DefaultColor = new Color(0.8f, 0.8f, 0.8f);
        private const float OutlineWidth = 0.03f;   // in cells
        private static int nextId = 1;

        public static Model Load(string path)
        {
            if (!File.Exists(path))
            {
                ModLog.Warn("[IsoRim] Model file not found: " + path);
                return null;
            }

            List<Vector3> pos = new List<Vector3>();
            List<Vector2> tex = new List<Vector2>();
            List<Color> vertColor = new List<Color>();
            List<bool> hasVertColor = new List<bool>();

            List<ModelMaterial> materials = new List<ModelMaterial>();
            Dictionary<string, int> materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            materials.Add(new ModelMaterial());   // used by faces that name no material
            int current = 0;

            List<Vector3> outPos = new List<Vector3>();
            List<Vector3> outNormal = new List<Vector3>();
            List<Color> outColor = new List<Color>();
            List<bool> outTint = new List<bool>();
            List<Vector2> outUV = new List<Vector2>();
            List<int> outMaterial = new List<int>();

            string dir = Path.GetDirectoryName(path);
            List<int> face = new List<int>();
            List<int> faceUV = new List<int>();

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] t = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

                if (t[0] == "mtllib" && t.Length > 1)
                {
                    ReadMaterials(Path.Combine(dir, line.Substring(6).Trim()), materials, materialIndex);
                }
                else if (t[0] == "usemtl")
                {
                    string name = t.Length > 1 ? t[1] : "";
                    if (!materialIndex.TryGetValue(name, out current))
                    {
                        current = AddMaterial(name, materials, materialIndex);
                    }
                }
                else if (t[0] == "v" && t.Length >= 4)
                {
                    // OBJ is right-handed; flipping Z turns it into the game's axes.
                    pos.Add(new Vector3(Num(t[1]), Num(t[2]), -Num(t[3])));
                    if (t.Length >= 7)
                    {
                        vertColor.Add(new Color(Num(t[4]), Num(t[5]), Num(t[6])));
                        hasVertColor.Add(true);
                    }
                    else
                    {
                        vertColor.Add(Color.white);
                        hasVertColor.Add(false);
                    }
                }
                else if (t[0] == "vt" && t.Length >= 3)
                {
                    tex.Add(new Vector2(Num(t[1]), Num(t[2])));
                }
                else if (t[0] == "f" && t.Length >= 4)
                {
                    // Each corner is written as position, position/texture or position/texture/normal.
                    face.Clear();
                    faceUV.Clear();
                    bool bad = false;
                    for (int i = 1; i < t.Length; i++)
                    {
                        string[] parts = t[i].Split('/');
                        int idx = Index(parts[0], pos.Count);
                        if (idx < 0)
                        {
                            bad = true;
                            break;
                        }
                        face.Add(idx);
                        faceUV.Add(parts.Length > 1 ? Index(parts[1], tex.Count) : -1);
                    }
                    if (bad)
                    {
                        continue;
                    }

                    ModelMaterial mat = materials[current];

                    // Split polygons into a fan of triangles.
                    for (int i = 1; i + 1 < face.Count; i++)
                    {
                        Vector3 a = pos[face[0]];
                        Vector3 b = pos[face[i]];
                        Vector3 c = pos[face[i + 1]];
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.sqrMagnitude < 1e-12f)
                        {
                            continue;
                        }
                        n.Normalize();

                        for (int k = 0; k < 3; k++)
                        {
                            int corner = k == 0 ? 0 : (k == 1 ? i : i + 1);
                            int pi = face[corner];
                            int ti = faceUV[corner];
                            outPos.Add(pos[pi]);
                            outNormal.Add(n);
                            outColor.Add(hasVertColor[pi] ? vertColor[pi] : mat.color);
                            outTint.Add(mat.tinted);
                            outUV.Add(ti >= 0 ? tex[ti] : Vector2.zero);
                            outMaterial.Add(current);
                        }
                    }
                }
            }

            if (outPos.Count == 0)
            {
                ModLog.Warn("[IsoRim] Model has no faces: " + path);
                return null;
            }

            Model m = new Model();
            m.positions = outPos.ToArray();
            m.normals = outNormal.ToArray();
            m.colors = outColor.ToArray();
            m.tinted = outTint.ToArray();
            m.uvs = outUV.ToArray();
            m.material = outMaterial.ToArray();
            m.materials = materials.ToArray();
            float maxY = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                maxY = Mathf.Max(maxY, m.positions[i].y);
            }
            m.maxY = maxY;
            float depth = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                depth = Mathf.Max(depth, Mathf.Abs(m.positions[i].z));
            }
            m.halfDepth = depth;
            m.minX = float.MaxValue;
            m.minZ = float.MaxValue;
            m.maxX = float.MinValue;
            m.maxZ = float.MinValue;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 p = m.positions[i];
                m.minX = Mathf.Min(m.minX, p.x);
                m.maxX = Mathf.Max(m.maxX, p.x);
                m.minZ = Mathf.Min(m.minZ, p.z);
                m.maxZ = Mathf.Max(m.maxZ, p.z);
            }
            m.id = nextId++;
            m.hull = BuildHull(m.positions, m.normals, OutlineWidth);
            return m;
        }

        // Turns a 1-based OBJ index (negative counts back from the end) into a list index, or -1.
        private static int Index(string token, int count)
        {
            int idx;
            if (token.Length == 0 || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                return -1;
            }
            idx = idx < 0 ? count + idx : idx - 1;
            return idx >= 0 && idx < count ? idx : -1;
        }

        private static int AddMaterial(string name, List<ModelMaterial> materials, Dictionary<string, int> materialIndex)
        {
            ModelMaterial mat = new ModelMaterial();
            mat.name = name;
            mat.tinted = name.StartsWith("stuff", StringComparison.OrdinalIgnoreCase);
            mat.leaf = name.StartsWith("leaf", StringComparison.OrdinalIgnoreCase);
            mat.twig = name.StartsWith("twig", StringComparison.OrdinalIgnoreCase);
            mat.glow = name.StartsWith("flame", StringComparison.OrdinalIgnoreCase) || name.StartsWith("glow", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ember", StringComparison.OrdinalIgnoreCase);
            mat.fire = name.StartsWith("flame", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ember", StringComparison.OrdinalIgnoreCase);
            mat.color = mat.tinted ? Color.white : DefaultColor;
            materials.Add(mat);
            materialIndex[name] = materials.Count - 1;
            return materials.Count - 1;
        }

        // For the outline, each corner moves away from all the faces that meet there. Corners
        // that share a position move together, so the enlarged copy stays closed.
        private static Vector3[] BuildHull(Vector3[] positions, Vector3[] normals, float width)
        {
            Dictionary<Vector3Int, List<Vector3>> facesAt = new Dictionary<Vector3Int, List<Vector3>>();
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3Int key = HullKey(positions[i]);
                List<Vector3> list;
                if (!facesAt.TryGetValue(key, out list))
                {
                    list = new List<Vector3>();
                    facesAt[key] = list;
                }
                bool known = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (Vector3.Dot(list[k], normals[i]) > 0.999f)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    list.Add(normals[i]);
                }
            }

            Vector3[] hull = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                List<Vector3> list = facesAt[HullKey(positions[i])];
                Vector3 sum = Vector3.zero;
                for (int k = 0; k < list.Count; k++)
                {
                    sum += list[k];
                }
                if (positions[i].y < 0.001f)
                {
                    sum.y = 0f;   // corners on the ground only move sideways
                }
                if (sum.sqrMagnitude < 1e-8f)
                {
                    hull[i] = Vector3.zero;
                    continue;
                }

                Vector3 dir = sum.normalized;
                float least = 1f;
                for (int k = 0; k < list.Count; k++)
                {
                    least = Mathf.Min(least, Vector3.Dot(dir, list[k]));
                }
                hull[i] = dir * (width / Mathf.Clamp(least, 0.4f, 1f));
            }
            return hull;
        }

        private static Vector3Int HullKey(Vector3 p)
        {
            return new Vector3Int(Mathf.RoundToInt(p.x * 10000f), Mathf.RoundToInt(p.y * 10000f), Mathf.RoundToInt(p.z * 10000f));
        }

        private static void ReadMaterials(string path, List<ModelMaterial> materials, Dictionary<string, int> materialIndex)
        {
            if (!File.Exists(path))
            {
                ModLog.Warn("[IsoRim] Material file not found: " + path);
                return;
            }
            ModelMaterial current = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] t = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "newmtl" && t.Length > 1)
                {
                    int index;
                    if (!materialIndex.TryGetValue(t[1], out index))
                    {
                        index = AddMaterial(t[1], materials, materialIndex);
                    }
                    current = materials[index];
                }
                else if (t[0] == "Kd" && t.Length >= 4 && current != null)
                {
                    current.color = new Color(Num(t[1]), Num(t[2]), Num(t[3]));
                }
                else if (t[0] == "map_Kd" && t.Length >= 2 && current != null)
                {
                    current.texture = TexturePath(line.Substring(6).Trim());
                }
            }
        }

        // Accepts either a game content path (IsoRim/Swatches/Wood) or a file path that Blender
        // wrote (C:\...\Textures\IsoRim\Swatches\Wood.png) and returns the content path.
        private static string TexturePath(string value)
        {
            string p = value.Replace('\\', '/');
            int cut = p.LastIndexOf("Textures/", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0)
            {
                p = p.Substring(cut + 9);
            }
            int dot = p.LastIndexOf('.');
            if (dot > p.LastIndexOf('/'))
            {
                p = p.Substring(0, dot);
            }
            p = p.Trim().TrimStart('/');
            return p.Length == 0 ? null : p;
        }

        private static float Num(string s)
        {
            float f;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
        }
    }

    // The mod's own textures, from its Textures folder. They are drawn with the same shader the
    // game uses for its sprites, with the colour (build material and shading) set per material.
    public static class R3DTextures
    {
        private static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string path)
        {
            Texture2D tex;
            if (cache.TryGetValue(path, out tex))
            {
                return tex;
            }
            tex = ContentFinder<Texture2D>.Get(path, false);
            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Repeat;   // the textures are made to tile
                // Smoothly filtered at a slant and at a distance, so fine patterns do not
                // shimmer as the camera moves.
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
            }
            else
            {
                ModLog.Warn("[IsoRim] Texture not found: " + path);
            }
            cache[path] = tex;
            return tex;
        }

        // A material showing this texture multiplied by a colour, or null if the texture is missing.
        // queue -1 = the textured def's own queue (2001, opaque).
        public static Material MaterialFor(string path, Color color, int queue = -1)
        {
            Texture2D tex = Get(path);
            if (tex == null)
            {
                return null;
            }
            color.a = 1f;
            Color32 c = color;
            string key = path + "|" + c.r + "," + c.g + "," + c.b + "|" + queue;
            Material m;
            if (!materials.TryGetValue(key, out m))
            {
                m = R3DMaterials.Make(R3DMaterials.SolidTexDef, "IsoRim_Tex_" + path, queue);
                if (m != null)
                {
                    m.mainTexture = tex;
                    m.color = color;
                    IsoGlow.Use(m, IsoGlow.OwnCell);
                }
                materials[key] = m;
            }
            return m;
        }

        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        public static int SwatchIndex(string swatch)
        {
            switch (swatch)
            {
                case "Stone":
                    return 1;
                case "Metal":
                    return 2;
                case "Fabric":
                    return 3;
                default:
                    return 0;
            }
        }

        // Which of the mod's surface textures matches what a thing is built from.
        public static string StuffSwatch(Thing t)
        {
            ThingDef stuff = t.Stuff;
            if (stuff == null || stuff.stuffProps == null)
            {
                return "Wood";
            }
            string name = stuff.stuffProps.appearance != null ? stuff.stuffProps.appearance.defName : "";
            if (name == "Bricks")
            {
                return "Stone";
            }
            if (name == "Smooth")
            {
                return "Metal";
            }
            if (name == "Planks")
            {
                return "Wood";
            }
            if (stuff.stuffProps.categories != null)
            {
                for (int i = 0; i < stuff.stuffProps.categories.Count; i++)
                {
                    string cat = stuff.stuffProps.categories[i].defName;
                    if (cat == "Stony")
                    {
                        return "Stone";
                    }
                    if (cat == "Metallic")
                    {
                        return "Metal";
                    }
                    if (cat == "Fabric" || cat == "Leathery")
                    {
                        return "Fabric";
                    }
                }
            }
            return "Wood";
        }
    }

    // The average colour of a thing's own sprite, used to colour the stand-in model of a plant
    // the mod has no model for, so it still looks like that plant from a distance.
    public static class SpriteColors
    {
        private const int N = 16;
        private static readonly Color Fallback = new Color(0.40f, 0.55f, 0.26f);
        private static readonly Dictionary<int, Color> cache = new Dictionary<int, Color>();

        public static Color MeanFor(Thing t)
        {
            try
            {
                Graphic g = t.Graphic;
                Material mat = g != null ? g.MatSingleFor(t) : null;
                if (mat == null || !mat.HasProperty("_MainTex") || mat.mainTexture == null)
                {
                    return Fallback;
                }
                Texture tex = mat.mainTexture;
                Color c;
                if (!cache.TryGetValue(tex.GetInstanceID(), out c))
                {
                    c = Read(tex);
                    cache[tex.GetInstanceID()] = c;
                }
                if (mat.HasProperty("_Color"))
                {
                    c *= mat.color;
                }
                c.a = 1f;
                return c;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a sprite's colour failed: " + e, 93611040);
                return Fallback;
            }
        }

        public const int SampleSize = N;

        // A 16 x 16 sample of the whole sprite (SpritePixels: on the CPU, as the 1.5 mod's Blit and
        // ReadPixels gave it; the 1.5 way on the GPU if the pixels cannot be read).
        private static Color Read(Texture tex)
        {
            Color32[] px = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, N);
            if (px == null && !SpritePixels.IsSheet(tex))
            {
                px = SpritePixels.GpuSample(tex, N);
            }
            return MeanOf(px);
        }

        // The lifted mean colour of the solid pixels of a sample; the fallback if there are none.
        public static Color MeanOf(Color32[] px)
        {
            float r = 0f;
            float g = 0f;
            float b = 0f;
            int count = 0;
            if (px != null)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a >= 128)
                    {
                        r += px[i].r;
                        g += px[i].g;
                        b += px[i].b;
                        count++;
                    }
                }
            }
            if (count == 0)
            {
                return Fallback;
            }

            // The average includes the sprite's dark outlines, so lift it back up a little.
            Color mean = new Color(r / count / 255f, g / count / 255f, b / count / 255f, 1f);
            float brightest = Mathf.Max(mean.r, Mathf.Max(mean.g, mean.b));
            float lift = brightest > 0.01f ? Mathf.Clamp(0.62f / brightest, 1f, 1.6f) : 1f;
            return new Color(Mathf.Min(1f, mean.r * lift), Mathf.Min(1f, mean.g * lift), Mathf.Min(1f, mean.b * lift), 1f);
        }
    }

    // The materials of all solid 3D geometry: colour from the vertices (stuff tint, fixed factors)
    // times the material colour, lit per pixel by IsoRim's sun (Shaders/IsoRimSolid.frag.glsl).
    [StaticConstructorOnStartup]
    public static class R3DMaterials
    {
        private static bool tried;
        private static Material solid;
        private static Material outline;
        private static bool shadowTried;
        private static Material gameShadow;
        private static Material plainShadow;
        private static bool groundShadowTried;
        private static Material groundShadow;

        // Light comes from above and from the south-west, so the faces turned toward the camera are lit.
        private static readonly Vector3 BaseLight = new Vector3(-0.35f, 0.8f, -0.5f).normalized;
        private static Vector3 LightDir = BaseLight;
        private static bool lightSet;

        // The direction toward IsoRim's sun (or moon) right now, in true (unsquashed) space.
        public static Vector3 SunDir
        {
            get
            {
                if (!lightSet)
                {
                    SetSunHour(10f);
                }
                return LightDir;
            }
        }

        // Where the sun is at a given hour: up in the east at six, high in the south at noon,
        // down in the west at six in the evening, and a fixed moonlight through the night, with
        // an hour's blend at dusk and dawn.
        public static Vector3 SunDirAt(float hour)
        {
            Vector3 moon = Dir(200f, 55f);
            if (hour >= 6f && hour <= 18f)
            {
                float t = (hour - 6f) / 12f;
                return Dir(95f + 170f * t, 22f + 40f * Mathf.Sin(t * Mathf.PI));
            }
            float k;
            Vector3 edge;
            if (hour > 18f)
            {
                k = Mathf.Clamp01(hour - 18f);
                edge = Dir(265f, 22f);
            }
            else
            {
                k = Mathf.Clamp01(6f - hour);
                edge = Dir(95f, 22f);
            }
            return Vector3.Slerp(edge, moon, k).normalized;
        }

        private static Vector3 Dir(float azimuth, float elevation)
        {
            float az = azimuth * Mathf.Deg2Rad;
            float el = elevation * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;
        }

        private static readonly int isoSunId = Shader.PropertyToID("_IsoSun");
        private static readonly int isoViewId = Shader.PropertyToID("_IsoView");

        // Moves the sun to the given hour of the day and hands it to the shaders (global _IsoSun),
        // which light every face per pixel. Cheap enough to call every frame.
        public static void SetSunHour(float hour)
        {
            SetLight(SunDirAt(hour));
        }

        // Night follows the game's moon (user decision 2026-10-13). The game swings its moon from
        // east to west through the night (GenCelestial.GetLightSourceInfo, night branch: shadows point
        // west in the evening, south at midnight, east before dawn). In 3D the game's shadows are
        // mirrored north-south (Patches.GetLightSourceInfo_Postfix), so the light comes from the
        // opposite side of the mirrored cast. Its height comes from the cast's length, by the same
        // relation the day sun has to the game's day casts: 62 degrees at the noon length (1.5),
        // 22 degrees at the length at six (about 8), never lower or higher.
        public static Vector3 MoonDir(Map map)
        {
            Vector2 cast = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.LightingMoon).vector;
            cast.y = -cast.y;   // mirrored like the 3D shadows
            float len = cast.magnitude;
            if (len < 1e-4f)
            {
                return Dir(180f, 62f);
            }
            float elevation = Mathf.Lerp(62f, 22f, Mathf.InverseLerp(1.5f, 8.04f, len)) * Mathf.Deg2Rad;
            Vector2 flat = -cast / len;
            return new Vector3(flat.x * Mathf.Cos(elevation), Mathf.Sin(elevation), flat.y * Mathf.Cos(elevation)).normalized;
        }

        // How far the light has gone over to the moon: 0 in daylight, 1 at night. The game switches
        // its shadows from sun to moon where the sun's glow crosses 0.6 (they fade out there), so the
        // light swings over smoothly across that point instead of jumping.
        public const float NightGlowFull = 0.52f;
        public const float DayGlowFull = 0.68f;

        public static float NightWeight(float sunGlow)
        {
            float t = Mathf.InverseLerp(DayGlowFull, NightGlowFull, sunGlow);
            return t * t * (3f - 2f * t);
        }

        // The light right now: IsoRim's own sun by day (unchanged), the game's moon at night.
        public static Vector3 LightAt(Map map, float hour)
        {
            Vector3 sun = SunDirAt(Mathf.Clamp(hour, 6f, 18f));
            if (map == null)
            {
                return sun;
            }
            float night = NightWeight(GenCelestial.CurCelestialSunGlow(map));
            if (night <= 0f)
            {
                return sun;
            }
            Vector3 moon = MoonDir(map);
            return night >= 1f ? moon : Vector3.Slerp(sun, moon, night).normalized;
        }

        public static void SetLight(Vector3 dir)
        {
            lightSet = true;
            LightDir = dir;
            Shader.SetGlobalVector(isoSunId, new Vector4(LightDir.x, LightDir.y, LightDir.z, 0f));
        }

        // The direction toward the camera in true space, and the squash, for the shaders' face
        // normals (global _IsoView). Set by View3D every frame while the 3D view is on.
        public static void SetView(Vector3 towardCamera, float squash)
        {
            Shader.SetGlobalVector(isoViewId, new Vector4(towardCamera.x, towardCamera.y, towardCamera.z, squash));
        }

        // ---- lit materials
        //
        // Faces are lit in the shader (Shaders\IsoRimSolid.frag.glsl): it works out which way each
        // face points, sorts it into one of these direction groups (straight up, straight down, or
        // one of eight compass points at three steepnesses) and shades it for this frame's sun.
        // So one material per shading curve serves every direction, and nothing is recoloured or
        // rebuilt as the sun moves. BinOf remains for callers that still pass a group (it is ignored).
        public const int Bins = 26;
        public const int LightModel = 1;   // _LightMode: model curve (curve 0)
        public const int LightWall = 2;    // _LightMode: wall and rock curve (curve 1)
        public const int LightRoof = 3;    // _LightMode: roof slates (model curve * 1.05, at most 1.1)
        private static readonly Material[] litSolid = new Material[2];
        private static readonly Dictionary<string, Material> litByKey = new Dictionary<string, Material>();

        // Which direction group a face pointing along normal belongs to.
        public static int BinOf(Vector3 normal)
        {
            float len = normal.magnitude;
            if (len < 1e-6f)
            {
                return 0;
            }
            normal /= len;
            if (normal.y >= 0.92f)
            {
                return 0;
            }
            if (normal.y <= -0.5f)
            {
                return 1;
            }
            int band = normal.y < 0.25f ? 0 : (normal.y < 0.65f ? 1 : 2);
            float az = Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg;
            int sector = Mathf.RoundToInt(Mathf.Repeat(az, 360f) / 45f) % 8;
            return 2 + band * 8 + sector;
        }

        private static int ModeOf(int curve)
        {
            return curve == 1 ? LightWall : LightModel;
        }

        // The plain solid material lit by the sun: vertex colours times the shade of the face's
        // direction. bin is ignored now (the shader finds each face's direction itself).
        public static Material Lit(int bin, int curve = 0)
        {
            if (Solid == null)
            {
                return null;
            }
            curve = curve == 1 ? 1 : 0;
            Material m = litSolid[curve];
            if (m == null)
            {
                m = Make(SolidDef, curve == 1 ? "IsoRim_LitWall" : "IsoRim_LitModel", -1, ModeOf(curve));
                IsoGlow.Use(m, IsoGlow.OwnCell);
                litSolid[curve] = m;
            }
            return m;
        }

        // The lit plain material for models: pawns, animals, baked models, extruded sides.
        public static Material LitModel
        {
            get { return Lit(0, 0); }
        }

        // A texture times a colour, lit by the sun. Shared by every face with the same texture,
        // colour and curve; bin is ignored (see Lit). Null if the texture is missing.
        public static Material LitTexture(string path, Color baseColor, int bin = 0, int curve = 0)
        {
            curve = curve == 1 ? 1 : 0;
            Color32 c = baseColor;
            string key = path + "|" + c.r + "," + c.g + "," + c.b + "|" + curve;
            Material m;
            if (litByKey.TryGetValue(key, out m))
            {
                return m;
            }
            Texture2D tex = R3DTextures.Get(path);
            if (tex != null)
            {
                m = Make(SolidTexDef, curve == 1 ? "IsoRim_LitTexWall" : "IsoRim_LitTex", -1, ModeOf(curve));
            }
            if (m != null)
            {
                m.mainTexture = tex;
                m.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f);
                IsoGlow.Use(m, IsoGlow.OwnCell);
            }
            litByKey[key] = m;
            return m;
        }

        public static Material Solid
        {
            get
            {
                if (!tried)
                {
                    Init();
                }
                return solid;
            }
        }

        // Same as Solid but drawn inside out: only the faces turned away from the camera show.
        public static Material Outline
        {
            get
            {
                if (!tried)
                {
                    Init();
                }
                return outline;
            }
        }

        // The outline of moving models (people, animals, doors, tools). Each part (body, head,
        // hair, hat) has its own shell; where one part tucks into another, the lower rim of its
        // shell sits just in front of the other part and showed as a dark ring. The vertex shader
        // pushes the whole shell straight away from the camera by View3D.OutlinePush, which hides
        // those rims behind the surface without moving anything on screen (same as 45b20f5).
        private static Material outlineMoving;

        public static Material OutlineMoving
        {
            get
            {
                if (outlineMoving == null && Outline != null)
                {
                    outlineMoving = Make(OutlineDef, "IsoRim_OutlineMoving");
                    outlineMoving.SetFloat(Shader.PropertyToID("_OutlinePush"), View3D.OutlinePush);
                }
                return outlineMoving;
            }
        }

        // ---- shaders (Defs\ShaderDefs\IsoRim_Shaders.xml). Every IsoRim def runs the same
        // fragment program (vertex colour * _Color [* texture] * shade); the def sets the render
        // state. _LightMode picks the shade: 0 unlit, 1 model curve, 2 wall curve, 3 roof slates.
        public const string SolidDef = "IsoRimSolid";       // opaque, depth write, cull off, 2000
        public const string SolidTexDef = "IsoRimSolidTex"; // same + texture, cut out at alpha 0.5, 2001
        public const string OutlineDef = "IsoRimOutline";   // opaque, cull front, pushed back in depth
        public const string GhostDef = "IsoRimGhost";       // alpha, depth write, cull back, 3100
        public const string BlendDef = "IsoRimBlend";       // alpha, depth test, no depth write, 3050
        public const string OverlayDef = "IsoRimOverlay";   // alpha, no depth test or write, 3650
        public const string WaveDef = "IsoRimWave";         // the game's Transparent with a repeating texture

        public const int LightUnlit = 0;
        private static readonly int lightModeId = Shader.PropertyToID("_LightMode");
        private static readonly Dictionary<string, Shader> shaders = new Dictionary<string, Shader>();

        // The shader of one of the defs above, or null (logged once) if it is missing.
        public static Shader ShaderOf(string defName)
        {
            Shader s;
            if (shaders.TryGetValue(defName, out s))
            {
                return s;
            }
            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(defName);
            s = def != null ? def.Shader : null;
            if (s == null)
            {
                Log.Error("[IsoRim] ShaderDef " + defName + " is missing, so parts of the 3D view cannot be drawn.");
            }
            shaders[defName] = s;
            return s;
        }

        // A new material on one of the IsoRim defs. queue -1 keeps the def's queue.
        public static Material Make(string defName, string name, int queue = -1, int lightMode = LightUnlit)
        {
            Shader s = ShaderOf(defName);
            if (s == null)
            {
                return null;
            }
            Material m = new Material(s);
            m.name = name;
            m.SetFloat(lightModeId, lightMode);
            if (queue >= 0)
            {
                m.renderQueue = queue;
            }
            return m;
        }

        private static void Init()
        {
            tried = true;
            solid = Make(SolidDef, "IsoRim_Solid");
            if (solid == null)
            {
                return;
            }
            // Night, lamp and indoor light per pixel from each face's own cell (see IsoGlow);
            // until the first 3D frame binds the map's light, the light is neutral.
            IsoGlow.BindNeutral();
            IsoGlow.Use(solid, IsoGlow.OwnCell);
            // The shell is pushed back in depth by the def, so where it runs close to the model's
            // own faces it can never show through them.
            outline = Make(OutlineDef, "IsoRim_Outline");
        }

        // See-through, coloured from the vertices, for the ghosts of things still to be built.
        private static Material ghost;

        public static Material Ghost
        {
            get
            {
                if (ghost == null && Solid != null)
                {
                    // After the game's lighting overlay (3100), lit by its own cell: drawn before
                    // it, a ghost over a face that carries its own light was never darkened.
                    ghost = Make(GhostDef, "IsoRim_Ghost", 3101);
                    IsoGlow.Use(ghost, IsoGlow.OwnCellUnmarked);
                }
                return ghost;
            }
        }

        // The solid material again, drawn late (after the night and indoor darkness) so that roofs
        // are not darkened by the rooms under them. Roofs dim with the daylight themselves.
        private static Material roof;

        public static Material Roof
        {
            get
            {
                if (roof == null && Solid != null)
                {
                    roof = Make(SolidDef, "IsoRim_Roof", 3600);
                    IsoGlow.Use(roof, IsoGlow.SkyOnly);   // sky colour only: lamps under a roof do not light its top
                }
                return roof;
            }
        }

        // See-through, coloured from the vertices, drawn after the ground and the game's own water,
        // so the water's moving shader shows through the mod's bands of colour and foam.
        private static Material waterOverlay;

        public static Material WaterOverlay
        {
            get
            {
                if (waterOverlay == null && Solid != null)
                {
                    waterOverlay = Make(BlendDef, "IsoRim_Water", 3050);
                }
                return waterOverlay;
            }
        }

        // The game's own shadow material. Whatever is drawn with it is slid along the ground by
        // the game according to the time of day, takes the game's shadow colour and fades out at
        // night, exactly like the shadows of walls and colonists. Null if the game has changed.
        public static Material GameShadow
        {
            get
            {
                if (!shadowTried)
                {
                    InitShadow();
                }
                return gameShadow;
            }
        }

        // A plain see-through black, used for shadows only when GameShadow is missing.
        public static Material PlainShadow
        {
            get
            {
                if (!shadowTried)
                {
                    InitShadow();
                }
                return plainShadow;
            }
        }

        // See-through black for the soft shading under things. How dark it is at each corner comes
        // from the corner's own colour, so one piece can fade from dark to nothing. It is drawn
        // straight after the ground, before anything that stands on it.
        public static Material GroundShadow
        {
            get
            {
                if (!groundShadowTried)
                {
                    groundShadowTried = true;
                    Material basis = Solid;
                    if (basis != null)
                    {
                        groundShadow = Make(BlendDef, "IsoRim_GroundShadow", 2420);
                    }
                }
                return groundShadow;
            }
        }

        private static void InitShadow()
        {
            shadowTried = true;
            try
            {
                Type bases = AccessTools.TypeByName("Verse.MatBases");
                string[] names = { "SunShadowFade", "SunShadow" };
                for (int i = 0; i < names.Length && gameShadow == null && bases != null; i++)
                {
                    FieldInfo f = AccessTools.Field(bases, names[i]);
                    gameShadow = f != null ? f.GetValue(null) as Material : null;
                }
            }
            catch (Exception e)
            {
                gameShadow = null;
                ModLog.Warn("[IsoRim] Looking up the game's shadow material failed: " + e.Message);
            }
            if (gameShadow != null)
            {
                return;
            }

            ModLog.Warn("[IsoRim] The game's shadow material was not found, so tree shadows will not move with the sun.");
            Material basis = Solid;
            if (basis == null)
            {
                return;
            }
            plainShadow = Make(BlendDef, "IsoRim_Shadow", 2900);
        }

        public static Color32 Shaded(Color c, float shade)
        {
            return new Color32(
                (byte)Mathf.Clamp(c.r * shade * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.g * shade * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.b * shade * 255f, 0f, 255f),
                255);
        }
    }
}
