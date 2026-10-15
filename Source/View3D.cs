using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Camera state and the maths shared by every other part of the mod.
    //
    // The game draws everything as flat quads on the ground and uses height (Y) only to decide
    // what is drawn on top of what. Seen from a tilted camera those layers would drift apart, so
    // the camera's view matrix multiplies Y by Squash. Anything that should really stand up has
    // its height divided by Squash to cancel that out. "Apparent space" below means the world
    // after squashing, which is what ends up on screen.
    public static class View3D
    {
        public const float MinPitch = 30f;
        public const float MaxPitch = 85f;
        public const float IsoYaw = 45f;   // how far the isometric camera is turned from north, clockwise seen from above

        // How far the lighting overlay is pulled toward the camera, and how much depth is kept
        // free either side of the visible ground.
        private const float OverlayPull = 30f;
        private const float DepthPad = 45f;

        public static bool Active;

        // Changes whenever a setting that affects the shape of things changes.
        public static int Stamp;

        public static Matrix4x4 LightingMatrix = Matrix4x4.identity;

        // Outline shells of moving models: how far (in cells) they are pushed straight away from the
        // camera (R3DMaterials.OutlineMoving, IsoRim.vert.glsl).
        public const float OutlinePush = 0.20f;

        private static bool haveFrame;
        private static Vector3 eye;
        private static Vector3 fwd = Vector3.down;
        private static Vector3 up = Vector3.forward;
        private static Vector3 right = Vector3.right;
        private static float orthoSize = 20f;
        private static float aspect = 1.6f;

        private static Camera[] cams;
        private static bool camerasOverridden;

        private static bool lastActive;
        private static int appliedSig;
        private static int pendingSig;
        private static float pendingSince;

        private static readonly MethodInfo regenMethod = AccessTools.Method(typeof(MapDrawer), "RegenerateEverythingNow");

        public static bool Ready
        {
            get { return Active && haveFrame; }
        }

        // How steeply the camera looks down, in degrees: 90 is straight down.
        public static float Pitch
        {
            get
            {
                IsoRimSettings st = IsoRimMod.Settings;
                return Mathf.Clamp(st.isometric ? st.isoPitch : st.pitch, MinPitch, MaxPitch);
            }
        }

        // Which way the camera faces right now, in degrees clockwise from north seen from above.
        // 0 is the game's usual "north is up"; the isometric view starts out looking north-east.
        // It glides to YawTarget when the view is turned.
        public static float Yaw
        {
            get { return yawNow; }
        }

        // Where the camera is turning to: the base direction plus the eighth turns asked for.
        public static float YawTarget
        {
            get { return Mathf.Repeat((IsoRimMod.Settings.isometric ? IsoYaw : 0f) + 45f * Step, 360f); }
        }

        // How many eighth turns clockwise the view has been turned from its starting direction (0 to 7).
        // With 90 degree snaps this only ever takes even values.
        public static int Step
        {
            get { return ((IsoRimMod.Settings.turn % 8) + 8) % 8; }
        }

        // True while the camera is still swinging toward YawTarget.
        public static bool Turning
        {
            get { return yawSet && Mathf.Abs(Mathf.DeltaAngle(yawNow, YawTarget)) > 0.05f; }
        }

        private static float yawNow;
        private static bool yawSet;
        private const float TurnSpeed = 300f;   // degrees a second: an eighth turn takes about a sixth of a second

        private static void TurnCamera()
        {
            float target = YawTarget;
            if (!yawSet || !Active)
            {
                yawNow = target;
                yawSet = true;
            }
            else
            {
                // Eased: fast in the middle of the swing, gentle at the end, so a turn reads as smooth.
                float left = Mathf.Abs(Mathf.DeltaAngle(yawNow, target));
                float speed = TurnSpeed * Mathf.Clamp(left / 30f, 0.25f, 1.6f);
                yawNow = Mathf.Repeat(Mathf.MoveTowardsAngle(yawNow, target, speed * Time.unscaledDeltaTime), 360f);
            }
            // The light no longer follows the camera: the sun stays put in the world while the
            // camera turns round it (see SunStep), so turning never redraws anything.
        }

        // The direction along the ground that runs up the screen, away from the camera.
        public static Vector3 GroundForward
        {
            get
            {
                float y = Yaw * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(y), 0f, Mathf.Cos(y));
            }
        }

        public static float Squash
        {
            // Kept very small: the game spaces its flat drawing layers far apart in height, and
            // whatever height is left after squashing shows as overlays (zone and drag
            // previews, field edges) floating off the ground.
            get { return Mathf.Clamp(IsoRimMod.Settings.squash, 0.002f, 0.004f); }
        }

        // The direction "up the sprite" points for anything stood up to face the camera.
        public static Vector3 CardUp
        {
            get
            {
                float p = Pitch * Mathf.Deg2Rad;
                float y = Yaw * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(p) * Mathf.Sin(y), Mathf.Cos(p), Mathf.Sin(p) * Mathf.Cos(y));
            }
        }

        public static float WallHeight
        {
            get { return Mathf.Clamp(IsoRimMod.Settings.wallHeight, 0.5f, 4f); }
        }

        // Called once per frame from the map camera's update.
        public static void Tick()
        {
            Probe.Begin();
            try
            {
                SpritePixels.Tick();
                Probe.Lap(0);
                TickInner();
            }
            finally
            {
                Probe.End();
            }
        }

        private static void TickInner()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                return;
            }
            // Like the game's own camera update: nothing while a long event (loading a save, making
            // a map) runs, as that work changes the maps on another thread. The game draws the maps
            // when the event ends, and the state check below then rebuilds them for 3D if needed.
            if (LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }

            HandleKeys();
            TurnCamera();

            Camera main = Find.Camera;
            if (main == null)
            {
                return;
            }

            if (Active)
            {
                ApplyCamera(main);
                Grade.Draw(main);
            }
            else if (camerasOverridden)
            {
                ResetCamera();
            }
            Probe.Lap(1);

            RegenerateIfStateChanged();
            Probe.Lap(2);
            MapSections.Process(8);
            Probe.Lap(3);
            Ground.Sync();
            Probe.Lap(4);
            Cutaway.Process();
            Probe.Lap(5);
            if (Ready)
            {
                Cutaway.TrackPointer();
                Probe.Lap(6);
                Effects.DrawAll();
                Spinners.DrawAll();
                Glass.DrawAll();
                WorkPanels.DrawAll();
                Probe.Lap(7);
                try
                {
                    Interaction.DrawDesignationDrag();
                    Interaction.DrawDragGhosts();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Drawing the drag box or ghosts failed: " + e, 93611163);
                }
            }
        }

        private static void HandleKeys()
        {
            // Keys are ignored while a text box has focus, so typing a 0 does not flip the view.
            bool typing = GUIUtility.keyboardControl != 0;

            if (!typing && R3DKeyDefOf.R3D_ToggleView != null && R3DKeyDefOf.R3D_ToggleView.JustPressed)
            {
                Active = !Active;
                Messages.Message(Active ? "IsoRim: 3D view on" : "IsoRim: 3D view off", MessageTypeDefOf.SilentInput, false);
            }

            if (!typing && R3DKeyDefOf.R3D_Isometric != null && R3DKeyDefOf.R3D_Isometric.JustPressed)
            {
                // From the flat game this goes straight to isometric; after that it swaps
                // between isometric and the straight-on 3D view.
                IsoRimSettings st = IsoRimMod.Settings;
                st.isometric = !Active || !st.isometric;
                Active = true;
                Messages.Message(st.isometric ? "IsoRim: isometric view" : "IsoRim: straight-on view", MessageTypeDefOf.SilentInput, false);
            }

            if (!Active || typing)
            {
                return;
            }

            // Q and E turn the view an eighth or a quarter turn (see the options). The game uses the
            // same keys to turn a building that is being placed, so they are left alone while
            // something is selected to build.
            if (Find.DesignatorManager == null || Find.DesignatorManager.SelectedDesignator == null)
            {
                IsoRimSettings turnSettings = IsoRimMod.Settings;
                if (R3DKeyDefOf.R3D_TurnLeft != null && R3DKeyDefOf.R3D_TurnLeft.JustPressed)
                {
                    turnSettings.turn = TurnBy(turnSettings.turn, -1, turnSettings.snap45);
                }
                if (R3DKeyDefOf.R3D_TurnRight != null && R3DKeyDefOf.R3D_TurnRight.JustPressed)
                {
                    turnSettings.turn = TurnBy(turnSettings.turn, 1, turnSettings.snap45);
                }
            }
            if (R3DKeyDefOf.R3D_ToggleRoofs != null && R3DKeyDefOf.R3D_ToggleRoofs.JustPressed)
            {
                Roofs.Toggle();
            }

            float dir = 0f;
            if (R3DKeyDefOf.R3D_TiltUp != null && R3DKeyDefOf.R3D_TiltUp.IsDown)
            {
                dir += 1f;
            }
            if (R3DKeyDefOf.R3D_TiltDown != null && R3DKeyDefOf.R3D_TiltDown.IsDown)
            {
                dir -= 1f;
            }
            if (dir != 0f)
            {
                IsoRimSettings s = IsoRimMod.Settings;
                float change = dir * 40f * Time.unscaledDeltaTime;
                if (s.isometric)
                {
                    s.isoPitch = Mathf.Clamp(s.isoPitch + change, MinPitch, MaxPitch);
                }
                else
                {
                    s.pitch = Mathf.Clamp(s.pitch + change, MinPitch, MaxPitch);
                }
            }
        }

        // One step left (-1) or right (+1) from an eighth-turn count. With quarter snaps an odd
        // count (left over from the eighth snaps) lands on the nearest quarter in that direction.
        public static int TurnBy(int turn, int dir, bool eighths)
        {
            turn = ((turn % 8) + 8) % 8;
            if (eighths)
            {
                return (turn + dir + 8) % 8;
            }
            if (dir > 0)
            {
                return ((turn / 2) + 1) * 2 % 8;
            }
            return (turn % 2 == 1 ? turn - 1 : turn - 2 + 8) % 8;
        }

        private static void ApplyCamera(Camera main)
        {
            if (cams == null || cams.Length == 0 || cams[0] == null)
            {
                // The main camera plus the helper cameras the game hangs under it (water depth etc.),
                // which have to look from the same place or their effects stop lining up.
                cams = main.GetComponentsInChildren<Camera>(true);
            }

            float pitchDeg = Pitch;
            float p = pitchDeg * Mathf.Deg2Rad;
            float s = Squash;

            orthoSize = main.orthographicSize;
            aspect = main.aspect;

            // The game keeps the camera directly above the point at the centre of the screen.
            // The transform is left alone so everything that reads it keeps working.
            Vector3 tp = main.transform.position;
            Vector3 focus = new Vector3(tp.x, 0f, tp.z);

            Quaternion look = Quaternion.Euler(pitchDeg, Yaw, 0f);
            fwd = look * Vector3.forward;
            up = look * Vector3.up;
            right = look * Vector3.right;

            float half = orthoSize / Mathf.Tan(p) + DepthPad;
            eye = focus - fwd * (half + 5f);
            float far = 2f * half + 10f;

            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                * Matrix4x4.TRS(eye, look, Vector3.one).inverse;
            Matrix4x4 worldToCamera = view * Matrix4x4.Scale(new Vector3(1f, s, 1f));

            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null)
                {
                    continue;
                }
                float size = c.orthographicSize;
                float asp = c.aspect;
                Matrix4x4 proj = Matrix4x4.Ortho(-size * asp, size * asp, -size, size, 0.5f, far);
                c.worldToCameraMatrix = worldToCamera;
                c.projectionMatrix = proj;
                c.cullingMatrix = proj * worldToCamera;
            }
            camerasOverridden = true;
            haveFrame = true;
            Cull.SetCamera(main.projectionMatrix * main.worldToCameraMatrix, s);

            // The shaders work out face normals in true space and turn them toward the camera.
            R3DMaterials.SetView(-fwd, s);

            // Moving something straight toward an orthographic camera does not move it on screen,
            // it only puts it in front. That is how the lighting overlay gets to darken upright things.
            LightingMatrix = Matrix4x4.Translate(new Vector3(-fwd.x * OverlayPull, -fwd.y * OverlayPull / s, -fwd.z * OverlayPull));
            UprightCards.SetCamera(pitchDeg, Yaw);
        }

        private static void ResetCamera()
        {
            if (cams != null)
            {
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null)
                    {
                        continue;
                    }
                    c.ResetWorldToCameraMatrix();
                    c.ResetProjectionMatrix();
                    c.ResetCullingMatrix();
                }
            }
            cams = null;
            camerasOverridden = false;
            haveFrame = false;
            Cull.Reset();
        }

        // Baked map geometry (walls, models, plants) depends on these values, so the map is
        // rebuilt when they change (a settings change, after 0.35 s without another). The camera's
        // pitch and turn are not part of this: the little that depends on them is redrawn on its
        // own (see Relight).
        private static int StateSignature()
        {
            if (!Active)
            {
                return 0;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(Squash * 10000f);
                h = h * 31 + Mathf.RoundToInt(WallHeight * 100f);
                h = h * 31 + (st.uprightPlants ? 1 : 0);
                h = h * 31 + (st.walls3D ? 1 : 0);
                h = h * 31 + (st.texturedWalls ? 1 : 0);
                h = h * 31 + (st.autoExtrude ? 1 : 0);
                h = h * 31 + (st.thickItems ? 1 : 0);
                h = h * 31 + (st.itemsFromArt ? 1 : 0);
                h = h * 31 + (st.rawModels ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.itemThickness * 20f);
                h = h * 31 + (st.ghosts3D ? 1 : 0);
                h = h * 31 + (st.cutRock ? 1 : 0);
                h = h * 31 + (st.mapEdge3D ? 1 : 0);
                h = h * 31 + (st.damage3D ? 1 : 0);
                h = h * 31 + (st.outlines ? 1 : 0);
                h = h * 31 + (st.texturedModels ? 1 : 0);
                h = h * 31 + (st.ruggedRock ? 1 : 0);
                h = h * 31 + (st.trees3D ? 1 : 0);
                h = h * 31 + (st.smallPlants3D ? 1 : 0);
                h = h * 31 + (st.models3D ? 1 : 0);
                h = h * 31 + (st.groundShadows ? 1 : 0);
                h = h * 31 + (st.waterStyle ? 1 : 0);
                h = h * 31 + (st.cutRooms ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.cutHeight * 100f);
                h = h * 31 + (st.treeShadows ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(st.treeShadowLength * 100f);
                return h == 0 ? 1 : h;
            }
        }

        private static void RegenerateIfStateChanged()
        {
            int sig = StateSignature();
            Stamp = sig;
            bool toggled = Active != lastActive;
            lastActive = Active;

            if (sig == appliedSig)
            {
                pendingSig = sig;
                RebuildStaleShownMaps();
                Relight();
                return;
            }

            if (!toggled)
            {
                if (sig != pendingSig)
                {
                    pendingSig = sig;
                    pendingSince = Time.realtimeSinceStartup;
                    return;
                }
                if (Time.realtimeSinceStartup - pendingSince < 0.35f)
                {
                    return;
                }
            }

            appliedSig = sig;
            pendingSig = sig;
            R3DMaterials.SetLight(LightNow());
            MapSections.Clear();
            RegenerateAllMaps();
        }

        // The sun moves smoothly with the time of day. Every face is lit per pixel by the shader
        // from the sun direction set here each frame (see R3DMaterials.SetSunHour), so nothing is
        // redrawn or rebuilt as it moves.
        //
        // A tilt redraws nothing: plants stood up as cards turn in their shader (UprightCards).
        // A turn redraws only what was built for the camera's direction, once the swing has ended:
        // - the edge of the map, which is built only on the sides facing the camera;
        // - with "cut walls in front of rooms" on, the walls (which are cut depends on the turn).
        // The blocks are redrawn a few per frame, those in view first.
        private static void Relight()
        {
            if (!Active)
            {
                return;
            }
            R3DMaterials.SetLight(LightNow());

            // A turn counts once the swing ends, also one that came back to where it started
            // (anything redrawn during the swing was built at an angle in between).
            bool turned = false;
            bool turning = Turning;
            int step = Step;
            if (litFacing < 0)
            {
                litFacing = step;
            }
            else if (!turning && (step != litFacing || wasTurning))
            {
                litFacing = step;
                turned = true;
            }
            wasTurning = turning;

            if (!turned)
            {
                return;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            foreach (Map map in Find.Maps)
            {
                bool here = Shown(map);
                if (st.mapEdge3D)
                {
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimEdge), here, true);
                }
                if (st.cutRooms && ShaderCut.On)
                {
                    IsoCellMap.Rebuilt(map);   // (shader cut-away) which walls hide a room depends on the facing: worked out again, nothing rebuilt
                }
                else if (st.cutRooms)
                {
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimWalls), here);
                    MapSections.QueueAll(map, typeof(SectionLayer_IsoRimDamage), here);
                    if (!st.texturedWalls)
                    {
                        // Untextured walls are in the game's things layer.
                        MapSections.QueueAll(map, MapSections.ThingsLayer, here);
                    }
                }
            }
        }

        private static bool wasTurning;
        private static int litFacing = -1;

        // The hour the sun is at: the current map's time of day, or mid-morning with
        // "Sun moves through the day" off.
        // The direction of the light now: IsoRim's sun by day, the game's moon at night (see
        // R3DMaterials.LightAt); with "Sun moves" off, the fixed mid-morning sun.
        public static Vector3 LightNow()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.sunMoves || map == null)
            {
                return R3DMaterials.SunDirAt(9.5f);
            }
            float hour = GenLocalDate.HourFloat(map);
            Vector3 dir = R3DMaterials.LightAt(map, hour);
            if (ModLog.Diag)
            {
                int h = GenLocalDate.HourOfDay(map);
                if (h != loggedHour)
                {
                    loggedHour = h;
                    float glow = GenCelestial.CurCelestialSunGlow(map);
                    Log.Message("[IsoRim] light at " + hour.ToString("0.00") + " h: sun glow " + glow.ToString("0.000") + ", night " + R3DMaterials.NightWeight(glow).ToString("0.00")
                        + ", dir (" + dir.x.ToString("0.000") + ", " + dir.y.ToString("0.000") + ", " + dir.z.ToString("0.000") + ")"
                        + ", azimuth " + Mathf.Repeat(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 360f).ToString("0") + ", elevation " + (Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("0"));
                }
            }
            return dir;
        }

        // With ModLog.Diag the light is logged once per game hour.
        private static int loggedHour = -1;

        public static float SunHour()
        {
            IsoRimSettings st = IsoRimMod.Settings;
            Map map = Find.CurrentMap;
            if (!st.sunMoves || map == null)
            {
                return 9.5f;
            }
            return GenLocalDate.HourFloat(map);
        }

        // Rebuilds the maps on screen now and the others when they are next shown, so a settings
        // change costs one map's rebuild rather than every map's.
        private static void RegenerateAllMaps()
        {
            stale.Clear();
            foreach (Map map in Find.Maps)
            {
                if (Shown(map))
                {
                    Regenerate(map);
                }
                else
                {
                    stale.Add(map);
                }
            }
        }

        private static readonly HashSet<Map> stale = new HashSet<Map>();

        private static void RebuildStaleShownMaps()
        {
            if (stale.Count == 0)
            {
                return;
            }
            stale.RemoveWhere(m => !Find.Maps.Contains(m));
            foreach (Map map in Find.Maps)
            {
                if (stale.Contains(map) && Shown(map))
                {
                    stale.Remove(map);
                    Regenerate(map);
                }
            }
        }

        // Whether a map is drawn now. Only the current map today; with z-levels (design §11) the
        // levels drawn with it would count too.
        public static bool Shown(Map map)
        {
            return map != null && map == Find.CurrentMap;
        }

        private static void Regenerate(Map map)
        {
            if (regenMethod == null)
            {
                Log.ErrorOnce("[IsoRim] MapDrawer.RegenerateEverythingNow was not found, so walls and models will not refresh.", 93611001);
                return;
            }
            if (MapSections.SectionsOf(map) == null)
            {
                // Not drawn yet: the game draws the whole map itself when it is ready. Try again then.
                stale.Add(map);
                return;
            }
            try
            {
                IsoCellMap.Rebuilt(map);
                regenMethod.Invoke(map.mapDrawer, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Rebuilding the map drawing failed: " + e, 93611002);
            }
        }

        // Screen pixel (origin bottom left) to the point on the ground under it.
        public static Vector3 ScreenToGround(Vector2 px)
        {
            return ScreenToHeight(px, 0f);
        }

        // Where the line of sight through a screen point (pixels, from the bottom left) is at a
        // given height above the ground, in cells.
        public static Vector3 ScreenToHeight(Vector2 px, float height)
        {
            float nx = px.x / Screen.width * 2f - 1f;
            float ny = px.y / Screen.height * 2f - 1f;
            Vector3 origin = eye + right * (nx * orthoSize * aspect) + up * (ny * orthoSize);
            float t = (height - origin.y) / fwd.y;
            Vector3 hit = origin + fwd * t;
            return new Vector3(hit.x, height, hit.z);
        }

        // The other way round: where a point (map x and z, real height in cells as y) shows on
        // screen, in pixels from the bottom left.
        public static Vector2 WorldToScreen(Vector3 p)
        {
            Vector3 d = p - eye;
            float nx = Vector3.Dot(d, right) / (orthoSize * aspect);
            float ny = Vector3.Dot(d, up) / orthoSize;
            return new Vector2((nx + 1f) * 0.5f * Screen.width, (ny + 1f) * 0.5f * Screen.height);
        }

        // The block of map cells that can be seen, used by the game to decide what to draw.
        public static CellRect GroundRect()
        {
            // With the camera turned, the screen is a slanted rectangle on the ground, so all
            // four corners count.
            Vector3 a = ScreenToGround(new Vector2(0f, 0f));
            Vector3 b = ScreenToGround(new Vector2(Screen.width, Screen.height));
            Vector3 c = ScreenToGround(new Vector2(0f, Screen.height));
            Vector3 d = ScreenToGround(new Vector2(Screen.width, 0f));
            CellRect r = new CellRect();
            r.minX = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))) - 2;
            r.maxX = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))) + 2;
            r.minZ = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))) - 2;
            r.maxZ = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))) + 2;

            // Extra room on the sides nearest the camera: tall things just off those screen
            // edges still poke into view.
            Vector3 f = GroundForward;
            if (f.z > 0.3f)
            {
                r.minZ -= 6;
            }
            else if (f.z < -0.3f)
            {
                r.maxZ += 6;
            }
            if (f.x > 0.3f)
            {
                r.minX -= 6;
            }
            else if (f.x < -0.3f)
            {
                r.maxX += 6;
            }
            return r;
        }

        // Stands a flat sprite up so that it faces the camera, hinged at pivot.
        public static Matrix4x4 UprightMatrix(Vector3 pivot)
        {
            return Matrix4x4.Translate(pivot)
                * Matrix4x4.Scale(new Vector3(1f, 1f / Squash, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(Pitch - 90f, Yaw, 0f))
                * Matrix4x4.Translate(-pivot);
        }

        // Turns a movement given in screen terms (x to the right, y up the screen) into one along
        // the ground. With exact set, a movement up the screen is also stretched to make up for
        // the ground being seen at a slant, so that a dragged map stays under the pointer.
        public static Vector2 ScreenMoveToGround(Vector2 move, bool exact)
        {
            float y = move.y;
            if (exact)
            {
                y /= Mathf.Max(0.2f, Mathf.Sin(Pitch * Mathf.Deg2Rad));
            }
            float yaw = Yaw * Mathf.Deg2Rad;
            float cy = Mathf.Cos(yaw);
            float sy = Mathf.Sin(yaw);
            return new Vector2(move.x * cy + y * sy, -move.x * sy + y * cy);
        }

        // Raises something by a real height, in cells, without tilting it.
        public static Matrix4x4 LiftMatrix(float height)
        {
            return Matrix4x4.Translate(new Vector3(0f, height / Squash, 0f));
        }
    }
}
