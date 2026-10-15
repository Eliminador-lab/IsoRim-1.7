using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // People drawn as 3D figures in place of their sprites.
    //
    // A figure keeps the game's own proportions (a big round head on a simple body with no arms
    // or legs) and is put together from plain parts, each coloured from the person it shows:
    //   - the body, in the game's five builds (and smaller for children);
    //   - skin colour; hair in the person's own colour and roughly their own cut; a beard;
    //   - what they are wearing: trousers and shirt colour the lower and upper body, a jacket or
    //     a long coat goes over them, armour adds plates, and a hat, helmet or hood goes on top.
    //     Coats, robes, suits and straps the mod has a shape for (CoatShapes) get it; a flak vest under one that is
    //     open at the front shows its front plate in the opening, and under a strap it shows whole with the strap
    //     made to go over it, as the game's sprites do.
    // Because all of it is read from the person, anyone the game makes up gets a figure to match.
    // Like the animals, a figure turns to face the way it is really walking.
    // Anomaly: a ghoul gets its own head (by its forced head type), its own hair, the ridged ghoulskin over the
    // body and its implanted barbs and plating (by hediff); a shambler keeps its own head and clothes, with
    // scars on the face and, while nothing covers the body, wounds on it; a creepjoiner (race CreepJoiner)
    // with one of the special head types (leathery, dark scholar, cult escapee, timeless) gets its own face,
    // an eye that is missing left out.
    public static class People
    {
        // What one person looks like. Worked out from the pawn now and then, not every frame.
        internal class Look
        {
            public int frame;
            public bool upperWorn;   // some clothing covers the upper body (the shirt part shows it)
            public bool lowerWorn;   // and the legs
            public float size = 1f;
            public float headWide = 1f;
            public float shoulders = 1f;
            public Vector3 bodyScale = Vector3.one;   // the body and all worn on it, by body type (BodyScales)
            public Model lower;
            public Model upper;
            public Model coat;
            public Model underCoat;   // a flak vest under the coat: its front plate in an open coat's front (no outline), or whole under a strap
            public Model hair;
            public Model hairTop;     // a hairstyle's separate part (topbun_tail's bun), drawn with the hair
            public bool hairShown;    // the game draws hair now (no headgear hides it)
            public string hatKind;    // for Report
            public Model beard;
            public Model hat;
            public Model legArmour;
            public bool pads;
            public bool visor;
            public bool faceHidden;   // the game leaves out the eyes (skip flag Eyes): no face, eyes or scars
            public bool headHidden;   // the game leaves out the whole head (skip flag Head, the burka): its covering (hat) instead
            public bool bedHidesHat;  // in a bed that hides headgear when this look was worked out (People.BedHidesHeadgear)
            public Color skin = Color.white;
            public Color hairColor = Color.white;
            public Color upperColor = Color.white;
            public Color lowerColor = Color.white;
            public Color coatColor = Color.white;
            public Color underCoatColor = Color.white;
            public Color hatColor = Color.white;
            public Color legArmourColor = Color.white;
            public Model mouthWear;   // a mouth-only item (cloth mask), head space
            public Model eyeWear;     // an eye cover (blindfold), head space
            public Model neckWear;    // a neck-only item (slave collar), body space
            public Color mouthColor = Color.white;
            public Color eyeColor = Color.white;
            public Color neckColor = Color.white;
            public Model ownHead;     // replaces head and face (ghoul heads), already as wide as the head type
            public Model ownFace;     // replaces the face (creepjoiner faces), tinted faceTint
            public Model eyeLeft;     // the creepjoiner face's separate eyes (cult escapee), left out when missing
            public Model eyeRight;
            public Model scars;       // over the face (shambler)
            public Model skinOverlay; // over the body (ghoulskin ridges, shambler wounds)
            public Model barbs;       // ghoul implants
            public Model plating;
            public bool ownHair;      // hair made for the ghoul head: not widened again, fixed colour
            public Color faceTint = Color.white;
            // Packs, belts and waist tools (WornGear), each with its place (figure space, before bodyScale) and colour;
            // rigid ones keep their own size (only their place takes bodyScale).
            public readonly List<Model> gear = new List<Model>();
            public readonly List<Matrix4x4> gearPlace = new List<Matrix4x4>();
            public readonly List<Color> gearTint = new List<Color>();
            public readonly List<bool> gearRigid = new List<bool>();
            public readonly List<Vector3> gearAnchor = new List<Vector3>();   // a rigid one's own point that goes to its place

            public bool SameGear(Look o)
            {
                if (o.gear.Count != gear.Count)
                {
                    return false;
                }
                for (int i = 0; i < gear.Count; i++)
                {
                    if (o.gear[i] != gear[i] || o.gearPlace[i] != gearPlace[i] || o.gearTint[i] != gearTint[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            // Where gear i goes on the figure, with the body type's scale.
            public Matrix4x4 GearOnBody(int i)
            {
                if (!gearRigid[i])
                {
                    return Matrix4x4.Scale(bodyScale) * gearPlace[i];
                }
                Vector3 t = gearPlace[i].GetColumn(3);
                return Matrix4x4.Translate(Vector3.Scale(bodyScale, t) - gearAnchor[i]);
            }

            // Ready-made meshes of this look, by facing, body or head, and alive or dead.
            public readonly Dictionary<int, List<DynamicModels.Part>> meshes = new Dictionary<int, List<DynamicModels.Part>>();

            public bool SameAs(Look o)
            {
                return o != null && size == o.size && headWide == o.headWide && shoulders == o.shoulders && bodyScale == o.bodyScale && lower == o.lower && upper == o.upper
                    && coat == o.coat && underCoat == o.underCoat && underCoatColor == o.underCoatColor && hair == o.hair && hairTop == o.hairTop && beard == o.beard && hat == o.hat && pads == o.pads && visor == o.visor
                    && legArmour == o.legArmour && legArmourColor == o.legArmourColor && faceHidden == o.faceHidden && headHidden == o.headHidden
                    && skin == o.skin && hairColor == o.hairColor && upperColor == o.upperColor && lowerColor == o.lowerColor
                    && coatColor == o.coatColor && hatColor == o.hatColor && ownHead == o.ownHead && ownFace == o.ownFace && eyeLeft == o.eyeLeft
                    && eyeRight == o.eyeRight && scars == o.scars && skinOverlay == o.skinOverlay && barbs == o.barbs && plating == o.plating
                    && ownHair == o.ownHair && faceTint == o.faceTint && SameGear(o)
                    && mouthWear == o.mouthWear && eyeWear == o.eyeWear && neckWear == o.neckWear
                    && mouthColor == o.mouthColor && eyeColor == o.eyeColor && neckColor == o.neckColor;
            }

            public void Forget()
            {
                foreach (List<DynamicModels.Part> parts in meshes.Values)
                {
                    DynamicModels.Destroy(parts);
                }
                meshes.Clear();
            }
        }

        private static readonly List<Model> mergeModels = new List<Model>();
        private static readonly List<Matrix4x4> mergeLocals = new List<Matrix4x4>();
        private static readonly List<Color> mergeTints = new List<Color>();

        // The body (head = false) or the head of a figure as ready-made meshes.
        private static List<DynamicModels.Part> Merged(Look look, bool headPart, bool dead, int turned)
        {
            // The meshes carry no shading (the shader lights them), so one set serves every light and turn.
            int key = (headPart ? 2 : 0) + (dead ? 1 : 0);
            List<DynamicModels.Part> parts;
            if (look.meshes.TryGetValue(key, out parts))
            {
                return parts;
            }
            mergeModels.Clear();
            mergeLocals.Clear();
            mergeTints.Clear();
            if (headPart)
            {
                Matrix4x4 headPlace = Matrix4x4.Scale(new Vector3(look.headWide, 1f, 1f));
                Color skin = dead ? Color.Lerp(look.skin, new Color(0.55f, 0.58f, 0.52f), 0.35f) : look.skin;
                if (look.headHidden)
                {
                    // No head at all, as the game draws it: the covering (look.hat) takes its place.
                }
                else if (look.ownHead != null)
                {
                    AddPart(look.ownHead, Matrix4x4.identity, Color.white);   // its skin colour is in the model
                }
                else
                {
                    AddPart(head, headPlace, skin);
                    if (!look.faceHidden)
                    {
                        AddPart(look.ownFace ?? face, headPlace, look.ownFace != null ? look.faceTint : Color.white);
                        AddPart(look.eyeLeft, headPlace, Color.white);
                        AddPart(look.eyeRight, headPlace, Color.white);
                        AddPart(look.scars, headPlace, Color.white);
                    }
                }
                if (look.ownHair)
                {
                    AddPart(look.hair, Matrix4x4.identity, Color.white);
                }
                else
                {
                    AddPart(look.hair, headPlace, look.hairColor);
                    AddPart(look.hairTop, headPlace, look.hairColor);
                }
                AddPart(look.beard, headPlace, look.hairColor);
                AddPart(look.eyeWear, headPlace, look.eyeColor);
                AddPart(look.mouthWear, headPlace, look.mouthColor);
                AddPart(look.hat, headPlace, look.hatColor);
                if (look.hat != null && look.visor)
                {
                    AddPart(visor, headPlace, Color.white);
                }
            }
            else
            {
                Matrix4x4 bodyPlace = Matrix4x4.Scale(look.bodyScale);
                AddPart(look.lower, bodyPlace, look.lowerColor);
                AddPart(look.upper, bodyPlace, look.upperColor);
                AddPart(look.skinOverlay, bodyPlace, Color.white);
                AddPart(look.barbs, bodyPlace, Color.white);
                AddPart(look.plating, bodyPlace, Color.white);
                AddPart(look.legArmour, bodyPlace, look.legArmourColor);
                AddPart(look.underCoat, bodyPlace, look.underCoatColor);
                AddPart(look.coat, bodyPlace, look.coatColor);
                AddPart(look.neckWear, bodyPlace, look.neckColor);
                for (int i = 0; i < look.gear.Count; i++)
                {
                    AddPart(look.gear[i], look.GearOnBody(i), look.gearTint[i]);
                }
                if (look.pads)
                {
                    AddPart(pads, bodyPlace * Matrix4x4.Scale(new Vector3(look.shoulders, 1f, 1.05f)), look.coat != null ? look.coatColor : look.upperColor);
                }
            }
            parts = DynamicModels.BuildMerged(mergeModels, mergeLocals, mergeTints, turned);
            look.meshes[key] = parts;
            return parts;
        }

        private static void AddPart(Model m, Matrix4x4 local, Color tint)
        {
            if (m == null)
            {
                return;
            }
            mergeModels.Add(m);
            mergeLocals.Add(local);
            mergeTints.Add(tint);
        }

        private const int RefreshFrames = 90;
        private const float HeadHeight = 0.98f;   // where the middle of the head is on the figure
        private const float GibbetScale = 0.78f;  // a corpse hanging in a gibbet cage, shrunk to stay under its dome
        private const float FigureHeight = 1.12f; // top of the head of a figure of size 1
        private const float CarryReach = 0.24f;   // a carried thing's middle, in front of the figure
        private const float CarryHeight = 0.36f;  // and the height of its bottom

        private static readonly Dictionary<int, Look> looks = new Dictionary<int, Look>();
        private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();
        private static Model head;
        private static Model face;
        private static Model visor;
        private static Model pads;
        private static bool tried;

        // Draws the person at drawLoc. Returns false if they cannot be drawn as a figure, in
        // which case the game's sprite must be drawn as usual. handHeight is how high above the
        // ground a weapon is held and facing which way the figure faces, for whoever draws the weapon.
        public static bool Draw(Pawn pawn, Vector3 drawLoc, out float handHeight, out float facing, out bool standing)
        {
            handHeight = 0.5f;
            facing = 0f;
            standing = false;
            if (pawn.story == null || !FigureRace(pawn) || Hidden(pawn))
            {
                return false;
            }
            if (!tried)
            {
                tried = true;
                head = Part("person_head");
                face = Part("person_face");
                if (face != null)
                {
                    // The eyes are flat decals on the face: an outline shell would poke out as dark spikes at grazing angles.
                    face.noOutline = new bool[face.positions.Length];
                    for (int i = 0; i < face.noOutline.Length; i++)
                    {
                        face.noOutline[i] = true;
                    }
                }
                visor = Part("person_visor");
                pads = Part("person_pads");
            }
            if (head == null)
            {
                return false;
            }
            Look look = LookOf(pawn);
            if (look == null || look.upper == null || look.lower == null)
            {
                return false;
            }

            bool dead = pawn.Dead;
            float lift = Carried.LiftOf(pawn, ref drawLoc);   // in mid-jump, or carried by another 3D pawn
            // A corpse in a gibbet cage shown as its model hangs upright in the cage (lying, an
            // adult would reach out through the bars), feet on the cage floor.
            Building_GibbetCage gibbet = dead ? GibbetOf(pawn) : null;
            // A person held in a glass vat shown as its model that floats them upright (growth vat):
            // standing in the vat, shrunk to fit under its roof.
            Building holder = dead ? null : Glass.HolderOf(pawn);
            ModelEntry vat = holder != null ? Glass.EntryFor(holder) : null;
            if (vat != null && !vat.def.heldPawnUpright)
            {
                vat = null;
            }
            bool lying = gibbet == null && vat == null && (dead || pawn.GetPosture() != PawnPosture.Standing);
            float size = look.size * (gibbet != null ? GibbetScale : 1f);
            float rise = 0f;
            if (vat != null)
            {
                rise = Glass.FloatRise(Find.TickManager.TicksGame);
                size = Mathf.Min(size, (Glass.Room(vat) - rise - 0.02f) / FigureHeight);
            }
            float yaw;
            float phase = 0f;
            float gait = 0f;
            if (gibbet != null)
            {
                yaw = 180f + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 50f;
            }
            else if (vat != null)
            {
                yaw = holder.Rotation.AsAngle + vat.def.rotation + 180f;   // facing the vat's front
            }
            else if (dead)
            {
                yaw = pawn.Rotation.AsAngle + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 60f;
            }
            else
            {
                Animals.Track(pawn, drawLoc, lying, 0.9f * size, out yaw, out phase, out gait);
            }

            float ground = 0f;
            bool headOnly = false;
            Matrix4x4 pose;
            Model workTool = null;
            Matrix4x4 workLocal = Matrix4x4.identity;
            float workLean = 0f;
            float workDip = 0f;
            if (lying)
            {
                // On their back, head toward the south of the figure's own space before it is turned.
                Quaternion onBack = Quaternion.AngleAxis(-90f, Vector3.right);
                Building_Bed bed = dead ? null : pawn.CurrentBed();
                if (bed != null)
                {
                    // In a bed the person's place is where their head is, and they lie along the bed.
                    yaw = bed.Rotation.AsAngle;
                    ground = Forms.HeightOf(bed);
                    headOnly = ground > 0.05f && bed.def.building != null && !bed.def.building.bed_showSleeperBody;
                    pose = Matrix4x4.Translate(new Vector3(0f, (headOnly ? 0.24f : 0.15f) * size, HeadHeight * size)) * Matrix4x4.Rotate(onBack);
                }
                else if (!dead && holder != null && pawn.ParentHolder is IThingHolderWithDrawnPawn drawnIn)
                {
                    // Lying in a building that shows who is inside (gene extractor, biosculpter pod): on its
                    // couch, along the body angle the game draws them at, centred where the game draws them.
                    yaw = drawnIn.HeldPawnBodyAngle + 180f;
                    ground = Forms.HeightOf(holder);
                    float swayAngle;
                    Vector3 swayShift;
                    if (holder is Building_HoldingPlatform && HoldingChains.Sway(pawn, out swayAngle, out swayShift))
                    {
                        yaw += swayAngle;   // straining against the chains, as the game moves its sprite
                        drawLoc += swayShift;
                    }
                    pose = Matrix4x4.Translate(new Vector3(0f, 0.15f * size, HeadHeight * 0.5f * size)) * Matrix4x4.Rotate(onBack);
                }
                else
                {
                    pose = Matrix4x4.Translate(new Vector3(0f, 0.15f * size, 0.55f * size)) * Matrix4x4.Rotate(onBack);
                    if (!dead && pawn.pather != null && pawn.pather.Moving)
                    {
                        yaw += 180f;   // crawling: head first, the way they are going
                    }
                }
            }
            else if (vat != null)
            {
                // Floating still in the middle of the vat.
                ground = Glass.Floor(vat) + rise;
                pose = Matrix4x4.identity;
                Vector3 c = Glass.ContentCentre(holder, vat);
                drawLoc = new Vector3(c.x, drawLoc.y, c.z);
            }
            else if (gibbet != null)
            {
                // Hanging slack: a slight lean, no step, nothing held.
                ground = Forms.HeightOf(gibbet);
                pose = Matrix4x4.Rotate(Quaternion.AngleAxis(5f, Vector3.right));
                Vector3 c = gibbet.TrueCenter();
                drawLoc = new Vector3(c.x, drawLoc.y, c.z);
            }
            else
            {
                // A little waddle and bounce in step.
                float bob = Mathf.Abs(Mathf.Sin(phase)) * 0.035f * size * gait;
                float sway = Mathf.Sin(phase) * 4.5f * gait;
                pose = Matrix4x4.Translate(new Vector3(0f, bob, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(sway, Vector3.forward));
                standing = true;

                // At work: face the work, lean into each stroke and swing the tool.
                if (IsoRimMod.Settings.workAnimations && WorkAnimations.Animate(pawn, drawLoc, ref yaw, out workTool, out workLocal, out workLean, out workDip))
                {
                    pose = Matrix4x4.Translate(new Vector3(0f, -workDip * size, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(workLean, Vector3.right));
                }
            }

            Vector3 at = new Vector3(drawLoc.x, ground + lift, drawLoc.z);
            Matrix4x4 whole = pose * Matrix4x4.Scale(new Vector3(size, size, size));

            // The whole figure is two ready-made meshes (body and head), each a single draw, put
            // together once for this look and the way it is facing.
            int turned = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 45f) % 8;
            if (!headOnly)
            {
                DynamicModels.DrawParts(Merged(look, false, dead, turned), at, yaw, whole);
            }
            DynamicModels.DrawParts(Merged(look, true, dead, turned), at, yaw, whole);
            if (workTool != null)
            {
                // The tool is placed in the figure's own space, so it leans and dips with it.
                DynamicModels.DrawLocal(workTool, at, yaw, whole * workLocal, Color.white, "Wood");
            }

            if (ground <= 0f && lift <= 0f)
            {
                GroundShadows.Blob(at, yaw, (lying ? 0.30f : 0.34f) * size * look.bodyScale.x, (lying ? 0.70f : 0.30f * look.bodyScale.z) * size);
            }
            if (standing && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                // Held in front, at the hands (the game draws it a little in front of the pawn the way it faces).
                Vector3 front = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
                float reach = CarryReach * size * Mathf.Max(1f, look.bodyScale.z);   // clear of a deeper body
                Carried.Draw(pawn, new Vector3(at.x + front.x * reach, at.y + CarryHeight * size, at.z + front.z * reach), yaw);
            }
            handHeight = 0.46f * size + lift;
            facing = yaw;
            return true;
        }

        // The gibbet cage holding this dead person's corpse, while the cage is drawn as its model; else null.
        public static Building_GibbetCage GibbetOf(Pawn pawn)
        {
            Corpse corpse = pawn.Corpse;
            Building_GibbetCage cage = corpse != null ? corpse.ParentHolder as Building_GibbetCage : null;
            return cage != null && cage.Spawned && View3D.Ready && Forms.For(cage.def).form == Form3D.Model ? cage : null;
        }

        // For the harness (probe entitycheck): the Anomaly parts of a person's figure.
        public static string Report(Pawn pawn)
        {
            if (pawn.story == null || !FigureRace(pawn))
            {
                return "no figure (" + (pawn.def != null ? pawn.def.defName : "?") + ")";
            }
            Look l = Work(pawn);
            Func<Model, string> n = m => m == null ? "-" : (m == head ? "person_head" : (m == face ? "person_face" : m.id.ToString()));
            return "race " + pawn.def.defName + ", mutant " + (pawn.IsMutant ? pawn.mutant.Def.defName : "none") + ", head " + (pawn.story.headType != null ? pawn.story.headType.defName : "?")
                + ", hair " + (pawn.story.hairDef != null ? pawn.story.hairDef.defName : "?") + " -> " + (HairParts.Hair(pawn.story.hairDef) ?? "none")
                + (l.hair != null ? "" : " (not drawn)") + (l.hairTop != null ? " +bun" : "")
                + ", beard " + (pawn.style != null && pawn.style.beardDef != null ? pawn.style.beardDef.defName : "?") + " -> "
                + (pawn.style != null ? HairParts.Beard(pawn.style.beardDef) ?? "none" : "none") + (l.beard != null ? "" : " (not drawn)") + ", body " + (pawn.story.bodyType != null ? pawn.story.bodyType.defName : "?")
                + ", ownHead " + (l.ownHead != null) + ", ownFace " + (l.ownFace != null) + ", eyes " + (l.eyeLeft != null) + "/" + (l.eyeRight != null)
                + ", scars " + (l.scars != null) + ", overlay " + (l.skinOverlay != null) + ", barbs " + (l.barbs != null) + ", plating " + (l.plating != null)
                + ", ownHair " + l.ownHair + ", hat " + (l.hatKind ?? "-") + ", faceHidden " + l.faceHidden + ", headHidden " + l.headHidden + ", skips " + SkipNames(VanillaSkips(pawn)) + ", skin " + l.skin + ", faceTint " + l.faceTint + ", drawn " + Patches.DrawnAsModel(pawn);
        }

        // The races drawn as figures: people, and creepjoiners (people in all but race).
        public static bool FigureRace(Pawn pawn)
        {
            return pawn.def != null && (pawn.def.defName == "Human" || pawn.def.defName == "CreepJoiner");
        }

        // People who are invisible must stay so: they keep their sprite, which the game hides.
        public static bool Hidden(Pawn pawn)
        {
            if (pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }
            List<Hediff> all = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].def != null && all[i].def.defName != null && all[i].def.defName.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        internal static Model Part(string name)
        {
            Model m;
            if (models.TryGetValue(name, out m))
            {
                return m;
            }
            m = IsoRimMod.RootDir != null ? ModelLibrary.LoadFile(Path.Combine(IsoRimMod.RootDir, "Models", name + ".obj")) : null;
            models[name] = m;
            return m;
        }

        private static Look LookOf(Pawn pawn)
        {
            Look look;
            int frame = Time.frameCount;
            // Worked out again at once on getting into or out of a bed that hides headgear.
            if (looks.TryGetValue(pawn.thingIDNumber, out look) && frame - look.frame < RefreshFrames && look.bedHidesHat == BedHidesHeadgear(pawn))
            {
                return look;
            }
            Look old = look;
            if (looks.Count > 600)
            {
                foreach (Look l in looks.Values)
                {
                    l.Forget();
                }
                looks.Clear();   // people long gone; the ones still about are worked out again
                old = null;
            }
            try
            {
                look = Work(pawn);
                if (old != null)
                {
                    if (look != null && old.SameAs(look))
                    {
                        look = old;   // nothing has changed: keep the meshes already made
                    }
                    else
                    {
                        old.Forget();
                    }
                }
            }
            catch (Exception e)
            {
                look = null;
                Log.ErrorOnce("[IsoRim] Working out what a person looks like failed: " + e, 93611090);
            }
            if (look != null)
            {
                // Spread the refreshes out so a crowd is not all worked out on the same frame.
                look.frame = frame - (pawn.thingIDNumber & 31);
                looks[pawn.thingIDNumber] = look;
            }
            return look;
        }

        private static bool Has(string text, string word)
        {
            return text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Look Work(Pawn pawn)
        {
            Look look = new Look();
            Pawn_StoryTracker story = pawn.story;

            // Build.
            string build = BuildOf(story.bodyType);
            look.shoulders = Shoulders(build);
            look.bodyScale = BodyScales.For(story.bodyType);
            float grown = 1f;
            if (pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null)
            {
                grown = pawn.ageTracker.CurLifeStage.bodySizeFactor;
            }
            look.size = SizeFor(grown);

            // Head, hair and beard.
            string headName = story.headType != null ? (story.headType.defName ?? "") : "";
            look.headWide = Has(headName, "Narrow") ? 0.9f : (Has(headName, "Wide") ? 1.07f : 1f);
            look.skin = story.SkinColor;
            look.hairColor = story.HairColor;
            string hairKind = HairParts.Hair(story.hairDef);
            string beardKind = pawn.style != null ? HairParts.Beard(pawn.style.beardDef) : null;

            string hatKind = Dress(look, build, pawn.apparel != null ? pawn.apparel.WornApparel : null, look.skin, null);

            // Headgear is left out where the game leaves it out (in a bed that hides the sleeper's body); then
            // hair, beard and eyes show under it exactly when the game draws them (its own skip flags).
            look.bedHidesHat = BedHidesHeadgear(pawn);
            PawnDrawParms parms = DrawParms(pawn);
            if (!PawnRenderNodeWorker_Apparel_Head.HeadgearVisible(parms))
            {
                hatKind = null;
                look.hat = null;
                look.visor = false;
                look.mouthWear = null;
                look.eyeWear = null;
                look.neckWear = null;
            }
            ulong skip = VanillaSkips(pawn, parms);
            // A garment that hides the whole head (skip flag Head: the burka): no head, a closed covering in its colour.
            Apparel hidesHead = (skip & RenderSkipFlagDefOf.Head) != 0 ? HeadHider(pawn) : null;
            if (hidesHead != null)
            {
                look.headHidden = true;
                look.hat = Part("person_hat_burka");
                look.hatColor = ApparelColour(hidesHead);
                look.visor = false;
                look.mouthWear = null;
                look.eyeWear = null;
                hatKind = "burka";
            }
            look.hatKind = hatKind;
            look.hairShown = !Skips(skip, RenderSkipFlagDefOf.Hair);
            if (hairKind != null && look.hairShown)
            {
                look.hair = Part("person_hair_" + hairKind);
                if (HairParts.Exists("person_hair_" + hairKind + "_bun"))
                {
                    look.hairTop = Part("person_hair_" + hairKind + "_bun");
                }
            }
            if (beardKind != null && !Skips(skip, RenderSkipFlagDefOf.Beard))
            {
                look.beard = Part("person_beard_" + beardKind);
            }
            look.faceHidden = (skip & RenderSkipFlagDefOf.Eyes) != 0;
            Anomaly(pawn, look, build, headName);
            return look;
        }

        // The game's own choice of what to leave out of a person's head (PawnRenderTree.AdjustParms: each worn
        // apparel's renderSkipFlags, or for apparel without them Hair under an UpperHead piece and Hair, Beard and
        // Eyes under a FullHead piece, the eyes put back for facings in forceEyesVisibleForRotations), as the
        // person is on the map facing south (the side the figure's face is on), in their bed if any (no flags at all
        // where the bed hides headgear). Patches to it apply too.
        private delegate void AdjustParmsCall(PawnRenderTree tree, ref PawnDrawParms parms);
        private static AdjustParmsCall adjustParms;
        private static bool adjustParmsLooked;

        internal static ulong VanillaSkips(Pawn pawn)
        {
            return VanillaSkips(pawn, DrawParms(pawn));
        }

        // The game's draw parameters that matter for headgear (PawnRenderer.GetDrawParms on the map): the person,
        // dead or not, posture and the bed they lie in, facing south.
        private static PawnDrawParms DrawParms(Pawn pawn)
        {
            PawnDrawParms parms = PawnDrawParms.Default;
            parms.pawn = pawn;
            parms.dead = pawn.Dead;
            parms.posture = pawn.GetPosture();
            parms.bed = pawn.CurrentBed();
            return parms;
        }

        // True while the person lies in a bed that hides headgear (PawnRenderNodeWorker_Apparel_Head.HeadgearVisible:
        // a bed without bed_showSleeperBody).
        internal static bool BedHidesHeadgear(Pawn pawn)
        {
            if (pawn.Dead || pawn.GetPosture() == PawnPosture.Standing)
            {
                return false;
            }
            Building_Bed bed = pawn.CurrentBed();
            return bed != null && bed.def.building != null && !bed.def.building.bed_showSleeperBody;
        }

        // The worn garment that hides the whole head (renderSkipFlags Head), or null.
        private static Apparel HeadHider(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            for (int i = 0; worn != null && i < worn.Count; i++)
            {
                if (worn[i].def.apparel != null && worn[i].def.apparel.renderSkipFlags.NotNullAndContains(RenderSkipFlagDefOf.Head))
                {
                    return worn[i];
                }
            }
            return null;
        }

        private static ulong VanillaSkips(Pawn pawn, PawnDrawParms parms)
        {
            if (!adjustParmsLooked)
            {
                adjustParmsLooked = true;
                MethodInfo m = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
                try
                {
                    adjustParms = m != null ? (AdjustParmsCall)Delegate.CreateDelegate(typeof(AdjustParmsCall), null, m) : null;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Can't use the game's headgear rule: " + e.Message, 93611091);
                }
            }
            PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
            if (adjustParms == null || tree == null)
            {
                return 0;
            }
            adjustParms(tree, ref parms);
            return parms.skipFlags;
        }

        // For Report: the names of the set skip flags.
        private static string SkipNames(ulong skip)
        {
            List<string> names = new List<string>();
            foreach (RenderSkipFlagDef f in DefDatabase<RenderSkipFlagDef>.AllDefsListForReading)
            {
                if (f != RenderSkipFlagDefOf.None && (skip & f) != 0)
                {
                    names.Add(f.defName);
                }
            }
            return names.Count > 0 ? string.Join("+", names) : "none";
        }

        // Hair or beard is left out when its own flag is set, or Head (both hang under the head).
        private static bool Skips(ulong skip, RenderSkipFlagDef flag)
        {
            return (skip & flag) != 0 || (skip & RenderSkipFlagDefOf.Head) != 0;
        }

        // Ghoul, shambler and creepjoiner parts (Anomaly), on top of what Work found.
        private static void Anomaly(Pawn pawn, Look look, string build, string headName)
        {
            if (pawn.IsGhoul)
            {
                string headKind = headName.StartsWith("Ghoul_") ? headName.Substring(6).ToLowerInvariant() : "normal";
                if (headKind != "normal" && headKind != "heavy" && headKind != "narrow" && headKind != "wide")
                {
                    headKind = "normal";
                }
                look.ownHead = Part("ghoul_head_" + headKind);
                if (look.ownHead != null)
                {
                    string hairName = pawn.story.hairDef != null ? (pawn.story.hairDef.defName ?? "") : "";
                    Model hair = hairName.StartsWith("Ghoul_") && look.hairShown ? Part("hairs_" + hairName.Substring(6).ToLowerInvariant() + "_" + headKind) : null;
                    if (hair != null)
                    {
                        look.hair = hair;
                        look.ownHair = true;
                    }
                    look.beard = null;
                }
                look.skinOverlay = Part("ghoul_" + build + "_body_overlay");
                HediffSet hediffs = pawn.health != null ? pawn.health.hediffSet : null;
                if (hediffs != null && HediffDefOf.GhoulBarbs != null && hediffs.HasHediff(HediffDefOf.GhoulBarbs))
                {
                    look.barbs = Part("implants_" + build + "_barbs");
                }
                if (hediffs != null && HediffDefOf.GhoulPlating != null && hediffs.HasHediff(HediffDefOf.GhoulPlating))
                {
                    look.plating = Part("implants_" + build + "_plating");
                }
                return;
            }
            if (pawn.IsShambler)
            {
                look.scars = Part("shambler_head_scars");
                if (!look.upperWorn && !look.lowerWorn && look.coat == null)
                {
                    look.skinOverlay = Part("shambler_" + build + "_body_overlay");
                }
                return;
            }
            if (pawn.def.defName != "CreepJoiner")
            {
                return;
            }
            string face = null;
            switch (headName)
            {
                case "Leathery_Male": face = "leathery_male_face"; break;
                case "Leathery_Female": face = "leathery_female_face"; break;
                case "DarkScholar_Male": face = "scholar_male_face"; break;
                case "DarkScholar_Female": face = "scholar_female_face"; break;
                case "CultEscapee": face = "cult_escapee_face"; break;
                case "TimelessOne": face = "timeless_face"; break;
            }
            if (face == null)
            {
                return;
            }
            look.ownFace = Part(face);
            // The creases are a darker shade of the skin (the model's crease colour is for a mid skin).
            look.faceTint = new Color(Mathf.Clamp01(look.skin.r * 1.35f), Mathf.Clamp01(look.skin.g * 1.35f), Mathf.Clamp01(look.skin.b * 1.35f));
            if (headName == "CultEscapee")
            {
                // The figure faces +z, so its left eye is on the -x side.
                look.eyeLeft = EyeMissing(pawn, "left") ? null : Part("cult_escapee_eye_negative_x");
                look.eyeRight = EyeMissing(pawn, "right") ? null : Part("cult_escapee_eye_positive_x");
            }
        }

        // True if the person's eye on that side ("left" or "right", by the body part's label) is missing.
        private static bool EyeMissing(Pawn pawn, string side)
        {
            if (pawn.health == null || pawn.health.hediffSet == null || pawn.RaceProps == null || pawn.RaceProps.body == null)
            {
                return false;
            }
            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                if (part.def == BodyPartDefOf.Eye && part.customLabel != null && part.customLabel.IndexOf(side, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pawn.health.hediffSet.PartIsMissing(part);
                }
            }
            return false;
        }

        // The mod's body shape ("male", "female", "thin", "hulk", "fat") for one of the game's body types.
        internal static string BuildOf(BodyTypeDef bodyType)
        {
            string bodyName = bodyType != null ? (bodyType.defName ?? "") : "";
            if (Has(bodyName, "Female"))
            {
                return "female";
            }
            if (Has(bodyName, "Thin") || Has(bodyName, "Child"))
            {
                return "thin";
            }
            if (Has(bodyName, "Hulk"))
            {
                return "hulk";
            }
            if (Has(bodyName, "Fat") || Has(bodyName, "Baby"))
            {
                return "fat";
            }
            return "male";
        }

        // A figure's size for a life stage's body size factor (children are smaller, not thinner).
        internal static float SizeFor(float bodySizeFactor)
        {
            return Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(bodySizeFactor));
        }

        internal static float Shoulders(string build)
        {
            return build == "hulk" ? 1.36f : (build == "male" || build == "fat" ? 1f : 0.84f);
        }

        // What a figure of this build wearing these clothes looks like (also the outfit stands, Stands.cs): the
        // body parts (trousers, shirt, coat or armour, leg armour, shoulder pads), the packs, belts and waist tools
        // (WornGear) and the hat, each in the colour of what it shows; bare is the colour of whatever no clothing
        // covers. Clothing with no 3D part goes into unshown if given. Returns the kind of hat worn, or null.
        internal static string Dress(Look look, string build, List<Apparel> worn, Color bare, List<Apparel> unshown)
        {
            // Clothes. Each layer over the body gives its colour to the part it covers; the
            // outermost one wins. A shell layer becomes a coat worn over the rest.
            look.lower = Part("person_" + build + "_lower");
            look.upper = Part("person_" + build + "_upper");
            look.upperColor = bare;
            look.lowerColor = bare;
            int upperLayer = -1;
            int lowerLayer = -1;
            string hatKind = null;
            string coatKind = null;
            string coatFallback = null;
            Under coatUnder = Under.Hidden;
            string legArmourKind = null;
            string vestKind = null;
            string shellArmourKind = null;
            Color vestColour = Color.white;
            Color shellArmourColour = Color.white;
            List<Apparel> gearWorn = null;
            look.mouthWear = null;
            look.eyeWear = null;
            look.neckWear = null;
            if (worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    Apparel a = worn[i];
                    ApparelProperties props = a.def != null ? a.def.apparel : null;
                    if (props == null)
                    {
                        continue;
                    }
                    string name = a.def.defName ?? "";
                    if (WornGear.For(a.def) != null)
                    {
                        (gearWorn ?? (gearWorn = new List<Apparel>())).Add(a);
                        continue;
                    }
                    Color colour = ApparelColour(a);
                    bool torso = Covers(props, "Torso");
                    bool legs = Covers(props, "Legs");
                    bool onHead = Covers(props, "FullHead") || Covers(props, "UpperHead");
                    bool armour = Has(name, "Armor") || Has(name, "Armour") || Has(name, "Flak") || Has(name, "Plate") || Has(name, "Cataphract")
                        || Has(name, "Marine") || Has(name, "Recon") || Has(name, "Phoenix") || Has(name, "Prestige");

                    string faceFile;
                    if (!onHead && !torso && !legs && OwnFaceWear.TryGetValue(name, out faceFile))
                    {
                        // A face, eye or neck item with its own model: its own slot, so it shows with a hat.
                        Model m = Part("person_hat_" + faceFile);
                        if (OnLayer(props, "EyeCover") || Covers(props, "Eyes"))
                        {
                            look.eyeWear = m;
                            look.eyeColor = colour;
                        }
                        else if (Covers(props, "Neck"))
                        {
                            look.neckWear = m;
                            look.neckColor = colour;
                        }
                        else
                        {
                            look.mouthWear = m;
                            look.mouthColor = colour;
                        }
                        continue;
                    }

                    string armourKind = onHead && !torso && !legs ? null : ArmourKind(name);
                    if (armourKind == "flakpants")
                    {
                        legArmourKind = armourKind;
                        look.legArmourColor = colour;
                        continue;
                    }
                    if (armourKind == "flakvest")
                    {
                        vestColour = colour;
                        vestKind = armourKind;
                        continue;
                    }
                    if (armourKind != null && torso)
                    {
                        shellArmourKind = armourKind;
                        shellArmourColour = colour;
                        if (legs && (armourKind == "marine" || armourKind == "cataphract" || armourKind == "recon"))
                        {
                            look.lowerColor = colour;
                            lowerLayer = 3;
                        }
                        continue;
                    }

                    if (onHead && !torso && !legs)
                    {
                        if (OnLayer(props, "Overhead") || hatKind == null)
                        {
                            hatKind = HatKind(name, Covers(props, "FullHead"));
                            look.hatColor = colour;
                        }
                        continue;
                    }

                    int layer = OnLayer(props, "Shell") ? 2 : (OnLayer(props, "Middle") ? 1 : (OnLayer(props, "OnSkin") ? 0 : -1));
                    if (layer < 0)
                    {
                        unshown?.Add(a);   // belts and the like
                        continue;
                    }
                    if (layer == 2 && torso)
                    {
                        bool longCoat = legs || Has(name, "Duster") || Has(name, "Parka") || Has(name, "Robe") || Has(name, "Cape") || Has(name, "Coat");
                        coatFallback = longCoat && !armour ? "coat_long" : "coat_short";
                        CoatShape shape;
                        if (CoatShapes.TryGetValue(name, out shape))
                        {
                            // Its own shape paints its own light and dark zones: the item's colour, white when undyed
                            // (not the sprite's mean, which would grey a painted suit). Its own shoulders: no pads.
                            coatKind = shape.file;
                            coatUnder = shape.under;
                            look.coatColor = a.DrawColor;
                        }
                        else
                        {
                            coatKind = coatFallback;
                            coatUnder = Under.Hidden;
                            look.coatColor = colour;
                            look.pads |= armour;
                        }
                        if (armour && legs && layer > lowerLayer)
                        {
                            look.lowerColor = colour;   // armour that reaches the legs
                            lowerLayer = layer;
                        }
                        continue;
                    }
                    if (torso && layer > upperLayer)
                    {
                        look.upperColor = colour;
                        upperLayer = layer;
                        look.pads |= armour;
                    }
                    if (legs && layer > lowerLayer)
                    {
                        look.lowerColor = colour;
                        lowerLayer = layer;
                    }
                }
            }
            // Armour the mod has a shape for takes the place of a coat (it is worn on the same
            // outer layer); a flak vest shows whole when nothing is worn over it or only a strap, and
            // its front plate in the opening of a coat that is open at the front.
            if (shellArmourKind != null)
            {
                look.coat = Part("person_" + build + "_armor_" + shellArmourKind);
                look.coatColor = shellArmourColour;
                look.pads = false;
            }
            else if (coatKind != null)
            {
                Model overVest = vestKind != null && coatUnder == Under.Whole ? Part("person_" + build + "_" + coatKind + "_overvest") : null;
                look.coat = overVest ?? Part("person_" + build + "_" + coatKind) ?? Part("person_" + build + "_" + coatFallback);
                if (vestKind != null && coatUnder == Under.FrontPlate)
                {
                    // The game draws the vest under the coat, so its plate shows in the open front; only that plate,
                    // seated inside the coat, and with no outline (its edge would draw over the coat).
                    look.underCoat = NoOutline(Part("person_" + build + "_armor_" + vestKind + "_front"));
                    look.underCoatColor = vestColour;
                }
                else if (overVest != null)
                {
                    // A strap over a vest: the whole vest, the strap made to go round it (<shape>_overvest).
                    look.underCoat = Part("person_" + build + "_armor_" + vestKind);
                    look.underCoatColor = vestColour;
                }
            }
            else if (vestKind != null)
            {
                look.coat = Part("person_" + build + "_armor_" + vestKind);
                look.coatColor = vestColour;
                look.pads = false;
            }
            if (legArmourKind != null)
            {
                look.legArmour = Part("person_" + build + "_armor_" + legArmourKind);
            }

            if (hatKind != null)
            {
                look.hat = Part("person_hat_" + hatKind);
                look.visor = hatKind == "fullhelm";
            }
            look.upperWorn = upperLayer >= 0;
            look.lowerWorn = lowerLayer >= 0;

            // Packs, belts and waist tools, fitted to what is worn under them.
            look.gear.Clear();
            look.gearPlace.Clear();
            look.gearTint.Clear();
            look.gearRigid.Clear();
            look.gearAnchor.Clear();
            for (int i = 0; gearWorn != null && i < gearWorn.Count; i++)
            {
                WornGearDef g = WornGear.For(gearWorn[i].def);
                Color tint;
                Model m = WornGear.ModelFor(g, gearWorn[i], out tint);
                if (m == null)
                {
                    unshown?.Add(gearWorn[i]);
                    continue;
                }
                bool rigid;
                Vector3 anchor;
                Matrix4x4 place = WornGear.Place(g, look, out rigid, out anchor);
                look.gear.Add(m);
                look.gearPlace.Add(place);
                look.gearTint.Add(tint);
                look.gearRigid.Add(rigid);
                look.gearAnchor.Add(anchor);
            }
            return hatKind;
        }

        // Outer clothing with its own worn shape (Models/person_<build>_<file>.obj, materials in coats.mtl), and how
        // a flak vest worn under it shows, by vanilla's sprites: in the open front of the duster, lab coat, jacket,
        // royal robe, psyfocus robe and cape (its front plate); whole around the straps (the strap's _overvest shape
        // goes over it); not at all under the parka, robe and burka. The suits and the locust armour take the middle
        // layer, so no vest goes under them. Anything else on the shell layer gets the generic coat_long or coat_short.
        // Children wear the thin shapes (BuildOf), unscaled; the kid parka and kid vacsuit share the adult shapes.
        internal enum Under { Hidden, FrontPlate, Whole }

        internal struct CoatShape
        {
            public string file;
            public Under under;

            public CoatShape(string file, Under under)
            {
                this.file = file;
                this.under = under;
            }
        }

        internal static readonly Dictionary<string, CoatShape> CoatShapes = new Dictionary<string, CoatShape>
        {
            { "Apparel_Duster", new CoatShape("duster", Under.FrontPlate) },
            { "Apparel_LabCoat", new CoatShape("labcoat", Under.FrontPlate) },
            { "Apparel_Jacket", new CoatShape("jacket", Under.FrontPlate) },
            { "Apparel_Parka", new CoatShape("parka", Under.Hidden) },
            { "Apparel_KidParka", new CoatShape("parka", Under.Hidden) },
            { "Apparel_Robe", new CoatShape("robe", Under.Hidden) },
            { "Apparel_RobeRoyal", new CoatShape("robe_royal", Under.FrontPlate) },
            { "Apparel_PsyfocusRobe", new CoatShape("robe_psy", Under.FrontPlate) },
            { "Apparel_Burka", new CoatShape("burka_body", Under.Hidden) },
            { "Apparel_Cape", new CoatShape("cape", Under.FrontPlate) },
            { "Apparel_MechlordSuit", new CoatShape("mechlord", Under.Hidden) },
            { "Apparel_Vacsuit", new CoatShape("vacsuit", Under.Hidden) },
            { "Apparel_VacsuitChildren", new CoatShape("vacsuit", Under.Hidden) },
            { "Apparel_ArmorLocust", new CoatShape("locust", Under.Hidden) },
            { "Apparel_Bandolier", new CoatShape("bandolier", Under.Whole) },
            { "Apparel_BodyStrap", new CoatShape("bodystrap", Under.Whole) },
        };

        // A part drawn with no outline at all (marks every vertex once; the part is shared).
        internal static Model NoOutline(Model m)
        {
            if (m != null && m.noOutline == null)
            {
                bool[] none = new bool[m.positions.Length];
                for (int i = 0; i < none.Length; i++)
                {
                    none[i] = true;
                }
                m.noOutline = none;
            }
            return m;
        }

        internal static bool Covers(ApparelProperties props, string group)
        {
            if (props.bodyPartGroups == null)
            {
                return false;
            }
            for (int i = 0; i < props.bodyPartGroups.Count; i++)
            {
                if (props.bodyPartGroups[i] != null && props.bodyPartGroups[i].defName == group)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool OnLayer(ApparelProperties props, string layer)
        {
            if (props.layers == null)
            {
                return false;
            }
            for (int i = 0; i < props.layers.Count; i++)
            {
                if (props.layers[i] != null && props.layers[i].defName == layer)
                {
                    return true;
                }
            }
            return false;
        }

        // Armour the mod has a worn shape for, by the game's names for it (and the names the
        // DLCs and most mods use for theirs). Null for anything else.
        private static string ArmourKind(string name)
        {
            if (Has(name, "Cataphract"))
            {
                return "cataphract";
            }
            if (Has(name, "PowerArmor") || Has(name, "Marine") || Has(name, "PowerArmour"))
            {
                return "marine";
            }
            if (Has(name, "Recon"))
            {
                return "recon";
            }
            if (Has(name, "PlateArmor") || Has(name, "PlateArmour"))
            {
                return "plate";
            }
            if (Has(name, "FlakJacket"))
            {
                return "flakjacket";
            }
            if (Has(name, "FlakVest"))
            {
                return "flakvest";
            }
            if (Has(name, "FlakPants"))
            {
                return "flakpants";
            }
            return null;
        }

        // The colour of a piece of clothing as the game shows it. Things made from cloth or
        // leather are drawn in that material's colour; things that are not (power armour, flak)
        // have their colours painted into the sprite, so its average colour is used instead.
        internal static Color ApparelColour(Apparel a)
        {
            Color c = a.DrawColor;
            if (a.def.MadeFromStuff)
            {
                return c;
            }
            CompColorable colorable = a.TryGetComp<CompColorable>();
            if (colorable != null && colorable.Active)
            {
                return c;
            }
            return SpriteColors.MeanFor(a);
        }

        // Hats with their own model (Models/person_hat_<file>.obj), by exact defName; one line per hat.
        // Others fall through to the keyword rules below (modded hats, masks, helmets).
        internal static readonly Dictionary<string, string> OwnHats = new Dictionary<string, string>
        {
            { "Apparel_Tuque", "tuque" },
            { "Apparel_Beret", "beret" },
            { "Apparel_AuthorityCap", "authority_cap" },
            { "Apparel_Flophat", "flophat" },
            { "Apparel_HatTop", "top_hat" },
            { "Apparel_EltexSkullcap", "eltex_skullcap" },
            { "Apparel_CowboyHat", "cowboy_hat" },
            { "Apparel_Shadecone", "shadecone" },
            { "Apparel_BowlerHat", "bowler_hat" },
            { "Apparel_HatLadies", "ladies_hat" },
            { "Apparel_Crown", "crown" },
            { "Apparel_Coronet", "coronet" },
            { "Apparel_TortureCrown", "torture_crown" },
            { "Apparel_HatHood", "cloth_hood" },
            { "Apparel_Slicecap", "slicecap" },
            { "Apparel_TribalHeaddress", "tribal_headdress" },
            { "Apparel_Tailcap", "tailcap" },
            { "Apparel_Headwrap", "headwrap" },
            { "Apparel_CrownStellic", "crown_stellic" },
            // Masks, veils, headsets and helmets (apparel step 3, tools/make_masks.py)
            { "Apparel_WarMask", "war_mask" },
            { "Apparel_VisageMask", "visage_mask" },
            { "Apparel_GasMask", "gas_mask" },
            { "Apparel_CultistMask", "cultist_mask" },
            { "Apparel_CeremonialCultistMask", "whisperer_hood" },
            { "Apparel_WarVeil", "war_veil" },
            { "Apparel_Broadwrap", "broadwrap" },
            { "Apparel_AirwireHeadset", "airwire_headset" },
            { "Apparel_ArrayHeadset", "array_headset" },
            { "Apparel_IntegratorHeadset", "integrator_headset" },
            { "Apparel_Gunlink", "gunlink" },
            { "Apparel_PsychicFoilHelmet", "psychic_foil_helmet" },
            { "Apparel_PsyfocusHelmet", "psyfocus_helmet" },
            { "Apparel_KidHelmet", "kid_helmet" },
            { "Apparel_VacsuitHelmet", "vacsuit_helmet" },
            { "Apparel_ArmorHelmetMechCommander", "mechcommander_helmet" },
            { "Apparel_ArmorHelmetMechlordHelmet", "mechlord_helmet" },
        };

        // Face, eye and neck items with their own model (Models/person_hat_<file>.obj), by exact defName. The game lets
        // them be worn with a hat (another layer or other body parts), so each has its own slot instead of the hat's.
        internal static readonly Dictionary<string, string> OwnFaceWear = new Dictionary<string, string>
        {
            { "Apparel_ClothMask", "cloth_mask" },
            { "Apparel_Blindfold", "blindfold" },
            { "Apparel_Collar", "collar" },
        };

        private static string HatKind(string name, bool fullHead)
        {
            string own;
            if (OwnHats.TryGetValue(name, out own))
            {
                return own;
            }
            if (name == "Apparel_SimpleHelmet")
            {
                return "simple_helmet";   // its own model; other helmets keep the generic one
            }
            if (Has(name, "Helmet") || Has(name, "Helm"))
            {
                if (Has(name, "Cataphract"))
                {
                    return "cataphract_helm";
                }
                if (Has(name, "PowerArmor") || Has(name, "Marine"))
                {
                    return "marine_helm";
                }
                if (Has(name, "Recon"))
                {
                    return "recon_helm";
                }
                if (Has(name, "Advanced"))
                {
                    return "combat";
                }
                bool sealedHelmet = fullHead && (Has(name, "Power") || Has(name, "Recon") || Has(name, "Marine") || Has(name, "Cataphract")
                    || Has(name, "Armor") || Has(name, "Phoenix") || Has(name, "Gunlink") || Has(name, "Prestige"));
                return sealedHelmet ? "fullhelm" : "helmet";
            }
            if (Has(name, "Cowboy") || Has(name, "Shadecone") || Has(name, "Brim") || Has(name, "Sun"))
            {
                return "brim";
            }
            if (Has(name, "Bowler") || Has(name, "TopHat") || Has(name, "HatTop") || Has(name, "Ladies") || Has(name, "Crown") || Has(name, "Coronet") || Has(name, "Stellic"))
            {
                return "tall";
            }
            if (Has(name, "Hood") || Has(name, "Veil") || Has(name, "Burka") || Has(name, "Wrap") || Has(name, "Slicecap") || Has(name, "Collar"))
            {
                return "hood";
            }
            if (Has(name, "Mask") || Has(name, "Visage"))
            {
                return fullHead ? "helmet" : "cap";
            }
            return "cap";
        }
    }
}
