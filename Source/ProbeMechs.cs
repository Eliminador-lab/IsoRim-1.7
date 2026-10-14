using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only probe keys for mechanoids and carried things (Mechs.cs, Carried.cs). Cells are map cells.
    //   clearzone=x0,z0,x1,z1         destroys the pawns (not colonists), corpses, items, filth and plants in the rectangle
    //   mech=x,z,<PawnKindDef>,<player|enemy>,<age>,<rot 0-3>[,<state>]  a new mech of that kind, faction and age facing rot
    //                                 (no overseer: the jobs below are started directly, as forced jobs); state: raid (an enemy in an assault lord), draft, down (damaged until downed), dead (killed), shutdown (self-shutdown job)
    //   attack=x0,z0,x1,z1            the pawn in the first cell given the job to fire at the thing (or cell) in the second
    //   haul=x,z,<ThingDef>,<count>,ix,iz  a new stack at (ix,iz) and the pawn in (x,z) given the job to haul it to storage
    //                                 (a real job; a stockpile must exist: zone=)
    //   order=x,z,<work>,tx,tz        the pawn in (x,z) given a real work job on (tx,tz): cut (a plant), mine, deconstruct,
    //                                 clean (filth made there first); the designation is added as the player would
    //   goto=x,z,tx,tz                the pawn in (x,z) given the job to walk to (tx,tz)
    //   jump=x,z,tx,tz                the pawn in (x,z) casts its jump ability (LongjumpMech*) at (tx,tz)
    //   walkfilm=N,k,tag,x,z,size[,follow|ticksM] N screenshots, k Unity frames apart (and each written before the next), the game
    //                                 running at normal speed meanwhile (renders\<harness tag>\walk_<tag>_<frame>.png), camera held at (x,z,size);
    //                                 with a 7th part "follow", centred on the pawns walking on a forced job instead
    //   turrettarget=x,z,tx,tz        the turret comp of the pawn in (x,z) given the pawn (or cell) at (tx,tz) as its target
    //   mechcheck=1                   logs every mech and carrying pawn on the map (Mechs.Report)
    public static partial class Probe
    {
        private static Pawn PawnAt(Map map, int x, int z)
        {
            IntVec3 c = new IntVec3(x, 0, z);
            Pawn p = c.InBounds(map) ? c.GetThingList(map).OfType<Pawn>().FirstOrDefault() : null;
            if (p == null)
            {
                Log.Warning("[IsoRim probe] no pawn at " + c);
            }
            return p;
        }

        private static bool MechKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "clearzone":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    List<Thing> gone = new List<Thing>();
                    foreach (IntVec3 c in r)
                    {
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map))
                        {
                            Pawn p = t as Pawn;
                            if ((p != null && !p.IsColonist) || t is Corpse || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth
                                || t.def.category == ThingCategory.Plant)
                            {
                                gone.Add(t);
                            }
                        }
                    }
                    foreach (Thing t in gone)
                    {
                        if (!t.Destroyed)
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                    return true;
                }
                case "mech":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Faction f = a[3] == "enemy" ? Find.FactionManager.OfMechanoids : Faction.OfPlayer;
                    float age = F(a[4]);
                    Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer,
                        fixedBiologicalAge: age, fixedChronologicalAge: age));
                    Rot4 rot = new Rot4(I(a[5]));
                    GenSpawn.Spawn(p, new IntVec3(I(a[0]), 0, I(a[1])), map, rot);
                    p.jobs?.StopAll();
                    p.pather?.StopDead();
                    p.Rotation = rot;
                    string state = a.Length > 6 ? a[6] : "";
                    if (state == "draft" && p.drafter != null)
                    {
                        p.drafter.Drafted = true;
                    }
                    else if (state == "down")
                    {
                        HealthUtility.DamageUntilDowned(p, false);
                    }
                    else if (state == "dead")
                    {
                        p.Kill(null);
                    }
                    else if (state == "raid")
                    {
                        // An enemy that attacks the colony, as in a raid (a threat the colony's turrets go for).
                        Verse.AI.Group.LordMaker.MakeNewLord(p.Faction, new LordJob_AssaultColony(p.Faction), map, new List<Pawn> { p });
                    }
                    else if (state == "shutdown")
                    {
                        p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.SelfShutdown, p.Position), JobCondition.InterruptForced);
                    }
                    Log.Message("[IsoRim probe] mech " + kind.defName + " at " + p.PositionHeld + " (" + state + "): " + Mechs.Report(p));
                    return true;
                }
                case "attack":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Thing target = tc.GetFirstBuilding(map) ?? (Thing)tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                    Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target != null ? new LocalTargetInfo(target) : new LocalTargetInfo(tc));
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    bool ok = p.CurJob == job;
                    Log.Message("[IsoRim probe] attack: " + p.LabelShort + " fires at " + (target != null ? target.def.defName : tc.ToString()) + ": " + ok);
                    return true;
                }
                case "haul":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    Thing item = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(a[2]));
                    item.stackCount = Mathf.Clamp(I(a[3]), 1, item.def.stackLimit);
                    GenSpawn.Spawn(item, new IntVec3(I(a[4]), 0, I(a[5])), map);
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job = HaulAIUtility.HaulToStorageJob(p, item, false);
                    if (job == null)
                    {
                        Log.Warning("[IsoRim probe] haul: no storage for " + item.def.defName);
                        return true;
                    }
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] haul: " + p.LabelShort + " at " + p.Position + " hauls " + item.LabelCap + " from " + item.Position + " (" + job.def.defName + ")");
                    return true;
                }
                case "order":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[3]), 0, I(a[4]));
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job = null;
                    switch (a[2])
                    {
                        case "cut":
                        {
                            Plant plant = tc.GetPlant(map);
                            if (plant != null)
                            {
                                map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                                job = JobMaker.MakeJob(JobDefOf.CutPlantDesignated, plant);
                            }
                            break;
                        }
                        case "mine":
                        {
                            Thing rock = tc.GetEdifice(map);
                            if (rock != null)
                            {
                                map.designationManager.AddDesignation(new Designation(tc, DesignationDefOf.Mine));
                                job = JobMaker.MakeJob(JobDefOf.Mine, rock);
                            }
                            break;
                        }
                        case "deconstruct":
                        {
                            Thing b = tc.GetEdifice(map);
                            if (b != null)
                            {
                                map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                                job = JobMaker.MakeJob(JobDefOf.Deconstruct, b);
                            }
                            break;
                        }
                        case "clean":
                        {
                            Filth filth = (Filth)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt), tc, map);
                            filth.thickness = filth.def.filth.maxThickness;
                            map.areaManager.Home[tc] = true;
                            job = JobMaker.MakeJob(JobDefOf.Clean);
                            job.AddQueuedTarget(TargetIndex.A, filth);
                            break;
                        }
                    }
                    if (job != null)
                    {
                        job.playerForced = true;
                        p.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    bool ok = job != null && p.CurJob == job;
                    Log.Message("[IsoRim probe] order " + a[2] + ": " + p.LabelShort + " at " + p.Position + " -> " + tc + ": " + ok);
                    return true;
                }
                case "goto":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    Job gj = JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(I(a[2]), 0, I(a[3])));
                    gj.playerForced = true;
                    p.jobs.StartJob(gj, JobCondition.InterruptForced);
                    bool ok = p.CurJob == gj;
                    Log.Message("[IsoRim probe] goto: " + p.LabelShort + " at " + p.Position + ": " + ok);
                    return true;
                }
                case "jump":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null || p.abilities == null)
                    {
                        return true;
                    }
                    Ability jump = p.abilities.AllAbilitiesForReading.FirstOrDefault(x => x.def.defName.StartsWith("Longjump"));
                    if (jump == null)
                    {
                        Log.Warning("[IsoRim probe] jump: " + p.LabelShort + " has no jump ability");
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Job jj = jump.GetJob(new LocalTargetInfo(tc), new LocalTargetInfo(tc));
                    jj.playerForced = true;
                    p.jobs.StartJob(jj, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] jump: " + p.LabelShort + " " + jump.def.defName + " -> " + tc + ", job " + (p.CurJob != null ? p.CurJob.def.defName : "none"));
                    return true;
                }
                case "turrettarget":
                {
                    // The CompTurretGun of the pawn in (x,z) given the thing (or cell) at (tx,tz) as its target, as its own
                    // target search would; the game's tick then turns and fires it (the search itself did not pick our
                    // harness raiders as threats).
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    CompTurretGun tg = p != null ? p.TryGetComp<CompTurretGun>() : null;
                    if (tg == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Thing tt = tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                    HarmonyLib.Traverse.Create(tg).Field("currentTarget").SetValue(tt != null ? new LocalTargetInfo(tt) : new LocalTargetInfo(tc));
                    HarmonyLib.Traverse.Create(tg).Field("burstWarmupTicksLeft").SetValue(60);
                    Log.Message("[IsoRim probe] turrettarget: " + p.LabelShort + " -> " + (tt != null ? tt.LabelShort : tc.ToString()));
                    return true;
                }
                case "walkfilm":
                    walkLeft = I(a[0]);
                    walkTicks = Mathf.Clamp(I(a[1]), 2, 60);
                    walkTag = a[2];
                    walkCam = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                    walkFrame = 0;
                    walkStep = 0;
                    walkPending = null;
                    walkFollow = a.Length > 6 && a[6] == "follow";
                    walkExtraTicks = a.Length > 6 && a[6].StartsWith("ticks") ? I(a[6].Substring(5)) : 0;
                    walkTrack = a.Length > 7 && a[7] == "track";
                    return true;
                case "mechcheck":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if ((p.RaceProps != null && p.RaceProps.IsMechanoid) || (p.carryTracker != null && p.carryTracker.CarriedThing != null))
                        {
                            Log.Message("[IsoRim probe] mechcheck " + p.def.defName + " " + p.LabelShort + " at " + p.Position + " rot " + p.Rotation + ": "
                                + (p.RaceProps.IsMechanoid ? Mechs.Report(p) : "carrying " + p.carryTracker.CarriedThing.LabelCap));
                        }
                    }
                    foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
                    {
                        if (c.InnerPawn != null && c.InnerPawn.RaceProps.IsMechanoid)
                        {
                            Log.Message("[IsoRim probe] mechcheck corpse " + c.InnerPawn.def.defName + " at " + c.Position + ": " + Mechs.Report(c.InnerPawn));
                        }
                    }
                    return true;
            }
            return false;
        }

        private static int walkLeft;
        private static int walkTicks;
        private static int walkFrame;
        private static string walkTag;
        private static Vector3 walkCam;
        private static bool walkFollow;
        private static bool walkTrack;       // 8th part "track": centred on the first entity bound to a model
        private static int walkExtraTicks;   // 7th part "ticks<N>": N more game ticks run before each film frame (slow states)

        // One film frame every few Unity frames: the game ticks and the camera is set, the picture is taken two
        // frames later, and the next frame waits until that picture has been written (the capture is not instant,
        // and a capture asked for while one is pending repeats the old picture).
        private static int walkStep;
        private static string walkPending;

        private static void WalkFilmTick()
        {
            if (walkLeft <= 0)
            {
                return;
            }
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            if (walkPending != null)
            {
                if (!System.IO.File.Exists(walkPending))
                {
                    return;
                }
                walkPending = null;
                walkLeft--;
                walkStep = 0;
                if (walkLeft <= 0)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                }
                return;
            }
            if (walkStep == 0)
            {
                // The game runs at normal speed through the film (paused, the game does not move pawns' drawn
                // positions on however many ticks are run).
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                for (int i = 0; i < walkExtraTicks; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                Vector3 cam = new Vector3(walkCam.x, 0f, walkCam.y);
                if (walkFollow && Find.CurrentMap != null)
                {
                    // The middle of the pawns walking on a forced job (goto=, haul=).
                    Vector3 sum = Vector3.zero;
                    int n = 0;
                    foreach (Pawn p in Find.CurrentMap.mapPawns.AllPawnsSpawned)
                    {
                        if (p.CurJob != null && p.CurJob.playerForced && p.pather != null && p.pather.Moving)
                        {
                            sum += p.DrawPos;
                            n++;
                        }
                    }
                    if (n > 0)
                    {
                        cam = new Vector3(sum.x / n, 0f, sum.z / n);
                    }
                }
                if (walkTrack && Find.CurrentMap != null)
                {
                    Pawn e = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(Bound);
                    if (e != null)
                    {
                        cam = new Vector3(e.DrawPos.x, 0f, e.DrawPos.z);
                    }
                }
                Find.CameraDriver.SetRootPosAndSize(cam, walkCam.z);
                walkStep = 1;
                return;
            }
            if (walkStep < walkTicks)
            {
                walkStep++;
                return;
            }
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            walkPending = System.IO.Path.Combine(dir, "walk_" + walkTag + "_" + walkFrame.ToString("000") + ".png");
            ScreenCapture.CaptureScreenshot(walkPending);
            Log.Message("[IsoRim probe] walkfilm " + walkTag + " frame " + walkFrame + " (Unity frame " + Time.frameCount + ", tick " + Find.TickManager.TicksGame + ")" + EntityFilmNote());
            walkFrame++;
        }
    }
}
