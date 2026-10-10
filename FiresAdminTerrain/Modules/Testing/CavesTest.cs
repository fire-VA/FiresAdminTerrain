using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    // caves_test (FAT 0.2.732; the lead / Fire 10-05: prove 0.2.720's cave hanger pool). On the tester's client:
    //   pool    every biome's Ceiling list resolves here, each prefab with its attach side, length and whether it can shrink
    //           (the ObjectDB export has no bounds, so a pool prefab that cannot scale and is longer than a cave is tall never
    //           hangs); plus the SERVER's own evidence from this session, asked over an RPC: its "[Caves] ceiling pool:" lines
    //           (what dressing actually met) and its "[Caves] zone (x,y) v5|v6 -> v7: N ceiling hanger(s) re-rolled" lines
    //   find    the nearest ceiling hanger FAT tagged (fires_cave = Ceiling) among the loaded objects, and the mix of hanger
    //           prefabs loaded; when none is loaded it goes looking zone by zone (vanilla teleports, up to MaxHops zones)
    //   view    the tester is teleported (vanilla TeleportTo, which lands on the floor under the target) onto the cave floor
    //           a few metres to the side of it and turned to look up at it by mouse look (Player.SetMouseLook, 0.2.734); PASS
    //           when the camera faces it within FaceLimit and nothing stands between them
    //   shot    a SHOT line of the hanger on its ceiling, with the facing number
    // Afterwards the tester goes back where it started. It changes no world data.  caves_test run [anyworld]   (bare: SKIPs)
    internal static class CavesTest
    {
        private const string Command = "caves_test";
        private const string Tag = "[CavesTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const string TestWorldMark = "test";
        private const string AskRpc = "FiresCaves_TestAsk", ReplyRpc = "FiresCaves_TestReply";
        private const int CeilingKind = 2;   // CaveContentKind.Ceiling
        private const int MaxHops = 12, HopRings = 3;
        private const float ZoneSize = 64f, ReplyWait = 8f, TeleportWait = 25f, SettleSeconds = 3f, AimSeconds = 2f;
        private const float FaceLimit = 10f, FloorLift = 0.3f, ShotHold = 2f, HashWaitSeconds = 5f;
        private static readonly string[] Steps = { "pool", "find", "view", "shot" };

        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;
        private static ZRoutedRpc s_rpcOn;
        private static List<string> s_serverPool, s_serverRerolls;
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] FAT's cave ceiling hangers (0.2.720): the pool and the server's [Caves] evidence, the nearest dressed cave, "
                + "and a shot of its ceiling with a facing number. Args: run [anyworld]. Bare: every step SKIPs.",
                args =>
                {
                    if (s_running) { Say($"{Tag} a run is already going."); return; }
                    if (ZNet.instance == null || Player.m_localPlayer == null) { Say($"{Tag} join a world first."); return; }
                    bool go = false, anyWorld = false;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "run", StringComparison.OrdinalIgnoreCase)) go = true;
                        else if (string.Equals(args[i], "anyworld", StringComparison.OrdinalIgnoreCase)) anyWorld = true;
                    }
                    ZNet.instance.StartCoroutine(Run(go, anyWorld));
                });
        }

        // From FAT's ZNet.Awake hook on every peer: the server answers with its evidence lines.
        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            try
            {
                rpc.Register(AskRpc, new Action<long>(RPC_Ask));
                rpc.Register<ZPackage>(ReplyRpc, RPC_Reply);
                s_rpcOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] caves_test RPC register failed: {ex.Message}"); }
        }

        private static void RPC_Ask(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pkg = new ZPackage();
            Write(pkg, VoxelCaveContent.PoolEvidence);
            Write(pkg, VoxelCaveContent.RerollEvidence);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, pkg);
        }

        private static void Write(ZPackage pkg, List<string> lines)
        {
            List<string> copy;
            lock (lines) copy = lines.ToList();
            pkg.Write(copy.Count);
            foreach (string line in copy) pkg.Write(line);
        }

        private static void RPC_Reply(long sender, ZPackage pkg)
        {
            s_serverPool = Read(pkg);
            s_serverRerolls = Read(pkg);
        }

        private static List<string> Read(ZPackage pkg)
        {
            int n = pkg.ReadInt();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(pkg.ReadString());
            return list;
        }

        private static IEnumerator Run(bool go, bool anyWorld)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            float started = Time.realtimeSinceStartup;
            var me = Player.m_localPlayer;
            Vector3 home = me.transform.position;
            Quaternion homeRot = me.transform.rotation;
            bool moved = false;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + HashWaitSeconds;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on client '{me.GetPlayerName()}' world '{world}' "
                    + $"{(go ? "run" : "bare")}: {Steps.Length} steps, ~{(go ? 120 : 1)} s");
                string refusal = !go ? $"on demand: '{Command} run'"
                    : !anyWorld && world.IndexOf(TestWorldMark, StringComparison.OrdinalIgnoreCase) < 0 ? $"world '{world}' is not a test world ('anyworld' overrides)"
                    : !FiresVoxelConfig.GeneratesCaves ? "this world generates no voxel caves"
                    : null;
                if (refusal != null) { foreach (string s in Steps) Step(s, Skip, refusal); yield break; }

                // pool
                var pool = new List<string>();
                int unusable = 0;
                foreach (var table in CaveContentConfig.AllTables)
                {
                    var entries = table.Value.Ceiling.Entries();
                    if (entries.Count == 0) continue;
                    var parts = new List<string>();
                    foreach (var e in entries)
                    {
                        var anchor = VoxelSurfaceSnap.Anchor(e.Key);
                        if (!anchor.Valid) unusable++;
                        parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2} {3:0.0}m {4}", e.Key.name, e.Value, anchor.Side,
                            anchor.Reach, anchor.Scalable ? "scalable" : "FIXED"));
                    }
                    pool.Add($"{table.Key}: {string.Join(", ", parts.ToArray())}");
                }
                s_serverPool = s_serverRerolls = null;
                EnsureRpcs();
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, AskRpc);
                if (ZNet.instance.IsServer()) RPC_Ask(ZDOMan.GetSessionID());
                float until = Time.realtimeSinceStartup + ReplyWait;
                while (s_serverPool == null && Time.realtimeSinceStartup < until) yield return null;
                string server = s_serverPool == null ? "the server sent no evidence (no reply)"
                    : $"server this session: {s_serverPool.Count} pool line(s) [{string.Join(" | ", s_serverPool.Select(Short).ToArray())}], "
                      + $"{s_serverRerolls.Count} re-roll line(s)" + (s_serverRerolls.Count > 0 ? $" [{string.Join(" | ", s_serverRerolls.Take(4).Select(Short).ToArray())}]" : " (zones were already v7, or none loaded)");
                Step("pool", pool.Count > 0 && unusable == 0 ? Pass : Fail, $"{string.Join("; ", pool.ToArray())}; {server}");

                // find
                ZDO hanger = Nearest(me.transform.position, out string mix);
                int hops = 0;
                Vector3 origin = home;
                foreach (var hop in Hops(origin))
                {
                    if (hanger != null || hops >= MaxHops) break;
                    hops++;
                    float ground = ZoneSystem.instance.GetGroundHeight(hop);
                    me.TeleportTo(new Vector3(hop.x, ground + 1f, hop.z), me.transform.rotation, true);
                    moved = true;
                    float t = Time.time + TeleportWait;
                    yield return new WaitForSeconds(0.5f);
                    while (Time.time < t && me.IsTeleporting()) yield return null;
                    yield return new WaitForSeconds(SettleSeconds);
                    hanger = Nearest(me.transform.position, out mix);
                }
                if (hanger == null)
                {
                    Step("find", Fail, $"no ceiling hanger loaded near the tester, nor in {hops} zone(s) visited around it");
                    Step("view", Skip, "no hanger"); Step("shot", Skip, "no hanger");
                    yield break;
                }
                string prefab = PrefabName(hanger);
                Vector3 at = hanger.GetPosition();
                var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(at.x, at.z) : Heightmap.Biome.None;
                Step("find", Pass, $"{prefab} at ({at.x:0.0}, {at.y:0.0}, {at.z:0.0}) in {biome}, {hops} zone hop(s); loaded hangers: {mix}");

                // view
                if (!VoxelWorld.TrySampleSurfaceY(at.x, at.z, at.y - 0.5f, at.y - 40f, out float floor, out _))
                {
                    Step("view", Fail, "no cave floor under the hanger"); Step("shot", Skip, "no view");
                    yield break;
                }
                me.TeleportTo(new Vector3(at.x + 1.2f, floor + FloorLift, at.z + 1.2f), me.transform.rotation, false);
                moved = true;
                yield return AwaitTeleport(me);
                var go2 = FindObject(hanger);
                Vector3 look = go2 != null ? Middle(go2) : at;
                // 0.2.734 (Run A 10-05: 18.2 deg): stand off to the side, so the hanger is not almost straight overhead (the
                // third-person camera cannot look along the player's vertical), on cave floor from which nothing blocks it.
                if (TryStandOff(look, floor, go2, out Vector3 stand))
                {
                    me.TeleportTo(stand, me.transform.rotation, false);
                    yield return AwaitTeleport(me);
                }
                // Aimed as a player aims, by mouse-look deltas (Player.SetMouseLook): Character.SetLookDir turns a player's
                // yaw only (its pitch is m_lookPitch, which only mouse look moves), which is why 0.2.732 stayed 18 deg low.
                // Steered by what the CAMERA faces, so its offset from the eye is corrected too.
                float aimUntil = Time.time + AimSeconds;
                while (Time.time < aimUntil)
                {
                    var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
                    if (cam != null) me.SetMouseLook(AimStep(cam, look));
                    yield return null;
                }
                var camera = GameCamera.instance != null ? GameCamera.instance.transform : null;
                float facing = camera != null ? Vector3.Angle(camera.forward, look - camera.position) : 180f;
                bool clear = camera != null && ClearLine(camera.position, look, go2);
                Step("view", facing <= FaceLimit && clear ? Pass : Fail, string.Format(CultureInfo.InvariantCulture,
                    "tester on the cave floor at y {0:0.0} under {1}; camera facing it {2:0.0} deg (limit {3:0}); {4}",
                    me.transform.position.y, prefab, facing, FaceLimit, clear ? "nothing in between" : "SOMETHING blocks the view"));

                // shot
                Say($"{Tag} SHOT caves: {prefab} hanging from a {biome} cave ceiling at ({at.x:0}, {at.y:0}, {at.z:0}), facing {facing:0.0} deg");
                yield return new WaitForSecondsRealtime(ShotHold);
                Step("shot", Pass, $"SHOT taken of {prefab}, facing {facing:0.0} deg");
            }
            finally
            {
                var now = Player.m_localPlayer;
                if (now != null && moved) now.TeleportTo(home, homeRot, true);
                Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0.0} s");
                s_running = false;
            }
        }

        private static IEnumerator AwaitTeleport(Player me)
        {
            float until = Time.time + TeleportWait;
            yield return new WaitForSeconds(0.5f);
            while (Time.time < until && me.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(SettleSeconds);
        }

        // A cave-floor spot StandOffMin..StandOffMax m to the side of the hanger, on floor within FloorSpread of the floor under
        // it, from whose eye height nothing but the hanger stands in the way; widest first, so the view is not near-vertical.
        private const float StandOffMin = 1.5f, StandOffMax = 5f, StandOffStep = 0.5f, FloorSpread = 3f, EyeHeight = 1.6f;

        private static bool TryStandOff(Vector3 look, float floor, GameObject hanger, out Vector3 stand)
        {
            stand = default;
            for (float r = StandOffMax; r >= StandOffMin; r -= StandOffStep)
                for (int k = 0; k < 8; k++)
                {
                    float a = k * Mathf.PI / 4f;
                    float x = look.x + Mathf.Cos(a) * r, z = look.z + Mathf.Sin(a) * r;
                    if (!VoxelWorld.TrySampleSurfaceY(x, z, floor + FloorSpread, floor - FloorSpread, out float y, out bool wall) || wall) continue;
                    var eye = new Vector3(x, y + EyeHeight, z);
                    if (!ClearLine(eye, look, hanger)) continue;
                    stand = new Vector3(x, y + FloorLift, z);
                    return true;
                }
            return false;
        }

        // One frame's mouse-look delta (degrees: x = yaw right, y = pitch up) that turns the camera toward the target, half
        // the remaining error at a time and at most MaxAimStep, as a hand on the mouse would.
        private const float MaxAimStep = 10f, AimGain = 0.5f;

        private static Vector2 AimStep(Transform cam, Vector3 target)
        {
            Vector3 want = (target - cam.position).normalized, have = cam.forward;
            float yaw = Vector3.SignedAngle(new Vector3(have.x, 0f, have.z), new Vector3(want.x, 0f, want.z), Vector3.up);
            float pitchUp = (Mathf.Asin(Mathf.Clamp(want.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(have.y, -1f, 1f))) * Mathf.Rad2Deg;
            return new Vector2(Mathf.Clamp(yaw * AimGain, -MaxAimStep, MaxAimStep), Mathf.Clamp(pitchUp * AimGain, -MaxAimStep, MaxAimStep));
        }

        // The nearest Ceiling-tagged hanger among the loaded objects, and the mix of hanger prefabs loaded.
        private static ZDO Nearest(Vector3 from, out string mix)
        {
            ZDO best = null;
            float bestD = float.MaxValue;
            var counts = new Dictionary<string, int>();
            foreach (var zdo in ObjectsById(ZDOMan.instance).Values)
            {
                if (zdo == null || zdo.GetInt(VoxelCaveContent.CaveContentKey) != CeilingKind) continue;
                string name = PrefabName(zdo);
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
                float d = Vector3.Distance(from, zdo.GetPosition());
                if (d < bestD) { bestD = d; best = zdo; }
            }
            mix = counts.Count == 0 ? "none" : string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Key} x{c.Value}").ToArray());
            return best;
        }

        // Zone centres in rings round the start, nearest first.
        private static IEnumerable<Vector3> Hops(Vector3 origin)
        {
            Vector2s home = ZoneSystem.GetZone(origin);
            for (int ring = 1; ring <= HopRings; ring++)
                for (int dz = -ring; dz <= ring; dz++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring) continue;
                        yield return ZoneSystem.GetZonePos(new Vector2s(home.x + dx, home.y + dz));
                    }
        }

        private static GameObject FindObject(ZDO zdo)
        {
            var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            return view != null ? view.gameObject : null;
        }

        private static Vector3 Middle(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return go.transform.position;
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b.center;
        }

        // True when a ray from the camera reaches the hanger (or its point) without hitting anything else first.
        private static bool ClearLine(Vector3 from, Vector3 to, GameObject target)
        {
            Vector3 dir = to - from;
            if (!Physics.Raycast(from, dir.normalized, out RaycastHit hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore)) return true;
            return target != null && hit.collider != null && hit.collider.transform.IsChildOf(target.transform);
        }

        private static string PrefabName(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "#" + zdo.GetPrefab().ToString(CultureInfo.InvariantCulture);
        }

        private static string Short(string line)
        {
            int at = line.IndexOf("[Caves] ", StringComparison.Ordinal);
            string s = at >= 0 ? line.Substring(at + 8) : line;
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        private static void Step(string name, string result, string detail)
        {
            s_step++;
            if (result == Pass) s_passes++;
            else if (result == Fail) s_fails++;
            else s_skips++;
            Say($"{Tag} {s_step}/{Steps.Length} {name} {result} - {detail}");
        }

        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, Command + ".txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
