using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only probe keys for Anomaly entities, mutants and creepjoiners (Mechs.cs entity path, People.cs).
    //   entity=x,z,<PawnKindDef>,<rot 0-3>[,opt...]   a new entity (faction Entities) facing rot; opts:
    //                                 alt:<n>       the n-th of the kind's alternate graphics (-1: its own), by generating
    //                                               until the game gives one that look
    //                                 activity:<f>  CompActivity.SetActivity (fleshmass nucleus stages)
    //                                 open:<f>      CompNociosphere.segScale (the shells; goal set too)
    //                                 visible       revealed (an invisible entity's HediffComp_Invisibility.BecomeVisible)
    //                                 dead / down   killed / damaged until downed
    //                                 stage:<LifeStageDef>  aged into that life stage of its race (metalhorror larva, juvenile, mature)
    //   devour=x,z,tx,tz              the devourer in (x,z) swallows the pawn in (tx,tz) (CompDevourer.StartDigesting, as its ability does)
    //   person=x,z,<PawnKindDef>,<rot 0-3>[,opt...]  a new person (player faction) facing rot; opts: ghoul, shambler (made that
    //                                 mutant), head:<HeadTypeDef>, hair:<HairDef>, body:<BodyTypeDef>, hediff:<HediffDef> (on the torso),
    //                                 noeye:<left|right> (that eye removed), naked, dead
    //   entitycheck=1                 logs every entity (Mechs.Report) and every figure's Anomaly parts (People.Report)
    public static partial class Probe
    {
        // Appended to each walkfilm frame's log line: every nociosphere's opening and activity, every devourer's state.
        private static string EntityFilmNote()
        {
            string s = "";
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return s;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                CompNociosphere n = p.TryGetComp<CompNociosphere>();
                CompActivity act = p.TryGetComp<CompActivity>();
                if (n != null)
                {
                    s += "; nociosphere " + p.Position + " segScale " + n.segScale.ToString("0.00") + " activity " + (act != null ? act.ActivityLevel.ToString("0.00") + (act.IsDormant ? " dormant" : " active") : "-")
                        + " casting " + (p.abilities != null && p.abilities.abilities.Any(ab => ab.Casting));
                }
                CompDevourer d = p.TryGetComp<CompDevourer>();
                if (d != null)
                {
                    s += "; devourer " + p.Position + " digesting " + d.Digesting;
                }
            }
            return s;
        }

        // Ages the pawn into the life stage of its race with that name (just past the stage's minimum age).
        internal static void SetLifeStage(Pawn p, string stage)
        {
            List<LifeStageAge> ages = p.RaceProps.lifeStageAges;
            for (int i = 0; ages != null && i < ages.Count; i++)
            {
                if (ages[i].def.defName == stage)
                {
                    float years = ages[i].minAge + (i + 1 < ages.Count ? (ages[i + 1].minAge - ages[i].minAge) * 0.5f : 0.01f);
                    p.ageTracker.AgeBiologicalTicks = (long)(years * GenDate.TicksPerYear);
                    p.ageTracker.AgeChronologicalTicks = p.ageTracker.AgeBiologicalTicks;
                    p.Drawer?.renderer?.SetAllGraphicsDirty();
                    return;
                }
            }
            Log.Warning("[IsoRim probe] no life stage " + stage + " for " + p.def.defName);
        }

        private static bool Bound(Pawn p)
        {
            ModelEntry e;
            return p.def != null && ModelLibrary.TryGet(p.def, out e) && e.def.entity;
        }

        private static bool EntityKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "entity":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Rot4 rot = new Rot4(I(a[3]));
                    int alt = -2;
                    float activity = -1f, open = -1f;
                    string state = "";
                    string phase = null;
                    bool visible = false;
                    string stageName = null;
                    for (int i = 4; i < a.Length; i++)
                    {
                        if (a[i].StartsWith("alt:")) alt = I(a[i].Substring(4));
                        else if (a[i].StartsWith("activity:")) activity = F(a[i].Substring(9));
                        else if (a[i].StartsWith("open:")) open = F(a[i].Substring(5));
                        else if (a[i] == "active" || a[i] == "passive") phase = a[i];
                        else if (a[i] == "visible") visible = true;
                        else if (a[i].StartsWith("stage:")) stageName = a[i].Substring(6);
                        else state = a[i];
                    }
                    Pawn p = null;
                    for (int tries = 0; tries < 400; tries++)
                    {
                        p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfEntities, PawnGenerationContext.NonPlayer));
                        if (alt < -1 || p.GetGraphicIndex() == alt)
                        {
                            break;
                        }
                    }
                    if (stageName != null)
                    {
                        SetLifeStage(p, stageName);
                    }
                    GenSpawn.Spawn(p, new IntVec3(I(a[0]), 0, I(a[1])), map, rot);
                    p.jobs?.StopAll();
                    p.pather?.StopDead();
                    p.Rotation = rot;
                    if (activity >= 0f)
                    {
                        p.TryGetComp<CompActivity>()?.SetActivity(activity);
                    }
                    if (visible)
                    {
                        foreach (Hediff h in p.health.hediffSet.hediffs)
                        {
                            h.TryGetComp<HediffComp_Invisibility>()?.BecomeVisible(true);
                        }
                    }
                    if (phase == "active")
                    {
                        p.TryGetComp<CompActivity>()?.EnterActiveState();
                    }
                    else if (phase == "passive")
                    {
                        p.TryGetComp<CompActivity>()?.EnterPassiveState();
                    }
                    if (open >= 0f)
                    {
                        CompNociosphere sphere = p.TryGetComp<CompNociosphere>();
                        if (sphere != null)
                        {
                            sphere.segScale = open;
                            HarmonyLib.Traverse.Create(sphere).Field("segScaleGoal").SetValue(open);
                        }
                    }
                    if (state == "dead")
                    {
                        p.Kill(null);
                    }
                    else if (state == "down")
                    {
                        HealthUtility.DamageUntilDowned(p, false);
                    }
                    Log.Message("[IsoRim probe] entity " + kind.defName + " at " + p.PositionHeld + " (alt " + p.GetGraphicIndex() + ", " + state + "): " + Mechs.Report(p));
                    return true;
                }
                case "devour":
                {
                    Pawn d = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn victim = PawnAt(map, I(a[2]), I(a[3]));
                    CompDevourer comp = d != null ? d.TryGetComp<CompDevourer>() : null;
                    if (comp != null && victim != null)
                    {
                        comp.StartDigesting(d.Position, victim);
                    }
                    Log.Message("[IsoRim probe] devour: " + (comp != null && comp.Digesting) + (d != null ? ": " + Mechs.Report(d) : ""));
                    return true;
                }
                case "person":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Rot4 rot = new Rot4(I(a[3]));
                    PawnGenerationRequest req = new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                        forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: false);
                    req.IsCreepJoiner = kind is CreepJoinerFormKindDef;
                    Pawn p = PawnGenerator.GeneratePawn(req);
                    GenSpawn.Spawn(p, new IntVec3(I(a[0]), 0, I(a[1])), map, rot);
                    bool dead = false;
                    for (int i = 4; i < a.Length; i++)
                    {
                        string o = a[i];
                        if (o == "ghoul") MutantUtility.SetPawnAsMutantInstantly(p, MutantDefOf.Ghoul);
                        else if (o == "shambler") MutantUtility.SetPawnAsMutantInstantly(p, MutantDefOf.Shambler);
                        else if (o.StartsWith("head:")) p.story.headType = DefDatabase<HeadTypeDef>.GetNamed(o.Substring(5));
                        else if (o.StartsWith("hair:")) p.story.hairDef = DefDatabase<HairDef>.GetNamed(o.Substring(5));
                        else if (o.StartsWith("body:")) p.story.bodyType = DefDatabase<BodyTypeDef>.GetNamed(o.Substring(5));
                        else if (o.StartsWith("hediff:")) p.health.AddHediff(DefDatabase<HediffDef>.GetNamed(o.Substring(7)), p.RaceProps.body.corePart);
                        else if (o.StartsWith("noeye:"))
                        {
                            string side = o.Substring(6);
                            BodyPartRecord eye = p.RaceProps.body.AllParts.FirstOrDefault(r => r.def == BodyPartDefOf.Eye && r.customLabel != null
                                && r.customLabel.IndexOf(side, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (eye != null && !p.health.hediffSet.PartIsMissing(eye))
                            {
                                p.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, p, eye), eye);
                            }
                        }
                        else if (o == "naked") p.apparel?.DestroyAll();
                        else if (o == "dead") dead = true;
                    }
                    p.jobs?.StopAll();
                    p.pather?.StopDead();
                    p.Rotation = rot;
                    p.Drawer?.renderer?.SetAllGraphicsDirty();
                    if (dead)
                    {
                        p.Kill(null);
                    }
                    Log.Message("[IsoRim probe] person " + kind.defName + " (" + p.def.defName + ") at " + p.PositionHeld + ": " + People.Report(p));
                    return true;
                }
                case "entitycheck":
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (p.RaceProps != null && p.RaceProps.Humanlike && (p.IsMutant || p.def.defName == "CreepJoiner"))
                        {
                            Log.Message("[IsoRim probe] entitycheck " + p.def.defName + " " + p.LabelShort + " at " + p.Position + ": " + People.Report(p));
                        }
                        else if (p.RaceProps != null && !p.RaceProps.Humanlike && Bound(p))
                        {
                            Log.Message("[IsoRim probe] entitycheck " + p.def.defName + " at " + p.Position + " rot " + p.Rotation + ": " + Mechs.Report(p));
                        }
                    }
                    foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
                    {
                        if (!(c.InnerPawn.IsMutant || c.InnerPawn.def.defName == "CreepJoiner" || Bound(c.InnerPawn)))
                        {
                            continue;
                        }
                        Log.Message("[IsoRim probe] entitycheck corpse " + c.InnerPawn.def.defName + " at " + c.Position + ": "
                            + (c.InnerPawn.RaceProps.Humanlike ? People.Report(c.InnerPawn) : Mechs.Report(c.InnerPawn)));
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
