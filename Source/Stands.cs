using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Outfit stands (Building_OutfitStand, Building_KidOutfitStand) drawn with their model.
    //
    // The game draws a stand every frame by itself (it never calls base.DrawAt): a flat base, body
    // and head, the apparel it holds over the body and head, and the weapon it holds. With the model,
    // the stand and its body are baked like any realtime building (SectionLayer_IsoRimBodies); here
    // go the rest, so that a stand looks like a 3D colonist wearing the same outfit, minus the colonist:
    //   - the stand's head model (headModel), left out as the game leaves out its head while worn
    //     apparel hides the head (renderSkipFlags Head: the burka, shown as a hood in its colour);
    //   - the clothes as the same 3D parts a colonist of the stand's body type wears (People.Dress:
    //     trousers, shirt, coat or armour, hat or helmet), in the same colours, fitted over the stand's
    //     body (dressScale, dressRise) and head (dressHeadScale about dressHeadCentre);
    //   - clothing with no 3D part (packs, belts) as the game's sprite, stood upright over the body
    //     (apparelHeight, apparelScale) as before;
    //   - the weapon made solid from its sprite (as a colonist's held weapon), placed and turned as the
    //     game carries it on a stand (DrawWeapon).
    public static class Stands
    {
        private const float DepthScale = 0.3f;   // the game's drawing heights, squeezed into a thin stack toward the camera
        private const float Nudge = 0.36f;       // and that stack held this far in front of the model's body, in cells
        private const float PersonHeadCentre = 0.998f;   // middle of a figure's head (person_head.obj), figure space
        private const float PersonHeight = 1.12f;        // a figure of size 1, feet to the top of the head (People.FigureHeight)
        private const float PictureHeight = 1.5f;        // the game's picture of a pawn (its 1.5-cell body mesh)
        private const float WeaponGap = 0.05f;           // between the dressed body and the weapon held in front of it, figure units

        // The game's carried-weapon offsets (PawnRenderUtility.EqLoc*), by facing.
        private static readonly Vector3[] EqLoc =
        {
            new Vector3(0f, 0f, -0.11f), new Vector3(0.22f, 0f, -0.22f), new Vector3(0f, 0f, -0.22f), new Vector3(-0.22f, 0f, -0.22f),
        };

        private struct Piece
        {
            public Graphic graphic;
            public int layer;
            public Vector3 scale;
            public Vector3 offset;
            public bool headgear;
        }

        // What one stand shows, worked out when its contents change.
        private class Outfit
        {
            public int version;
            public bool made;
            public string bodyKey;
            public string headKey;
            public float halfDepth;   // how far the dressed body reaches north or south of its middle, figure units (facing north)
            public float halfWidth;   // and east or west
            public readonly List<Apparel> unshown = new List<Apparel>();
            public Rot4 seen;
            public int piecesVersion = -1;
            public readonly List<Piece> pieces = new List<Piece>();
        }

        private static readonly Dictionary<int, Outfit> outfits = new Dictionary<int, Outfit>();
        // Ready-made meshes of the dressed parts, shared by every stand that shows the same.
        private static readonly Dictionary<string, List<DynamicModels.Part>> meshes = new Dictionary<string, List<DynamicModels.Part>>();
        private static readonly List<Model> partModels = new List<Model>();
        private static readonly List<Matrix4x4> partLocals = new List<Matrix4x4>();
        private static readonly List<Color> partTints = new List<Color>();

        private static FieldInfo skipHeadField;
        private static MethodInfo bodyTypeGetter;
        private static MethodInfo weaponFactorGetter;
        private static bool reflected;

        public static bool Draw(Building_OutfitStand stand, ModelEntry entry, Vector3 drawLoc)
        {
            if (!reflected)
            {
                reflected = true;
                skipHeadField = AccessTools.Field(typeof(Building_OutfitStand), "cachedApparelRenderInfoSkipHead");
                bodyTypeGetter = AccessTools.PropertyGetter(typeof(Building_OutfitStand), "BodyTypeDefForRendering");
                weaponFactorGetter = AccessTools.PropertyGetter(typeof(Building_OutfitStand), "WeaponDrawDistanceFactor");
            }
            if (skipHeadField == null || bodyTypeGetter == null || weaponFactorGetter == null)
            {
                return false;
            }
            ModelDef def = entry.def;
            BodyTypeDef body = bodyTypeGetter.Invoke(stand, null) as BodyTypeDef;
            bool skipHead = (bool)skipHeadField.GetValue(stand);
            float yaw = Glass.YawOf(stand, entry);
            Vector3 c = stand.TrueCenter();
            Vector3 ground = new Vector3(c.x, 0f, c.z);
            Vector3 fit = entry.Fit(stand.def) * def.scale;
            Matrix4x4 place = Matrix4x4.Translate(def.offset) * Matrix4x4.Scale(fit);

            if (!skipHead && entry.head != null)
            {
                DynamicModels.DrawLocal(entry.head, ground, yaw, place, R3DTextures.ModelTint(stand), R3DTextures.ModelSwatch(stand));
            }

            Outfit outfit = OutfitOf(stand, body, skipHead);
            Matrix4x4 bodyPlace = place * Matrix4x4.Translate(new Vector3(0f, def.dressRise, 0f)) * Matrix4x4.Scale(def.dressScale);
            Matrix4x4 headPlace = place * Matrix4x4.Translate(new Vector3(0f, def.dressHeadCentre - PersonHeadCentre * def.dressHeadScale, 0f))
                * Matrix4x4.Scale(Vector3.one * def.dressHeadScale);
            DynamicModels.DrawParts(MeshesFor(outfit.bodyKey), ground, yaw, bodyPlace);
            DynamicModels.DrawParts(MeshesFor(outfit.headKey), ground, yaw, headPlace);

            ThingWithComps weapon = stand.HeldWeapon;
            bool weaponDrawn = weapon == null;
            if (weapon != null && IsoRimMod.Settings.heldWeapons3D)
            {
                weaponDrawn = DrawWeapon(stand, weapon, def, fit, c, outfit);
            }

            Rot4 seen = Seen(stand.Rotation);
            List<Piece> pieces = PiecesFor(outfit, seen, body);
            if (pieces.Count == 0 && weaponDrawn)
            {
                return true;
            }

            bool wasActive = DrawHook.active, wasBillboard = DrawHook.billboard, wasSkip = DrawHook.skip;
            Matrix4x4 wasXform = DrawHook.xform;
            try
            {
                DrawHook.active = true;
                DrawHook.billboard = false;
                DrawHook.skip = false;
                Mesh mesh = MeshPool.GetMeshSetForSize(1.5f, 1.5f).MeshAt(seen);
                float scale = def.apparelScale * fit.y;

                // On the body, as the game: each at its layer's height over the item layer.
                float itemY = AltitudeLayer.Item.AltitudeFor();
                DrawHook.xform = Card(c, def.apparelHeight * fit.y, scale, new Vector3(drawLoc.x, itemY, drawLoc.z));
                for (int i = 0; i < pieces.Count; i++)
                {
                    Piece p = pieces[i];
                    if (p.headgear)
                    {
                        continue;
                    }
                    Vector3 at = new Vector3(drawLoc.x, itemY + PawnRenderUtility.AltitudeForLayer(p.layer), drawLoc.z);
                    Matrix4x4 m = Matrix4x4.TRS(at + p.graphic.DrawOffset(seen) + p.offset, p.graphic.QuatFromRot(seen), p.scale);
                    DrawHook.DrawMesh(mesh, m, p.graphic.MatAt(seen), 0);
                }
                if (!weaponDrawn)
                {
                    // No 3D form (or 3D weapons off): the game's sprite, as it carries it, stood up with the clothes.
                    Vector3 at = seen != Rot4.North
                        ? new Vector3(drawLoc.x, AltitudeLayer.ItemImportant.AltitudeFor() + PawnRenderUtility.AltitudeForLayer(90f), drawLoc.z)
                        : new Vector3(drawLoc.x, itemY - 0.05f, drawLoc.z);
                    PawnRenderUtility.DrawCarriedWeapon(weapon, at, seen, (float)weaponFactorGetter.Invoke(stand, null));
                }

                // Headgear with no 3D part, over the head unless something worn hides the head (as the game).
                if (!skipHead)
                {
                    float importantY = AltitudeLayer.ItemImportant.AltitudeFor();
                    Vector3 head = drawLoc + HeadOffsetAt(body, seen);
                    DrawHook.xform = Card(c, def.dressHeadCentre * fit.y, scale, new Vector3(head.x, importantY, head.z));
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        Piece p = pieces[i];
                        if (!p.headgear)
                        {
                            continue;
                        }
                        Vector3 at = new Vector3(head.x, importantY + PawnRenderUtility.AltitudeForLayer(p.layer), head.z);
                        Matrix4x4 m = Matrix4x4.TRS(at + p.graphic.DrawOffset(seen), p.graphic.QuatFromRot(seen), p.scale);
                        DrawHook.DrawMesh(mesh, m, p.graphic.MatAt(seen), 0);
                    }
                }
            }
            finally
            {
                DrawHook.active = wasActive;
                DrawHook.billboard = wasBillboard;
                DrawHook.skip = wasSkip;
                DrawHook.xform = wasXform;
            }
            return true;
        }

        // The weapon as the game carries it on a stand (PawnRenderUtility.DrawCarriedWeapon, DrawEquipmentAiming):
        // the game's flat picture is the stand seen from the south, so the weapon (its sprite made solid, as a
        // colonist's) stands in the east-west upright plane, turned and mirrored as the game turns its sprite,
        // moved by the game's offset for the stand's facing (east-west across the picture, south = lower on the
        // figure), held just clear of the dressed body: in front of it (south), or behind it for a stand facing
        // north, which the game draws under the body. Sized with the dressed figure.
        private static bool DrawWeapon(Building_OutfitStand stand, ThingWithComps weapon, ModelDef def, Vector3 fit, Vector3 c, Outfit outfit)
        {
            Rot4 facing = stand.Rotation;
            float aim = facing == Rot4.West ? 217f : 143f;
            Vector3 off = EqLoc[facing.AsInt] * (float)weaponFactorGetter.Invoke(stand, null);
            float picture = PersonHeight / PictureHeight;   // the game's picture units to figure units
            float height = (def.dressRise + (PersonHeight * 0.5f + off.z * picture) * def.dressScale.y) * fit.y;
            float across = off.x * picture * def.dressScale.x * fit.x;
            // Clear of the body: its reach toward the south (its depth, or its width if it faces east or west).
            float reach = facing.IsHorizontal ? outfit.halfWidth : outfit.halfDepth;
            float clear = (reach * Mathf.Max(def.dressScale.x, def.dressScale.z) + WeaponGap) * Mathf.Max(fit.x, fit.z);
            Vector3 centre = new Vector3(c.x + across, height, facing == Rot4.North ? c.z + clear : c.z - clear);

            bool flip = aim > 200f && aim < 340f;
            float angle = flip ? aim - 270f - weapon.def.equippedAngleOffset : aim - 90f + weapon.def.equippedAngleOffset;
            if (IsoRimMod.Settings.itemsFromArt && SpriteSolids.DrawUpright(weapon, centre, angle, flip, def.dressScale.y * fit.y))
            {
                return true;
            }
            return Patches.HeldWeapon3D(weapon, centre, aim);   // no sprite to make solid: its model, level
        }

        // The stand's clothes as figure parts (People.Dress), kept until its contents change.
        private static Outfit OutfitOf(Building_OutfitStand stand, BodyTypeDef body, bool skipHead)
        {
            ThingOwner things = ((IThingHolder)stand).GetDirectlyHeldThings();
            int version = things.Count * 2 + (skipHead ? 1 : 0);
            for (int i = 0; i < things.Count; i++)
            {
                version = version * 31 + things[i].thingIDNumber;
            }
            Outfit outfit;
            if (outfits.TryGetValue(stand.thingIDNumber, out outfit) && outfit.version == version && outfit.made)
            {
                return outfit;
            }
            if (outfit == null)
            {
                if (outfits.Count > 500)
                {
                    outfits.Clear();
                }
                outfit = new Outfit();
                outfits[stand.thingIDNumber] = outfit;
            }
            outfit.version = version;
            outfit.piecesVersion = -1;
            outfit.unshown.Clear();

            List<Apparel> worn = new List<Apparel>();
            Apparel hides = null;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Apparel a)
                {
                    worn.Add(a);
                    if (hides == null && a.def.apparel != null && a.def.apparel.renderSkipFlags.NotNullAndContains(RenderSkipFlagDefOf.Head))
                    {
                        hides = a;
                    }
                }
            }
            string build = People.BuildOf(body);
            People.Look look = new People.Look();
            People.Dress(look, build, worn, Color.white, outfit.unshown);

            // The body: only what some clothing covers (the stand's own body shows elsewhere).
            Begin();
            if (look.lowerWorn)
            {
                Add(look.lower, Matrix4x4.identity, look.lowerColor);
            }
            if (look.upperWorn)
            {
                Add(look.upper, Matrix4x4.identity, look.upperColor);
            }
            Add(look.legArmour, Matrix4x4.identity, look.legArmourColor);
            Add(look.coat, Matrix4x4.identity, look.coatColor);
            if (look.pads)
            {
                Add(People.Part("person_pads"), Matrix4x4.Scale(new Vector3(People.Shoulders(build), 1f, 1.05f)), look.coat != null ? look.coatColor : look.upperColor);
            }
            // How far the dressed body reaches (the shirt part stands for the stand's own body), for the weapon.
            outfit.halfDepth = look.upper != null ? look.upper.halfDepth : 0.15f;
            outfit.halfWidth = look.upper != null ? Mathf.Max(-look.upper.minX, look.upper.maxX) : 0.25f;
            for (int i = 0; i < partModels.Count; i++)
            {
                outfit.halfDepth = Mathf.Max(outfit.halfDepth, partModels[i].halfDepth * partLocals[i].m22);
                outfit.halfWidth = Mathf.Max(outfit.halfWidth, Mathf.Max(-partModels[i].minX, partModels[i].maxX) * partLocals[i].m00);
            }
            outfit.bodyKey = Key("b");

            // The head: the hat on the stand's head, as the game draws headgear only while the head shows;
            // with the head hidden by a garment that covers it (the burka), a hood in that garment's colour.
            Begin();
            if (!skipHead)
            {
                Add(look.hat, Matrix4x4.identity, look.hatColor);
                if (look.hat != null && look.visor)
                {
                    Add(People.Part("person_visor"), Matrix4x4.identity, Color.white);
                }
            }
            else if (hides != null && (People.Covers(hides.def.apparel, "FullHead") || People.Covers(hides.def.apparel, "UpperHead")))
            {
                Add(People.Part("person_hat_hood"), Matrix4x4.identity, People.ApparelColour(hides));
            }
            outfit.headKey = Key("h");
            outfit.made = true;
            return outfit;
        }

        private static void Begin()
        {
            partModels.Clear();
            partLocals.Clear();
            partTints.Clear();
        }

        private static void Add(Model m, Matrix4x4 local, Color tint)
        {
            if (m == null)
            {
                return;
            }
            partModels.Add(m);
            partLocals.Add(local);
            partTints.Add(tint);
        }

        // A name for the parts gathered (models, placings, colours); their meshes are made once per name.
        private static string Key(string kind)
        {
            if (partModels.Count == 0)
            {
                return null;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(kind);
            for (int i = 0; i < partModels.Count; i++)
            {
                Color32 t = partTints[i];
                sb.Append('|').Append(partModels[i].id).Append(':').Append(t.r).Append(',').Append(t.g).Append(',').Append(t.b)
                    .Append(':').Append(partLocals[i].m00.ToString("0.###"));
            }
            string key = sb.ToString();
            if (!meshes.ContainsKey(key))
            {
                if (meshes.Count > 300)
                {
                    foreach (List<DynamicModels.Part> old in meshes.Values)
                    {
                        DynamicModels.Destroy(old);
                    }
                    meshes.Clear();
                    foreach (Outfit o in outfits.Values)
                    {
                        o.made = false;   // worked out again, and its meshes made again, when next drawn
                    }
                }
                meshes[key] = DynamicModels.BuildMerged(new List<Model>(partModels), new List<Matrix4x4>(partLocals), new List<Color>(partTints), 0);
            }
            return key;
        }

        private static List<DynamicModels.Part> MeshesFor(string key)
        {
            List<DynamicModels.Part> parts;
            return key != null && meshes.TryGetValue(key, out parts) ? parts : null;
        }

        // Stands what the game draws flat about pivot upright over the stand at centre, square to the
        // camera but straight up (leaning back toward the camera, as a pawn's sprite does, its top would
        // sink into the model's body and head): pivot at height cells, scale times the game's size, the
        // game's heights squeezed into a thin stack held a little in front of the model.
        private static Matrix4x4 Card(Vector3 centre, float height, float scale, Vector3 pivot)
        {
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(centre.x, height / s, centre.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(-90f, View3D.Yaw, 0f))
                * Matrix4x4.Translate(new Vector3(0f, Nudge, 0f))
                * Matrix4x4.Scale(new Vector3(scale, DepthScale, scale))
                * Matrix4x4.Translate(-pivot);
        }

        // The side of the stand the camera sees: its own facing while the camera looks roughly north,
        // turned with the camera beyond that.
        public static Rot4 Seen(Rot4 facing)
        {
            int turns = Mathf.FloorToInt((Mathf.Repeat(View3D.Yaw, 360f) + 44f) / 90f) & 3;
            return new Rot4((facing.AsInt - turns + 4) & 3);
        }

        private static Vector3 HeadOffsetAt(BodyTypeDef body, Rot4 rot)
        {
            Vector2 o = body != null ? body.headOffset : Vector2.zero;
            switch (rot.AsInt)
            {
                case 1: return new Vector3(o.x, 0f, o.y);
                case 3: return new Vector3(-o.x, 0f, o.y);
                default: return new Vector3(0f, 0f, o.y);
            }
        }

        // The game's Building_OutfitStand.RecacheGraphics for the clothing with no 3D part, for the side
        // seen (the game works it out for the stand's own facing only), kept until the contents or the side change.
        private static List<Piece> PiecesFor(Outfit outfit, Rot4 seen, BodyTypeDef body)
        {
            if (outfit.piecesVersion == outfit.version && outfit.seen == seen)
            {
                return outfit.pieces;
            }
            outfit.piecesVersion = outfit.version;
            outfit.seen = seen;
            outfit.pieces.Clear();
            if (outfit.unshown.Count == 0)
            {
                return outfit.pieces;
            }

            List<Apparel> list = new List<Apparel>(outfit.unshown);
            list.SortBy(a => a.def.apparel.LastLayer.drawOrder);
            Dictionary<ApparelLayerDef, int> perLayer = new Dictionary<ApparelLayerDef, int>();
            foreach (Apparel item in list)
            {
                ApparelLayerDef lastLayer = item.def.apparel.LastLayer;
                bool headgear = lastLayer == ApparelLayerDefOf.Overhead || lastLayer == ApparelLayerDefOf.EyeCover;
                ApparelGraphicRecord rec;
                if (!ApparelGraphicRecordGetter.TryGetGraphicApparel(item, body, false, out rec))
                {
                    continue;
                }
                int count = perLayer.GetValueOrDefault(lastLayer, 0);
                perLayer[lastLayer] = count + 1;
                int layer = lastLayer.drawOrder / 10 + count;
                float? forRot = item.def.apparel.drawData?.LayerForRot(seen, -1f);
                if (forRot.HasValue && forRot.Value > 0f)
                {
                    layer += ((int)forRot.Value - (headgear ? 70 : 20)) * 2;
                }
                Vector3 scale = Vector3.one;
                Vector3 offset = Vector3.zero;
                if (item.RenderAsPack())
                {
                    Vector2 s = item.def.apparel.wornGraphicData.BeltScaleAt(seen, body);
                    scale.x *= s.x;
                    scale.z *= s.y;
                    Vector2 o = item.def.apparel.wornGraphicData.BeltOffsetAt(seen, body);
                    offset.x += o.x;
                    offset.z += o.y;
                    if (seen == Rot4.North)
                    {
                        layer = 93;
                    }
                    else if (seen == Rot4.South)
                    {
                        layer = -3;
                    }
                }
                outfit.pieces.Add(new Piece { graphic = rec.graphic, layer = layer, scale = scale, offset = offset, headgear = headgear });
            }
            return outfit.pieces;
        }

        // For the harness: what the stand shows and how.
        public static string Report(Building_OutfitStand stand)
        {
            bool skip = skipHeadField != null && (bool)skipHeadField.GetValue(stand);
            int apparel = 0;
            foreach (Thing t in ((IThingHolder)stand).GetDirectlyHeldThings())
            {
                if (t is Apparel)
                {
                    apparel++;
                }
            }
            Outfit o;
            outfits.TryGetValue(stand.thingIDNumber, out o);
            return "apparel " + apparel + ", weapon " + (stand.HeldWeapon != null ? stand.HeldWeapon.def.defName : "none") + ", head hidden " + skip
                + ", seen " + Seen(stand.Rotation) + ", model " + SectionLayer_IsoRimBodies.StandsIn(stand)
                + (o != null ? ", 3D parts " + (o.bodyKey ?? "-") + " / " + (o.headKey ?? "-") + ", as sprites " + o.unshown.Count : "");
        }
    }
}
