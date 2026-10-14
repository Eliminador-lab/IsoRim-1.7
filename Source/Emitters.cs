using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Buildings that puff the game's own smoke or dust (CompThrownFleckEmitter: smoke spewer, toxic
    // spewer). The game throws each puff from a spot on its flat sprite (a chimney drawn on the
    // sprite is "north" of the building); in 3D, with a model whose entry lists emitters, each puff
    // starts at one of the model's real nozzles instead, and is drawn standing up at the nozzle's
    // height, rising by emitterRise cells a second as it ages. Everything else about the puffs
    // (how often, how many, size, colour, drift, fade) is the game's.
    //
    // Puffs are plain structs in the game's fleck lists, so the ones thrown here are known by
    // their exact spawn position (kept until they have faded).
    public static class Emitters
    {
        private struct Puff
        {
            public float height;
            public float rise;
            public int until;   // game tick after which the puff has certainly faded
        }

        private static readonly Dictionary<Vector3, Puff> puffs = new Dictionary<Vector3, Puff>();
        private static readonly List<Vector3> expired = new List<Vector3>();
        private static int lastSweep;

        // The game sets the height of a puff's spawn position on creation; only x and z identify it.
        private static Vector3 Key(Vector3 v)
        {
            return new Vector3(v.x, 0f, v.z);
        }

        public static bool Has(ModelEntry entry)
        {
            return entry != null && entry.def.emitters != null && entry.def.emitters.Count > 0;
        }

        // In place of CompThrownFleckEmitter.Emit while the 3D view is on. Returns false if it
        // threw the puffs itself.
        public static bool Emit(CompThrownFleckEmitter comp)
        {
            Sweep();
            Thing parent = comp.parent;
            if (!View3D.Ready || parent == null || !parent.Spawned)
            {
                return true;
            }
            ThingForm form = Forms.For(parent.def);
            if (form.form != Form3D.Model || !Has(form.model))
            {
                return true;
            }
            CompProperties_ThrownFleckEmitter props = comp.props as CompProperties_ThrownFleckEmitter;
            if (props == null || props.fleck == null)
            {
                return true;
            }
            ModelEntry entry = form.model;
            ModelDef def = entry.def;
            Quaternion turn = Quaternion.AngleAxis(parent.Rotation.AsAngle + def.rotation + (entry.Swapped(parent.def) ? 90f : 0f), Vector3.up);
            Vector3 fit = entry.Fit(parent.def) * def.scale;
            int lifeTicks = Mathf.CeilToInt((props.fleck.solidTime + props.fleck.fadeInTime + props.fleck.fadeOutTime) * 60f) + 120;
            for (int i = 0; i < props.burstCount; i++)
            {
                // As the game's Emit, from a nozzle (with a little spread across it).
                Vector3 nozzle = def.emitters[Rand.Range(0, def.emitters.Count)];
                Vector3 local = Vector3.Scale(new Vector3(nozzle.x + Rand.Range(-0.08f, 0.08f), 0f, nozzle.z + Rand.Range(-0.08f, 0.08f)), fit);
                Vector3 at = parent.DrawPos + turn * local;
                FleckCreationData data = FleckMaker.GetDataStatic(at, parent.Map, props.fleck, props.scale.RandomInRange);
                data.rotationRate = props.rotationRate.RandomInRange;
                data.instanceColor = Color.Lerp(props.colorA, props.colorB, Rand.Value);
                data.velocityAngle = props.velocityX.RandomInRange;
                data.velocitySpeed = props.velocityY.RandomInRange;
                puffs[Key(data.spawnPosition)] = new Puff { height = nozzle.y * fit.y, rise = def.emitterRise, until = Find.TickManager.TicksGame + lifeTicks };
                parent.Map.flecks.CreateFleck(data);
            }
            return false;
        }

        // The game draws puffs on worker threads, so the puff being drawn is noted per thread and
        // its quad stood up in Patches.DrawBatch_Prefix (Stand), not through DrawHook's shared state.
        [ThreadStatic] private static bool standing;
        [ThreadStatic] private static float standHeight;

        public static bool On
        {
            get { return standing; }
        }

        // Before the game draws one thrown puff: if it is one of ours, stand it up at its height.
        // Only reads the table (it is written while the game ticks, not while it draws).
        public static bool Begin(ref FleckStatic fleck)
        {
            if (puffs.Count == 0 || !View3D.Ready)
            {
                return false;
            }
            Puff p;
            if (!puffs.TryGetValue(Key(fleck.spawnPosition), out p))
            {
                return false;
            }
            standing = true;
            standHeight = p.height + p.rise * fleck.ageSecs;
            return true;
        }

        public static void End()
        {
            standing = false;
        }

        // The puff's flat quad stood upright where it is, facing the camera, raised to its height
        // (as DrawHook's billboards).
        public static Matrix4x4 Stand(Matrix4x4 matrix)
        {
            Vector3 p = matrix.GetColumn(3);
            Matrix4x4 local = Matrix4x4.Translate(-p) * matrix;
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(p.x, p.y + standHeight / s, p.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * local;
        }

        // Now and then, forgets puffs that have faded.
        public static void Sweep()
        {
            if (puffs.Count == 0 || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (Math.Abs(now - lastSweep) < 250)
            {
                return;
            }
            lastSweep = now;
            expired.Clear();
            foreach (KeyValuePair<Vector3, Puff> kv in puffs)
            {
                if (now > kv.Value.until || now < kv.Value.until - 100000)
                {
                    expired.Add(kv.Key);
                }
            }
            for (int i = 0; i < expired.Count; i++)
            {
                puffs.Remove(expired[i]);
            }
        }

        public static int Count
        {
            get { return puffs.Count; }
        }
    }
}
