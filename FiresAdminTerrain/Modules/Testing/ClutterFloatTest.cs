using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Testing
{
    // ── `clutterfloat_test <x> <z> [radius=20]`: is the grass on the ground? (0.2.690; Fire 10-01, HR5: "The clutter grasses
    // don't retarget the leveled terrain after it's leveled; they just float") ─────────────────────────────────────────────
    // Read-only, on-demand F5 contract test (without arguments the step SKIPs). Every vanilla grass instance
    // (InstanceRenderer) within radius of the point gets a down ray on the terrain layer, the one clutter itself is placed
    // on (ClutterSystem.m_placeRayMask; the voxel colliders are on it too). An instance more than FloatTolerance above the
    // first ground under it is FLOATING; the step PASSes at 0 floating. It reports the worst ones and how many found no
    // ground. Grass exists only near the camera, so the tester stands within clutter range. A SHOT follows.
    internal static class ClutterFloatTest
    {
        private const string Command = "clutterfloat_test", Tag = "[ClutterFloatTest]";
        private const float DefaultRadius = 20f, FloatTolerance = 0.2f, RayAbove = 2f, RayLength = 12f, ShotLift = 12f, VoxelReach = 40f, AreaStep = 2f, MinBareArea = 40f, BareRatio = 0.5f;
        private const int ListWorst = 6;
        private const float ShotHoldSeconds = 7.5f;
        private static readonly AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> Instances = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>("m_instances");
        private static readonly AccessTools.FieldRef<InstanceRenderer, int> InstanceCount = AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");
        private static readonly AccessTools.FieldRef<ClutterSystem, GameObject> GrassRoot = AccessTools.FieldRefAccess<ClutterSystem, GameObject>("m_grassRoot");
        private static bool s_registered, s_running;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] [no relaunch] counts grass clutter instances floating more than 0.2 m above the ground within radius, "
                + "then a SHOT. Args: <x> <z> [radius=20] (read-only), or 'live <x> <z> [size=12] [depth=2]': levels a size x size pad "
                + "depth m below the ground there with va_voxel_level (a real edit, admin), waits for its colliders and the grass "
                + "re-roll, then measures inside the pad (Fire's case). Without args the step SKIPs.",
                args => Start(args.Args.Skip(1).ToArray()));
        }

        private static void Start(string[] args)
        {
            if (s_running) { Say($"{Tag} a run is already going."); return; }
            if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
            var inv = CultureInfo.InvariantCulture;
            if (args.Length > 0 && args[0].Equals("live", System.StringComparison.OrdinalIgnoreCase))
            {
                float size = 12f, depth = 2f;
                if (args.Length < 3 || !float.TryParse(args[1], NumberStyles.Float, inv, out float lx) || !float.TryParse(args[2], NumberStyles.Float, inv, out float lz)
                    || args.Length > 3 && !float.TryParse(args[3], NumberStyles.Float, inv, out size)
                    || args.Length > 4 && !float.TryParse(args[4], NumberStyles.Float, inv, out depth))
                { Say($"{Tag} usage: {Command} live <x> <z> [size=12] [depth=2]; nothing started."); return; }
                ZNet.instance.StartCoroutine(RunLive(lx, lz, Mathf.Clamp(size, 4f, 40f), Mathf.Clamp(depth, 0.5f, 6f)));
                return;
            }
            float x = 0f, z = 0f, r = DefaultRadius;
            if (args.Length > 0 && (args.Length < 2 || !float.TryParse(args[0], NumberStyles.Float, inv, out x) || !float.TryParse(args[1], NumberStyles.Float, inv, out z)
                || args.Length > 2 && !float.TryParse(args[2], NumberStyles.Float, inv, out r)))
            { Say($"{Tag} usage: {Command} <x> <z> [radius]; nothing started."); return; }
            ZNet.instance.StartCoroutine(Run(x, z, args.Length == 0 ? 0f : Mathf.Clamp(r, 2f, 60f)));
        }

        private static IEnumerator Run(float x, float z, float radius)
        {
            s_running = true;
            float started = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + 5f;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {mod} {version} ({FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}' "
                    + (radius > 0f ? $"at ({x:0}, {z:0}) r={radius:0}" : "no point") + ": 1 steps, ~3 s");
                if (radius <= 0f) { skip++; Say($"{Tag} 1/1 floating SKIP - on demand: run {Command} <x> <z> [radius]"); yield break; }

                string detail = Measure(x, z, radius, out bool ok, out _, out _);
                if (ok) pass++; else fail++;
                Say($"{Tag} 1/1 floating {(ok ? "PASS" : "FAIL")} - {detail}");
                // 0.2.694: at the tester's eye, looking toward the point (grass is not drawn 15 m up, where this used to look from).
                Vector3 eye = Player.m_localPlayer.transform.position + Vector3.up * LiveEyeHeight;
                float yaw = Mathf.Atan2(x - eye.x, z - eye.z) * Mathf.Rad2Deg;
                Say($"{Tag} SHOT " + "{pose " + string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0},{4:0}", eye.x, eye.y, eye.z, yaw, LiveCameraPitch)
                    + "} the grass over the edited ground, from the tester's eye");
                // 0.2.695 ([lead]: both HR5d SHOTs were "skipped (its moment passed while another shot was paced)"): FDT paces shots
                // 6 s apart, and this END and the next test's SHOT came within that. Hold until the shot has surely been taken.
                yield return new WaitForSecondsRealtime(ShotHoldSeconds);
            }
            finally
            {
                Say($"{Tag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        // ── live: Fire's case, reproduced (0.2.693; [lead]: "level a patch live with grass on screen, then check it") ─────
        // 1 level    the ground at (x, z) by a terrain ray; `va_voxel_level x z size size (ground - depth) 4 0` in this console
        //            (the real V-tool brush, admin; skirt 0 so the pad is exactly size x size); PASS when it answers.
        // 0 approach the tester stands LiveStandOff m west of the spot facing it (a teleport when farther), until grass is drawn there.
        // 2 regrow   waits (cap LiveWaitSeconds) until the pad's colliders are rebuilt (FatGroundProbe.CollidersReady) and the
        //            ground at the centre reads the new level, then until FatClutterRegrow has flushed after that and the
        //            grass has had LiveSettleSeconds to re-roll; PASS when all of that happened, with the regrow counters.
        // 3 floating the census inside the pad (radius size/2): no grass floating, and the pad not bare against the ring.
        private const float LiveWaitSeconds = 20f, LiveSettleSeconds = 2f, LiveLevelDepth = 4f;
        private const float LiveRing = 8f, LiveEyeHeight = 1.7f, LiveCameraBack = 6f, LiveCameraPitch = 8f;
        private const float LiveStandOff = 10f, LiveStandTolerance = 3f, LiveDistantTeleport = 60f, LiveCameraPitchIn = 15f;

        private static IEnumerator RunLive(float x, float z, float size, float depth)
        {
            s_running = true;
            float started = Time.realtimeSinceStartup;
            int pass = 0, fail = 0;
            const int steps = 4;
            void Step(int n, string name, bool ok, string detail) { if (ok) pass++; else fail++; Say($"{Tag} {n}/{steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            try
            {
                float hashUntil = Time.realtimeSinceStartup + 5f;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                const string mod = global::VerdantsAscent.FiresAdminTerrain.PluginName, version = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {mod} {version} ({FatDllHash.Value}) on client '{Player.m_localPlayer.GetPlayerName()}' world '{world}' "
                    + $"live at ({x:0}, {z:0}) size {size:0} depth {depth:0.0}: {steps} steps, ~{LiveWaitSeconds:0} s");
                var inv = CultureInfo.InvariantCulture;
                int mask = LayerMask.GetMask("terrain");
                var centre = new Vector3(x, 0f, z);

                // 0 approach (0.2.694, [lead]: an automated round's tester spawns ~80 m away, past clutter range): stand
                // LiveStandOff m north of the spot, facing it, and wait until the grass there is drawn. Test setup only (a
                // teleport, like the drills' own placement): what is measured is the grass, not the walk.
                var me = Player.m_localPlayer;
                // 0.2.695: WEST of the spot, out of the SHOT camera's line (it looks in from the north).
                Vector3 stand = new Vector3(x - LiveStandOff, 0f, z);
                float standY = Physics.Raycast(stand + Vector3.up * 500f, Vector3.down, out RaycastHit hs, 1000f, mask) ? hs.point.y
                    : WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(stand.x, stand.z) : me.transform.position.y;
                stand.y = standY + 0.3f;
                float away = Utils.DistanceXZ(me.transform.position, stand);
                if (away > LiveStandTolerance)
                {
                    me.TeleportTo(stand, Quaternion.Euler(0f, 90f, 0f), distantTeleport: away > LiveDistantTeleport);
                    float tpUntil = Time.time + LiveWaitSeconds;
                    while (Time.time < tpUntil && (me.IsTeleporting() || Utils.DistanceXZ(me.transform.position, stand) > LiveStandTolerance)) yield return null;
                }
                float drawnUntil = Time.time + LiveWaitSeconds;
                int drawn = 0;
                while (Time.time < drawnUntil && (drawn = CountGrass(centre, size * 0.5f)) == 0) yield return null;
                if (drawn > 0) yield return new WaitForSeconds(LiveSettleSeconds);   // the rest of the ring fills too
                drawn = CountGrass(centre, size * 0.5f);
                Step(1, "approach", drawn > 0, string.Format(inv, "tester {0:0} m from the spot (was {1:0} m away{2}); {3} grass instance(s) drawn over the pad before the edit",
                    Utils.DistanceXZ(me.transform.position, centre), away + LiveStandOff, away > LiveStandTolerance ? ", teleported" : "", drawn));
                if (drawn == 0) yield break;

                // 1 level
                if (!Physics.Raycast(centre + Vector3.up * 500f, Vector3.down, out RaycastHit h0, 1000f, mask))
                { Step(2, "level", false, $"no ground under ({x:0}, {z:0})"); yield break; }
                float before = h0.point.y, target = before - depth;
                global::VerdantsAscent.Performance.ClutterSpread.ResetGateCensus();
                int queued0 = global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.Queued, flushes0 = global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.Flushes;
                string cmd = string.Format(inv, "va_voxel_level {0:0.0} {1:0.0} {2:0} {3:0} {4:0.00} {5:0} 0", x, z, size, size, target, LiveLevelDepth);

                var console = global::Console.instance;
                if (console == null) { Step(2, "level", false, "no console"); yield break; }
                int histBefore = ConsoleLineCount(console);
                console.TryRunCommand(cmd, silentFail: false, skipAllowedCheck: true);
                yield return null;
                string reply = LastConsoleLines(console, histBefore);
                bool refused = reply.IndexOf("refused", System.StringComparison.OrdinalIgnoreCase) >= 0 || reply.IndexOf("not allowed", System.StringComparison.OrdinalIgnoreCase) >= 0;
                Step(2, "level", !refused, string.Format(inv, "ground {0:0.00} → target {1:0.00}; ran '{2}'; FAT says: {3}", before, target, cmd, reply.Length > 0 ? reply : "(no reply)"));
                if (refused) yield break;

                // 2 regrow
                float waitStart = Time.time, levelledAt = -1f;
                float half = size * 0.5f;
                while (Time.time - waitStart < LiveWaitSeconds)
                {
                    bool ready = global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.CollidersReady(new Vector3(x, target, z), half);
                    bool low = Physics.Raycast(centre + Vector3.up * 500f, Vector3.down, out RaycastHit h1, 1000f, mask) && h1.point.y <= target + 0.5f;
                    if (ready && low) { levelledAt = Time.time; break; }
                    yield return null;
                }
                float flushAt = -1f;
                if (levelledAt > 0f)
                {
                    while (Time.time - waitStart < LiveWaitSeconds)
                    {
                        if (global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.LastFlushAt >= levelledAt) { flushAt = global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.LastFlushAt; break; }
                        yield return null;
                    }
                    if (flushAt > 0f) yield return new WaitForSeconds(LiveSettleSeconds);
                }
                int queued = global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.Queued - queued0;
                int flushes = global::VerdantsAscent.Terrain.Voxel.FatClutterRegrow.Flushes - flushes0;
                Step(3, "regrow", levelledAt > 0f && flushAt > 0f, levelledAt < 0f
                    ? $"the pad's colliders / new level did not appear within {LiveWaitSeconds:0} s"
                    : flushAt < 0f
                        ? string.Format(inv, "the pad was levelled {0:0.0} s after the command, but the grass regrow never flushed after it ({1} queued, {2} flushes)", levelledAt - waitStart, queued, flushes)
                        : string.Format(inv, "levelled after {0:0.0} s, grass reset after {1:0.0} s ({2} column/zone(s) queued, {3} flush(es)), then {4:0} s to re-roll", levelledAt - waitStart, flushAt - waitStart, queued, flushes, LiveSettleSeconds));

                // 3 floating (and bare): the pad AND a LiveRing-wide band of untouched ground round it, so the bare check
                // compares the cut with the ground beside it (0.2.694, [lead]: GRASSb's "0 m² untouched").
                string detail = Measure(x, z, half + LiveRing, out bool ok, out _, out _);
                Step(4, "floating", ok, detail);
                // 0.2.694 ([lead]: the 15 m-up SHOT showed no grass, clutter is not drawn that high): at eye height on the
                // new level, LiveCameraBack m back from the pad's north edge, looking north at the cut step, so the grass on
                // both levels is in frame. `pose x,y,z,yaw,pitch` (FDT FiresTestShots): yaw 0 faces +z.
                // 0.2.695 ([lead]: the pose sat at the pad centre): LiveCameraBack m OUTSIDE the pad's north cut edge, at eye
                // height on that untouched ground, looking south into the cut (yaw 180), so the step and the grass on both
                // levels are in frame.
                float camZ = z + half + LiveCameraBack;
                float camGround = Physics.Raycast(new Vector3(x, 500f, camZ), Vector3.down, out RaycastHit hc, 1000f, mask) ? hc.point.y : before;
                Say($"{Tag} SHOT " + "{pose " + string.Format(inv, "{0:0.0},{1:0.0},{2:0.0},180,{3:0}", x, camGround + LiveEyeHeight, camZ, LiveCameraPitchIn)
                    + "} the grass on the pad just levelled, from eye height outside the cut edge, looking in");
                // 0.2.695 ([lead]: both HR5d SHOTs were "skipped (its moment passed while another shot was paced)"): FDT paces shots
                // 6 s apart, and this END and the next test's SHOT came within that. Hold until the shot has surely been taken.
                yield return new WaitForSecondsRealtime(ShotHoldSeconds);
            }
            finally
            {
                Say($"{Tag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, 0 skip in {Time.realtimeSinceStartup - started:0} s");
                s_running = false;
            }
        }

        // Grass instances (snap-to-water included) currently drawn within radius of a point (XZ).
        private static int CountGrass(Vector3 at, float radius)
        {
            var cs = ClutterSystem.instance;
            var root = cs != null ? GrassRoot(cs) : null;
            if (root == null) return 0;
            int n = 0;
            foreach (Transform child in root.transform)
            {
                if (child == null) continue;
                var ir = child.GetComponent<InstanceRenderer>();
                if (ir == null) { if (Utils.DistanceXZ(child.position, at) <= radius) n++; continue; }
                var m = Instances(ir);
                int count = Mathf.Min(InstanceCount(ir), m?.Length ?? 0);
                for (int i = 0; i < count; i++) if (Utils.DistanceXZ((Vector3)m[i].GetColumn(3), at) <= radius) n++;
            }
            return n;
        }

        // The console's text so far, and what it printed after a mark: FAT's reply to the level command.
        private static readonly HarmonyLib.AccessTools.FieldRef<Terminal, List<string>> ChatBuffer = HarmonyLib.AccessTools.FieldRefAccess<Terminal, List<string>>("m_chatBuffer");

        private static int ConsoleLineCount(Terminal t) { try { return ChatBuffer(t)?.Count ?? 0; } catch { return 0; } }

        private static string LastConsoleLines(Terminal t, int from)
        {
            try
            {
                var buf = ChatBuffer(t);
                if (buf == null || buf.Count <= from) return "";
                return string.Join(" / ", buf.Skip(from).Select(s => s.Trim()).Where(s => s.Length > 0).Take(4));
            }
            catch { return ""; }
        }

        private static bool IsEdited(float x, float z, float groundY) => global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.IsGroundEdited(x, z, groundY);

        // The census at a point: one line (without the step prefix), and whether it passes (grass there, none floating).
        private static string Measure(float x, float z, float radius, out bool ok, out int floatingOut, out int totalOut)
        {
                var at = new Vector3(x, 0f, z);
                var cs = ClutterSystem.instance;
                var root = cs != null ? GrassRoot(cs) : null;
                int mask = LayerMask.GetMask("terrain");
                int total = 0, floating = 0, noGround = 0, waterSnapped = 0;
                // Snap-to-water rules (lilies, vass) sit on the water surface by design (ClutterSystem.cs:326-327), above the bed.
                var snaps = new HashSet<string>(cs != null && cs.m_clutter != null
                    ? cs.m_clutter.Where(c => c != null && c.m_snapToWater && c.m_prefab != null).Select(c => c.m_prefab.name)
                    : Enumerable.Empty<string>());
                var worst = new List<(float lift, Vector3 p, string name, string why)>();
                var byName = new Dictionary<string, int>();
                int onVoxel = 0, rayOnVoxel = 0, rayOnVanilla = 0;   // 0.2.696: the ray count split by what it landed on ([release])
                // 0.2.691 ([lead]: "the test is probably blind to the exact fault"): in a zone swapped to voxel ground the
                // ground is the voxel DENSITY surface under the instance (what the player stands on), not whatever collider
                // happens to be under it; grass resting on a stray collider at the original height reads as floating now.
                int onEdited = 0;
                void Judge(Vector3 p, string name)
                {
                    total++;
                    float ground;
                    // 0.2.693: a ray that lands on a VOXEL collider is the ground (the collider mesh sits ~0.35 m off the
                    // density crossing, GRASSa's false 58); the density surface judges only grass standing on something else.
                    bool rayHit = Physics.Raycast(p + Vector3.up * RayAbove, Vector3.down, out RaycastHit hit, RayLength, mask);
                    bool onVoxelCollider = rayHit && hit.collider.GetComponent<global::VerdantsAscent.Terrain.Voxel.VoxelChunkView>() != null;
                    bool voxelZone = global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(p));
                    if (onVoxelCollider) { ground = hit.point.y; rayOnVoxel++; }
                    else if (voxelZone && global::VerdantsAscent.Terrain.Voxel.VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 0.5f, p.y - VoxelReach, out float vy, out _))
                    { ground = vy; onVoxel++; }
                    else if (rayHit) { ground = hit.point.y; rayOnVanilla++; }
                    else { noGround++; return; }
                    // 0.2.694 ([lead]: the bare check had no baseline): edited = ground off the generated height by more than EditedTolerance.
                    if (IsEdited(p.x, p.z, ground)) onEdited++;
                    float lift = p.y - ground;
                    if (lift <= FloatTolerance) return;
                    floating++;
                    byName.TryGetValue(name, out int n);
                    byName[name] = n + 1;
                    worst.Add((lift, p, name, Diagnose(p, mask)));
                }
                if (root != null)
                    foreach (Transform child in root.transform)
                    {
                        if (child == null) continue;
                        string name = child.name.Replace("(Clone)", "").Trim();
                        var ir = child.GetComponent<InstanceRenderer>();
                        if (ir == null)
                        {
                            // Non-instanced clutter: one GameObject per instance.
                            if (Utils.DistanceXZ(child.position, at) > radius) continue;
                            if (snaps.Contains(name)) { waterSnapped++; continue; }
                            Judge(child.position, name);
                            continue;
                        }
                        var m = Instances(ir);
                        int count = Mathf.Min(InstanceCount(ir), m?.Length ?? 0);
                        for (int i = 0; i < count; i++)
                        {
                            Vector3 p = m[i].GetColumn(3);
                            if (Utils.DistanceXZ(p, at) > radius) continue;
                            if (snaps.Contains(name)) { waterSnapped++; continue; }
                            Judge(p, name);
                        }
                    }
                string list = string.Join(" | ", worst.OrderByDescending(w => w.lift).Take(ListWorst)
                    .Select(w => string.Format(CultureInfo.InvariantCulture, "{0} {1:0.00} m up at ({2:0.0}, {3:0.0}, {4:0.0}): {5}", w.name, w.lift, w.p.x, w.p.y, w.p.z, w.why)));
                string kinds = string.Join(", ", byName.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
                // 0.2.693 ([lead], Fire: "no more grass over edited terrain at all"): the BARE ratio. Grass per m² on voxel-
                // edited ground (a VAVoxel edit anchor's chunk) against the rest of the circle, from a 2 m grid of the
                // circle's ground. Where the two differ by more than BareRatio, the edited ground is bare: FAIL.
                int editedCells = 0, plainCells = 0;
                for (float dz = -radius; dz <= radius; dz += AreaStep)
                    for (float dx = -radius; dx <= radius; dx += AreaStep)
                    {
                        if (dx * dx + dz * dz > radius * radius) continue;
                        // HasEdits tests a 3D chunk cube, so each cell is tested at its ground (a terrain ray), not at y 0.
                        var cell = new Vector3(x + dx, 0f, z + dz);
                        if (!Physics.Raycast(cell + Vector3.up * 500f, Vector3.down, out RaycastHit ch, 1000f, mask)) continue;
                        if (IsEdited(cell.x, cell.z, ch.point.y)) editedCells++; else plainCells++;
                    }
                float editedArea = editedCells * AreaStep * AreaStep, plainArea = plainCells * AreaStep * AreaStep;
                int onPlain = total - onEdited;
                float dEdited = editedArea > 0f ? onEdited / editedArea : 0f, dPlain = plainArea > 0f ? onPlain / plainArea : 0f;
                bool bare = editedArea >= MinBareArea && plainArea >= MinBareArea && dPlain > 0f && dEdited < dPlain * BareRatio;
                string bareText = editedArea < MinBareArea ? $"bare check: only {editedArea:0} m² of edited ground in the circle (needs {MinBareArea:0})"
                    : string.Format(CultureInfo.InvariantCulture, "bare check: {0:0.00} grass/m² on {1:0} m² of edited ground vs {2:0.00} on {3:0} m² untouched{4}",
                        dEdited, editedArea, dPlain, plainArea, bare ? $" - BARE (under {BareRatio:P0} of untouched)" : "");
                ok = cs != null && total > 0 && floating == 0 && !bare;
                floatingOut = floating; totalOut = total;
                float fromTester = Utils.DistanceXZ(Player.m_localPlayer.transform.position, at);
                int moved = global::VerdantsAscent.Performance.ClutterGroundProbe.s_voxelCorrected;
                string gEdited = global::VerdantsAscent.Performance.ClutterSpread.GateCensus(true);
                string gPlain = global::VerdantsAscent.Performance.ClutterSpread.GateCensus(false);
                int noHit = global::VerdantsAscent.Performance.ClutterGroundProbe.s_noHit;
                int unresolved = global::VerdantsAscent.Performance.ClutterGroundProbe.s_unresolved;
                string lastUnresolved = global::VerdantsAscent.Performance.ClutterGroundProbe.s_lastUnresolved;
                int pending = global::VerdantsAscent.Performance.ClutterSpread.BuildersPending;
                int starved = global::VerdantsAscent.Performance.ClutterSpread.s_starvedTotal;
                var cam = Utils.GetMainCamera();
                bool queuedNow = cam != null && cs != null && Heightmap.HaveQueuedRebuild(cam.transform.position, cs.m_distance);
                string gates = $"placement gates on edited ground: {gEdited} | on untouched ground: {gPlain} | GetGroundInfo misses: "
                    + $"{noHit} no hit, {unresolved} unresolved" + (unresolved > 0 ? $" (last '{lastUnresolved}')" : "")
                    + $" | grass fill: {pending} patch(es) waiting, held back {starved} frame(s) by a queued heightmap rebuild, one queued now: {queuedNow}";
                return $"{bareText}; {gates}; "
                    + $"{floating} of {total} grass instance(s) within {radius:0} m float more than "
                    + $"{FloatTolerance:0.0} m above the ground{(floating > 0 ? $" ({kinds}); worst: {list}" : "")}; judged by a ray onto voxel ground {rayOnVoxel} / onto vanilla ground {rayOnVanilla}, on the voxel density surface {onVoxel}; "
                    + $"{noGround} found no ground under them; {waterSnapped} water-snapped (lilies, vass) not counted; "
                    + $"grass candidates moved onto the voxel ground this session: {moved}"
                    + (cs == null ? "; no ClutterSystem on this client" : total == 0 ? $"; no grass here (the tester is {fromTester:0} m away, clutter range {cs.m_distance:0} m)" : "");
        }

        // Why a floating instance floats: what the PLACEMENT ray (ClutterGroundProbe's, from 500 m up on the terrain layer)
        // lands on, the voxel density surface and vanilla's heightmap there, and whether that heightmap's collider is on.
        private static string Diagnose(Vector3 p, int mask)
        {
            var inv = CultureInfo.InvariantCulture;
            string placed = "the placement ray hits nothing";
            if (Physics.Raycast(new Vector3(p.x, p.y + 500f, p.z), Vector3.down, out RaycastHit hit, 1000f, mask))
            {
                var c = hit.collider;
                string kind = c.GetComponent<global::VerdantsAscent.Terrain.Voxel.VoxelChunkView>() != null ? "voxel"
                    : c.GetComponent<Heightmap>() != null ? "heightmap" : "other";
                placed = string.Format(inv, "placement ray hits {0} '{1}' at y {2:0.00}", kind, c.name, hit.point.y);
            }
            string voxel = global::VerdantsAscent.Terrain.Voxel.VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 2f, p.y - VoxelReach, out float vy, out _)
                ? string.Format(inv, "voxel y {0:0.00}", vy) : "no voxel column";
            var hm = Heightmap.FindHeightmap(p);
            string hmText = hm != null && Heightmap.GetHeight(p, out float hy)
                ? string.Format(inv, "heightmap y {0:0.00} (collider {1})", hy, hm.GetComponent<MeshCollider>() is MeshCollider mc ? (mc.enabled ? "ON" : "off") : "none")
                : "no heightmap";
            string zone = global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(p)) ? "voxel zone" : "zone NOT swapped to voxel";
            return $"{placed}; {voxel}; {hmText}; {zone}";
        }

        // The log line, the console, and BepInEx\FiresTests\clutterfloat_test.txt, which FDT's runner also reads.
        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, Command + ".txt"),
                    $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{System.Environment.NewLine}");
            }
            catch (System.Exception) { }
        }
    }
}
