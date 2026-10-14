using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Mechanoids drawn as 3D models in place of their sprites (ModelDef mech = true).
    //
    // A mech is a body that does not move plus the pieces that do (ModelDef.mechParts), all built
    // in one space and turned about their own pivots. Everything is read from the game's own state,
    // so the model does what the sprite would: it turns to face the way it walks (and the game's
    // facing when it stands), its wheels roll by the ground covered and its legs step, and both stop
    // when it stops or the game is paused. Its faction marks take the faction's mech colour, its gun
    // shows when the game shows it, pointing where the game points it, a turret follows its comp,
    // a hauled thing is held in front of it, it rises with a jump, and downed or dead it lies on its
    // side. While it works, the parts made for the work (brushes, cutter discs, sensor heads, digging
    // arms) move. A mech the game draws with its other look (the Ancient sprites of mechs over 100
    // years old, which includes almost every raider) is drawn worn: darker, rusty, marks kept.
    public static class Mechs
    {
        private enum Motion { Fixed, Wheel, Leg, Spin, Pitch, Arm, Upper, Head, Weapon, Turret }

        private class Piece
        {
            public MechPart def;
            public Motion motion;
            public Model model;
            public Model marked;      // with the faction marks tinted, or null
            public int parent = -1;
            public Model[] worn;      // weathered copies (Ancient), by variant; made when first needed
            public Model[] wornMarked;
        }

        private class Rig
        {
            public Model body;
            public Model bodyMarked;
            public Color markBase = Color.white;
            public readonly List<Piece> pieces = new List<Piece>();
            public float minX, maxX, minZ, maxZ, height;   // the whole mech, guns left out
            public bool legs;
            public bool weaponPart;
            public Model[] bodyWorn;
            public Model[] bodyWornMarked;
        }

        // Ancient mechs (the game's other sprite of the kind, over 100 years old) are drawn worn: darker, with rust
        // patches, in one of a few patterns picked by the mech's id (so each keeps its own).
        private const int WornVariants = 3;

        private static Vector3 WornOffset(int v)
        {
            return new Vector3(v * 17.3f, v * 3.1f, v * 9.7f);
        }

        // The model to draw for one piece (body: piece null): plain or worn, with or without faction marks.
        private static Model Pick(Rig rig, ModelDef def, Piece p, int worn, bool marked)
        {
            Model plain = p != null ? p.model : rig.body;
            if (worn < 0)
            {
                Model m = p != null ? p.marked : rig.bodyMarked;
                return marked && m != null ? m : plain;
            }
            if (p == null && rig.bodyWorn == null)
            {
                rig.bodyWorn = new Model[WornVariants];
                rig.bodyWornMarked = new Model[WornVariants];
            }
            if (p != null && p.worn == null)
            {
                p.worn = new Model[WornVariants];
                p.wornMarked = new Model[WornVariants];
            }
            Model[] w = p != null ? p.worn : rig.bodyWorn;
            Model[] wm = p != null ? p.wornMarked : rig.bodyWornMarked;
            if (w[worn] == null)
            {
                w[worn] = ObjLoader.Weathered(plain, WornOffset(worn), def.factionMaterials);
                Color unused;
                wm[worn] = ObjLoader.Marked(w[worn], def.factionMaterials, out unused);
            }
            return marked && wm[worn] != null ? wm[worn] : w[worn];
        }

        // Half the game's allegiance overlay: it lays the colour over the sprite's mask at half strength.
        private const float FactionMix = 0.5f;

        private static readonly Dictionary<ModelDef, Rig> rigs = new Dictionary<ModelDef, Rig>();

        private static readonly HashSet<string> WorkJobs = new HashSet<string>
        {
            "CutPlant", "CutPlantDesignated", "Harvest", "HarvestDesignated", "Sow", "Mine", "FinishFrame", "Repair", "BuildRoof",
            "RemoveRoof", "Deconstruct", "Uninstall", "SmoothFloor", "SmoothWall", "RemoveFloor", "FixBrokenDownBuilding",
            "PlaceNoCostFrame", "Clean", "DoBill"
        };

        // True if the pawn is a mech with a model that will be drawn as it (the view and setting on).
        public static bool HasModel(Pawn pawn)
        {
            ModelEntry entry;
            return pawn != null && pawn.def != null && ModelLibrary.TryGet(pawn.def, out entry) && entry.def.mech && entry.HasModel
                && !People.Hidden(pawn);
        }

        // Draws the mech at drawLoc. Returns false if it has no model, in which
        // case the game's sprite must be drawn as usual.
        public static bool Draw(Pawn pawn, Vector3 drawLoc)
        {
            ModelEntry entry;
            if (pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !entry.def.mech || !entry.HasModel
                || People.Hidden(pawn))
            {
                return false;
            }
            Rig rig = RigFor(entry);
            if (rig == null || rig.body == null)
            {
                return false;
            }
            ModelDef def = entry.def;
            IsoRimSettings st = IsoRimMod.Settings;

            float size = def.scale * Animals.SizeOf(pawn);
            bool dead = pawn.Dead;
            bool lying = dead || pawn.GetPosture() != PawnPosture.Standing;
            bool onSide = dead || pawn.Downed;
            float lift = Carried.LiftOf(pawn, ref drawLoc);

            float yaw;
            float phase = 0f;
            float gait = 0f;
            float travel = 0f;
            if (dead)
            {
                // Each wreck lies at its own slight angle.
                yaw = pawn.Rotation.AsAngle + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 50f;
            }
            else
            {
                Animals.Track(pawn, drawLoc, lying, def.stride * size, out yaw, out phase, out gait, out travel);
            }

            // At work: face the work, and move the parts made for it (timed by game ticks, so they stop when paused).
            float work = -1f;
            IntVec3 target;
            if (!lying && st.workAnimations && Working(pawn, out target))
            {
                Vector3 to = target.ToVector3Shifted() - drawLoc;
                if (to.x * to.x + to.z * to.z > 0.04f)
                {
                    yaw = to.AngleFlat();
                }
                work = (Find.TickManager.TicksGame + (pawn.thingIDNumber % 97)) / 60f;
            }

            // The gun, as the game shows it (PawnRenderUtility.DrawEquipmentAndApparelExtras).
            bool showGun = false;
            bool aiming = false;
            float aim = yaw;
            if (!lying && st.heldWeapons3D)
            {
                GunState(pawn, ref aim, out showGun, out aiming);
            }
            CompTurretGun turret = lying ? null : pawn.TryGetComp<CompTurretGun>();
            float turretAim = turret != null ? turret.curRotation - turret.Props.angleOffset : yaw;
            if (turret != null && turret.curRotation == 0f && pawn.Rotation == Rot4.West)
            {
                turretAim += 180f;   // never aimed yet: the game's sprite is turned round for a mech facing west
            }

            // How the whole mech is posed, before it is turned to face its heading.
            Matrix4x4 sized = Matrix4x4.Scale(new Vector3(size, size, size));
            Matrix4x4 pose;
            if (onSide)
            {
                // Rolled over onto its left side, which then rests on the ground.
                float centre = rig.height * 0.5f;
                pose = Matrix4x4.Translate(new Vector3(0f, -rig.minX * size, 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.forward))
                    * Matrix4x4.Translate(new Vector3(0f, -centre * size, 0f));
                gait = 0f;
            }
            else if (rig.legs)
            {
                // A slight rise and fall with each step.
                float bob = Mathf.Abs(Mathf.Sin(phase)) * 0.025f * rig.height * size * gait;
                pose = Matrix4x4.Translate(new Vector3(0f, bob, 0f));
            }
            else
            {
                pose = Matrix4x4.identity;
            }
            Matrix4x4 whole = pose * sized;
            Vector3 at = new Vector3(drawLoc.x, lift, drawLoc.z);

            Faction faction = pawn.Faction;
            Color mark = faction != null ? Color.Lerp(rig.markBase, faction.AllegianceColor, FactionMix) : rig.markBase;
            bool marked = faction != null;

            int worn = OtherLook(pawn) ? (pawn.thingIDNumber & 0x7FFFFFFF) % WornVariants : -1;
            DynamicModels.DrawLocal(Pick(rig, def, null, worn, marked), at, yaw, whole, mark, null);

            // Each moving piece, placed in the body's space (after its parent's movement).
            Matrix4x4[] places = new Matrix4x4[rig.pieces.Count];
            bool gunDrawn = false;
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                Quaternion turn = Quaternion.identity;
                switch (p.motion)
                {
                    case Motion.Wheel:
                        turn = Quaternion.AngleAxis(travel / Mathf.Max(0.05f, p.def.radius * size) * Mathf.Rad2Deg, Vector3.right);
                        break;
                    case Motion.Leg:
                        if (gait > 0.01f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Sin(phase + p.def.beat * 2f * Mathf.PI) * p.def.swing * Mathf.Clamp01(gait), Vector3.right);
                        }
                        break;
                    case Motion.Spin:
                        if (work >= 0f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Repeat(work * p.def.speed, 360f), Vector3.up);
                        }
                        break;
                    case Motion.Pitch:
                        if (work >= 0f)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Sin(work * 2f * Mathf.PI / 1.4f) * p.def.swing, Vector3.right);
                        }
                        break;
                    case Motion.Arm:
                        if (work >= 0f)
                        {
                            // Down onto the work and back up, a stroke a second and a half.
                            float k = 0.5f - 0.5f * Mathf.Cos(work * 2f * Mathf.PI / 1.5f);
                            turn = Quaternion.AngleAxis(k * p.def.swing, Vector3.right);
                        }
                        break;
                    case Motion.Upper:
                        if (aiming)
                        {
                            turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, aim), Vector3.up);
                        }
                        break;
                    case Motion.Head:
                        if (aiming)
                        {
                            turn = Quaternion.AngleAxis(Mathf.Clamp(Mathf.DeltaAngle(yaw, aim), -p.def.maxTurn, p.def.maxTurn), Vector3.up);
                        }
                        break;
                    case Motion.Weapon:
                        // On an upper body the upper body turns it; otherwise it turns by itself.
                        if (aiming && (p.parent < 0 || rig.pieces[p.parent].motion != Motion.Upper))
                        {
                            turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, aim), Vector3.up);
                        }
                        break;
                    case Motion.Turret:
                        turn = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, turretAim), Vector3.up);
                        break;
                }
                Matrix4x4 own = turn == Quaternion.identity ? Matrix4x4.identity
                    : Matrix4x4.Translate(p.def.pivot) * Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-p.def.pivot);
                places[i] = (p.parent >= 0 ? places[p.parent] : Matrix4x4.identity) * own;

                if (p.motion == Motion.Weapon)
                {
                    if (!showGun)
                    {
                        continue;
                    }
                    gunDrawn = true;
                    Thing gun = pawn.equipment.Primary;
                    if (!p.def.weapon.NullOrEmpty() && gun.def.defName != p.def.weapon)
                    {
                        // Some other gun: drawn as that gun's own 3D form, at the mount.
                        Vector3 mount = WorldPoint(at, yaw, whole * places[i], p.def.pivot);
                        Patches.HeldWeapon3D(gun, mount, aim);
                        continue;
                    }
                }
                else if (p.motion == Motion.Turret && (turret == null || turret.TurretDestroyed))
                {
                    continue;
                }
                DynamicModels.DrawLocal(Pick(rig, def, p, worn, marked), at, yaw, whole * places[i], mark, null);
            }
            if (showGun && !gunDrawn)
            {
                // A gun the model has no mount for: held in front, half way up.
                Vector3 front = WorldPoint(at, yaw, whole, new Vector3(0f, rig.height * 0.5f, rig.maxZ));
                Patches.HeldWeapon3D(pawn.equipment.Primary, front, aim);
            }

            if (lift <= 0f)
            {
                float halfX = Mathf.Max(-rig.minX, rig.maxX) * size;
                float halfZ = Mathf.Max(-rig.minZ, rig.maxZ) * size;
                if (onSide)
                {
                    GroundShadows.Blob(at, yaw, rig.height * 0.5f * size * 1.2f, halfZ * 1.15f);
                }
                else
                {
                    GroundShadows.Blob(at, yaw, halfX * 1.2f, halfZ * 1.15f);
                }
            }

            if (!lying && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                Vector3 hold = def.carryAt.y >= 0f ? def.carryAt : new Vector3(0f, rig.height * 0.45f, rig.maxZ + 0.08f);
                Carried.Draw(pawn, WorldPoint(at, yaw, whole, hold), yaw);
            }
            return true;
        }

        // A point of the model (its own space, facing north) on the map: x and z in cells, y the height above the ground.
        private static Vector3 WorldPoint(Vector3 at, float yaw, Matrix4x4 local, Vector3 p)
        {
            Vector3 v = Quaternion.AngleAxis(yaw, Vector3.up) * local.MultiplyPoint3x4(p);
            return new Vector3(at.x + v.x, at.y + v.y, at.z + v.z);
        }

        // Whether the game shows the mech's gun now and where it points (as PawnRenderUtility.DrawEquipmentAndApparelExtras).
        private static void GunState(Pawn pawn, ref float aim, out bool show, out bool aiming)
        {
            show = false;
            aiming = false;
            if (pawn.equipment == null || pawn.equipment.Primary == null)
            {
                return;
            }
            Job job = pawn.CurJob;
            if (job == null || job.def == null || job.def.neverShowWeapon)
            {
                return;
            }
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy != null && !busy.neverAimWeapon && busy.focusTarg.IsValid)
            {
                Vector3 to = (busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted()) - pawn.DrawPos;
                if (to.x * to.x + to.z * to.z > 0.001f)
                {
                    aim = to.AngleFlat();
                }
                Verb verb = pawn.CurrentEffectiveVerb;
                if (verb != null && verb.AimAngleOverride.HasValue)
                {
                    aim = verb.AimAngleOverride.Value;
                }
                show = true;
                aiming = true;
            }
            else if (PawnRenderUtility.CarryWeaponOpenly(pawn))
            {
                show = true;
            }
        }

        // True while the mech is at its work (a job with work parts, at the target, not walking).
        private static bool Working(Pawn pawn, out IntVec3 target)
        {
            target = IntVec3.Invalid;
            Job job = pawn.CurJob;
            if (job == null || job.def == null || pawn.Drafted || (pawn.pather != null && pawn.pather.Moving) || !WorkJobs.Contains(job.def.defName))
            {
                return false;
            }
            LocalTargetInfo a = job.targetA;
            if (!a.IsValid)
            {
                return false;
            }
            target = a.Cell;
            return target.IsValid && pawn.Position.DistanceToSquared(target) <= 8;
        }

        // True for a mech the game draws with a sprite other than its kind's first one (the Ancient look).
        public static bool OtherLook(Pawn pawn)
        {
            List<PawnKindLifeStage> stages = pawn.kindDef != null ? pawn.kindDef.lifeStages : null;
            if (stages == null || stages.Count < 2 || pawn.ageTracker == null)
            {
                return false;
            }
            PawnKindLifeStage cur = pawn.ageTracker.CurKindLifeStage;
            if (cur == null || cur == stages[0] || cur.bodyGraphicData == null || stages[0].bodyGraphicData == null)
            {
                return false;
            }
            return cur.bodyGraphicData.texPath != stages[0].bodyGraphicData.texPath;
        }

        private static Motion MotionOf(string s)
        {
            switch ((s ?? "fixed").ToLowerInvariant())
            {
                case "wheel": return Motion.Wheel;
                case "leg": return Motion.Leg;
                case "spin": return Motion.Spin;
                case "pitch": return Motion.Pitch;
                case "arm": return Motion.Arm;
                case "upper": return Motion.Upper;
                case "head": return Motion.Head;
                case "weapon": return Motion.Weapon;
                case "turret": return Motion.Turret;
                default: return Motion.Fixed;
            }
        }

        private static Rig RigFor(ModelEntry entry)
        {
            ModelDef def = entry.def;
            Rig rig;
            if (rigs.TryGetValue(def, out rig))
            {
                return rig;
            }
            rig = null;
            try
            {
                rig = Build(entry);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Putting a mechanoid's model together failed (" + def.defName + "): " + e, 93611401);
            }
            rigs[def] = rig;
            return rig;
        }

        private static Rig Build(ModelEntry entry)
        {
            ModelDef def = entry.def;
            Rig rig = new Rig();
            string root = def.modContentPack != null ? def.modContentPack.RootDir : IsoRimMod.RootDir;
            List<Model> still = new List<Model> { entry.variants[0] };
            Dictionary<string, int> byName = new Dictionary<string, int>();
            List<string> parents = new List<string>();
            if (def.mechParts != null)
            {
                for (int i = 0; i < def.mechParts.Count; i++)
                {
                    MechPart part = def.mechParts[i];
                    Model m = part == null || part.model.NullOrEmpty() ? null : ModelLibrary.LoadFile(Path.Combine(root, part.model));
                    if (m == null)
                    {
                        continue;
                    }
                    Motion motion = MotionOf(part.motion);
                    if (motion == Motion.Fixed && part.parent.NullOrEmpty())
                    {
                        still.Add(m);
                        continue;
                    }
                    Piece p = new Piece { def = part, motion = motion, model = m };
                    byName[part.name.NullOrEmpty() ? Path.GetFileNameWithoutExtension(part.model) : part.name] = rig.pieces.Count;
                    rig.pieces.Add(p);
                    parents.Add(part.parent);
                    rig.legs |= motion == Motion.Leg;
                    rig.weaponPart |= motion == Motion.Weapon;
                }
            }
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                int pi;
                if (!parents[i].NullOrEmpty() && byName.TryGetValue(parents[i], out pi))
                {
                    if (pi < i)
                    {
                        rig.pieces[i].parent = pi;
                    }
                    else
                    {
                        ModLog.Warn("[IsoRim] " + def.defName + ": mech part " + rig.pieces[i].def.model + " must come after its parent " + parents[i]);
                    }
                }
            }
            rig.body = still.Count > 1 ? ObjLoader.Merge(still) : still[0];

            Color c;
            rig.bodyMarked = ObjLoader.Marked(rig.body, def.factionMaterials, out c);
            bool haveBase = rig.bodyMarked != null;
            if (haveBase)
            {
                rig.markBase = c;
            }
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                p.marked = ObjLoader.Marked(p.model, def.factionMaterials, out c);
                if (p.marked != null && !haveBase)
                {
                    rig.markBase = c;
                    haveBase = true;
                }
            }

            // The size of the whole mech, guns left out.
            rig.minX = rig.body.minX;
            rig.maxX = rig.body.maxX;
            rig.minZ = rig.body.minZ;
            rig.maxZ = rig.body.maxZ;
            rig.height = rig.body.maxY;
            for (int i = 0; i < rig.pieces.Count; i++)
            {
                Piece p = rig.pieces[i];
                if (p.motion == Motion.Weapon || p.motion == Motion.Turret)
                {
                    continue;
                }
                rig.minX = Mathf.Min(rig.minX, p.model.minX);
                rig.maxX = Mathf.Max(rig.maxX, p.model.maxX);
                rig.minZ = Mathf.Min(rig.minZ, p.model.minZ);
                rig.maxZ = Mathf.Max(rig.maxZ, p.model.maxZ);
                rig.height = Mathf.Max(rig.height, p.model.maxY);
            }
            return rig;
        }

        // For the harness (probe mechcheck): how the mech is drawn now.
        public static string Report(Pawn pawn)
        {
            ModelEntry entry;
            bool bound = pawn.def != null && ModelLibrary.TryGet(pawn.def, out entry) && entry.def.mech && entry.HasModel;
            string s = "bound " + bound + ", ancient " + OtherLook(pawn) + ", model " + (bound && HasModel(pawn) && Patches.DrawnAsModel(pawn));
            s += ", posture " + pawn.GetPosture() + (pawn.Downed ? " downed" : "") + (pawn.Dead ? " dead" : "") + ", faction " + (pawn.Faction != null ? pawn.Faction.def.defName : "none");
            if (pawn.ageTracker != null)
            {
                s += ", age " + pawn.ageTracker.AgeBiologicalYears;
            }
            if (pawn.Spawned && !pawn.Dead)
            {
                float aim = 0f;
                bool show, aiming;
                GunState(pawn, ref aim, out show, out aiming);
                IntVec3 t;
                s += ", gun " + (pawn.equipment != null && pawn.equipment.Primary != null ? pawn.equipment.Primary.def.defName : "none")
                    + (show ? (aiming ? " aiming " + aim.ToString("0") : " shown") : " hidden")
                    + ", job " + (pawn.CurJob != null ? pawn.CurJob.def.defName : "none") + (Working(pawn, out t) ? " working" : "")
                    + ", moving " + (pawn.pather != null && pawn.pather.Moving)
                    + ", carrying " + (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null ? pawn.carryTracker.CarriedThing.LabelCap.ToString() : "nothing");
                CompTurretGun tg = pawn.TryGetComp<CompTurretGun>();
                if (tg != null)
                {
                    s += ", turret " + (tg.TurretDestroyed ? "destroyed" : "rot " + tg.curRotation.ToString("0"))
                        + " target " + HarmonyLib.Traverse.Create(tg).Field("currentTarget").GetValue<LocalTargetInfo>().ToString()
                        + " canShoot " + HarmonyLib.Traverse.Create(tg).Property("CanShoot").GetValue<bool>() + " fireAtWill " + HarmonyLib.Traverse.Create(tg).Field("fireAtWill").GetValue<bool>() + " cooldown " + HarmonyLib.Traverse.Create(tg).Field("burstCooldownTicksLeft").GetValue<int>();
                    CompCanBeDormant dorm = pawn.TryGetComp<CompCanBeDormant>();
                    s += ", awake " + (dorm == null || dorm.Awake);
                }
            }
            return s;
        }
    }
}
