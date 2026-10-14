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
    // and head, the apparel it holds on the body and head, and the weapon it holds. With the model,
    // the stand and its body are baked like any realtime building (SectionLayer_IsoRimBodies); here
    // go the rest: the stand's head model (headModel), left out as the game leaves out its head
    // sprite while worn headgear hides the head; and the game's own apparel and weapon sprites,
    // laid out exactly as the game lays them out (layers, packs, head offset, weapon place), then
    // stood upright facing the camera over the model: the body's sprites at apparelHeight, the
    // headgear at apparelHeadHeight. The sprites are those for the side of the stand the camera
    // sees, so a stand turned away shows its back.
    public static class Stands
    {
        private const float DepthScale = 0.3f;   // the game's drawing heights, squeezed into a thin stack toward the camera
        private const float Nudge = 0.36f;       // and that stack held this far in front of the model's body, in cells

        private struct Piece
        {
            public Graphic graphic;
            public int layer;
            public Vector3 scale;
            public Vector3 offset;
            public bool headgear;
        }

        private class Cache
        {
            public Rot4 seen;
            public int version;
            public readonly List<Piece> pieces = new List<Piece>();
        }

        private static readonly Dictionary<int, Cache> caches = new Dictionary<int, Cache>();
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
            Vector3 fit = entry.Fit(stand.def) * def.scale;

            if (!skipHead && entry.head != null)
            {
                DynamicModels.DrawLocal(entry.head, new Vector3(c.x, 0f, c.z), yaw, Matrix4x4.Translate(def.offset) * Matrix4x4.Scale(fit),
                    R3DTextures.ModelTint(stand), R3DTextures.ModelSwatch(stand));
            }

            Rot4 seen = Seen(stand.Rotation);
            List<Piece> pieces = PiecesFor(stand, seen, body);
            ThingWithComps weapon = stand.HeldWeapon;
            if (pieces.Count == 0 && weapon == null)
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
                if (weapon != null)
                {
                    // Where the game holds it: in front of the body, or behind it seen from the back.
                    Vector3 at = seen != Rot4.North
                        ? new Vector3(drawLoc.x, AltitudeLayer.ItemImportant.AltitudeFor() + PawnRenderUtility.AltitudeForLayer(90f), drawLoc.z)
                        : new Vector3(drawLoc.x, itemY - 0.05f, drawLoc.z);
                    PawnRenderUtility.DrawCarriedWeapon(weapon, at, seen, (float)weaponFactorGetter.Invoke(stand, null));
                }

                // The headgear, over the head unless something worn hides the head (as the game).
                if (!skipHead)
                {
                    float importantY = AltitudeLayer.ItemImportant.AltitudeFor();
                    Vector3 head = drawLoc + HeadOffsetAt(body, seen);
                    DrawHook.xform = Card(c, def.apparelHeadHeight * fit.y, scale, new Vector3(head.x, importantY, head.z));
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

        // The game's Building_OutfitStand.RecacheGraphics, for the side seen (the game works it out
        // for the stand's own facing only), kept until the stand's contents or the side seen change.
        private static List<Piece> PiecesFor(Building_OutfitStand stand, Rot4 seen, BodyTypeDef body)
        {
            ThingOwner things = ((IThingHolder)stand).GetDirectlyHeldThings();
            int version = things.Count;
            for (int i = 0; i < things.Count; i++)
            {
                version = version * 31 + things[i].thingIDNumber;
            }
            Cache cache;
            if (!caches.TryGetValue(stand.thingIDNumber, out cache))
            {
                if (caches.Count > 500)
                {
                    caches.Clear();
                }
                cache = new Cache();
                cache.version = version + 1;
                caches[stand.thingIDNumber] = cache;
            }
            if (cache.version == version && cache.seen == seen)
            {
                return cache.pieces;
            }
            cache.version = version;
            cache.seen = seen;
            cache.pieces.Clear();

            List<Apparel> list = new List<Apparel>();
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Apparel a)
                {
                    list.Add(a);
                }
            }
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
                cache.pieces.Add(new Piece { graphic = rec.graphic, layer = layer, scale = scale, offset = offset, headgear = headgear });
            }
            return cache.pieces;
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
            return "apparel " + apparel + ", weapon " + (stand.HeldWeapon != null ? stand.HeldWeapon.def.defName : "none") + ", head hidden " + skip
                + ", seen " + Seen(stand.Rotation) + ", model " + SectionLayer_IsoRimBodies.StandsIn(stand);
        }
    }
}
