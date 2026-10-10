using System;
using System.Collections;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    // noclip_test (FAT 0.2.729; Fire 10-05: "noclip with godmode flight ... we try to fly under the terrain we are forced back
    // up unless there is a cave below"). On the tester's client, with Server Devcommands installed, on a voxel world:
    //   setup     a column of solid voxel ground near the tester, solid for 45 m under its surface (no cave to fall into);
    //             fly on through the console's `fly` (as a player types it), Server Devcommands' "fly noclip" setting on
    //   noclip_on the tester is put 20 m under that surface (vanilla's teleport refuses a target with no floor under it, so
    //             this setup move is direct, like the drills') and watched for 10 s: it must stay below the ground, and
    //             FAT must report its lifts standing down ("[FiresVoxel] noclip: ... skipped")
    //   noclip_off the same with noclip off (still flying): FAT's under-world rescue must lift the tester back to the ground
    // Afterwards the fly state, the setting and the tester's position are put back as they were. It changes no world data.
    //   noclip_test run [anyworld]   (bare: every step SKIPs)
    internal static class NoClipTest
    {
        private const string Command = "noclip_test";
        private const string Tag = "[NoClipTest]";
        private const string Pass = "PASS", Fail = "FAIL", Skip = "SKIP";
        private const string ModName = global::VerdantsAscent.FiresAdminTerrain.PluginName;
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const string TestWorldMark = "test";
        private const float Depth = 20f, SolidBelow = 45f, HoldSeconds = 10f, LiftWait = 10f, StayMargin = 5f, LiftedMargin = 1.5f;
        private const float SearchStep = 8f, SearchRadius = 96f, HashWaitSeconds = 5f;
        private static readonly string[] Steps = { "setup", "noclip_on", "noclip_off" };

        private static bool s_registered, s_running;
        private static int s_step, s_passes, s_fails, s_skips;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            FatDllHash.Start();
            new Terminal.ConsoleCommand(Command,
                "[F5 test] FAT stands its lifts down for Server Devcommands' noclip flight: 20 m under solid voxel ground for 10 s "
                + "with noclip on (stays down), then the same with it off (lifted). Args: run [anyworld]. Bare: every step SKIPs.",
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

        private static IEnumerator Run(bool go, bool anyWorld)
        {
            s_running = true;
            s_step = s_passes = s_fails = s_skips = 0;
            float started = Time.realtimeSinceStartup;
            var me = Player.m_localPlayer;
            Vector3 home = me.transform.position;
            Quaternion homeRot = me.transform.rotation;
            var sdcSettings = AccessTools.TypeByName("ServerDevcommands.Settings");
            var noclipSetting = (sdcSettings != null ? AccessTools.Field(sdcSettings, "configFlyNoClip") : null)?.GetValue(null)
                as BepInEx.Configuration.ConfigEntry<bool>;
            bool noclipWas = noclipSetting != null && noclipSetting.Value;
            bool flyWas = me.IsDebugFlying();
            bool moved = false;
            try
            {
                float hashUntil = Time.realtimeSinceStartup + HashWaitSeconds;
                while (FatDllHash.Value == FatDllHash.Pending && Time.realtimeSinceStartup < hashUntil) yield return null;
                string world = ZNet.World != null ? ZNet.World.m_name : "?";
                Say($"{Tag} BEGIN {ModName} {ModVersion} ({FatDllHash.Value}) on client '{me.GetPlayerName()}' world '{world}' "
                    + $"{(go ? "run" : "bare")}: {Steps.Length} steps, ~{(go ? 60 : 1)} s");
                string refusal = !go ? $"on demand: '{Command} run'"
                    : !anyWorld && world.IndexOf(TestWorldMark, StringComparison.OrdinalIgnoreCase) < 0 ? $"world '{world}' is not a test world ('anyworld' overrides)"
                    : !ZNet.instance.LocalPlayerIsAdminOrHost() ? "the tester is not an admin (fly needs devcommands)"
                    : noclipSetting == null ? "Server Devcommands is not installed (no Settings.configFlyNoClip)"
                    : !VoxelOnlyMode.Active && !VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(home)) ? "not on voxel ground here"
                    : null;
                if (refusal != null) { foreach (string s in Steps) Step(s, Skip, refusal); yield break; }

                // setup: a column solid for SolidBelow m under its surface.
                Vector3 spot = default;
                float surface = 0f;
                bool found = false;
                for (float r = 0f; r <= SearchRadius && !found; r += SearchStep)
                {
                    int points = r <= 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / SearchStep);
                    for (int k = 0; k < points && !found; k++)
                    {
                        float a = k * 2f * Mathf.PI / points;
                        float x = home.x + Mathf.Cos(a) * r, z = home.z + Mathf.Sin(a) * r;
                        if (!VoxelWorld.TrySampleSurfaceY(x, z, home.y + 200f, home.y - 300f, out float top, out _)) continue;
                        if (VoxelWorld.TrySampleSurfaceY(x, z, top - 1f, top - SolidBelow, out _, out _)) continue;   // a cave or gap below
                        spot = new Vector3(x, top - Depth, z);
                        surface = top;
                        found = true;
                    }
                }
                if (!found) { Step("setup", Fail, $"no voxel column solid for {SolidBelow:0} m under its surface within {SearchRadius:0} m"); Step("noclip_on", Skip, "no site"); Step("noclip_off", Skip, "no site"); yield break; }
                if (!me.IsDebugFlying()) Type("fly");
                yield return new WaitForSeconds(0.5f);
                noclipSetting.Value = true;
                yield return null;
                bool on = FatNoClip.IsOn(me);
                Step("setup", me.IsDebugFlying() && on ? Pass : Fail,
                    $"column at ({spot.x:0.0}, {spot.z:0.0}): surface {surface:0.00}, solid {SolidBelow:0} m under it; flying {me.IsDebugFlying()}, "
                    + $"Server Devcommands noclip (FlyFeatures.NoClipPlayerEnabled) {on}");

                // noclip_on: stays below the ground. The tester goes down the player's way first: Server Devcommands'
                // `goto x,z,y` (its ParseArgs splits the first argument on commas; it sends RPC_TeleportTo, distant). Vanilla's
                // teleport ends on a floor under the target or falls back to the surface, so only a landing really under the
                // ground counts; otherwise the setup move is direct and the step says so.
                int skipsBefore = FatNoClip.SkipCount;
                moved = true;
                string how = null;
                yield return GotoUnder(me, spot, r => how = r);
                if (me.transform.position.y > surface - Depth * 0.5f)
                {
                    how += $"; placed directly instead";
                    Place(me, spot);
                }
                float highest = float.MinValue, until = Time.time + HoldSeconds;
                while (Time.time < until) { highest = Mathf.Max(highest, me.transform.position.y); yield return null; }
                int skips = FatNoClip.SkipCount - skipsBefore;
                bool stayed = highest < surface - StayMargin;
                Step("noclip_on", stayed && skips > 0 ? Pass : Fail,
                    $"{how}; target y {spot.y:0.00} ({Depth:0} m under {surface:0.00}); highest y in {HoldSeconds:0} s {highest:0.00} "
                    + $"({(stayed ? "stayed under the ground" : "LIFTED")}); FAT lifts stood down {skips} time(s)");

                // noclip_off: lifted.
                noclipSetting.Value = false;
                yield return null;
                Place(me, spot);
                float liftedAt = -1f, from = Time.time;
                until = Time.time + LiftWait;
                while (Time.time < until)
                {
                    yield return null;
                    if (me.transform.position.y >= surface - LiftedMargin) { liftedAt = Time.time - from; break; }
                }
                Step("noclip_off", liftedAt >= 0f ? Pass : Fail, liftedAt >= 0f
                    ? $"lifted to y {me.transform.position.y:0.00} (surface {surface:0.00}) after {liftedAt:0.0} s"
                    : $"still at y {me.transform.position.y:0.00} after {LiftWait:0} s (surface {surface:0.00}): nothing lifted it");
            }
            finally
            {
                if (noclipSetting != null && noclipSetting.Value != noclipWas) noclipSetting.Value = noclipWas;
                var now = Player.m_localPlayer;
                if (now != null)
                {
                    if (moved) Place(now, home, homeRot);
                    if (now.IsDebugFlying() != flyWas) Type("fly");
                }
                Say($"{Tag} END {(s_fails > 0 ? Fail : Pass)}: {s_passes} pass, {s_fails} fail, {s_skips} skip in {Time.realtimeSinceStartup - started:0.0} s");
                s_running = false;
            }
        }

        // `goto x,z,y` as a player types it, then the teleport's end (or 20 s). Reports where it left the tester.
        private const float GotoWait = 20f;

        private static IEnumerator GotoUnder(Player me, Vector3 target, Action<string> done)
        {
            var inv = CultureInfo.InvariantCulture;
            string line = string.Format(inv, "goto {0:0.00},{1:0.00},{2:0.00}", target.x, target.z, target.y);
            Type(line);
            float until = Time.time + GotoWait;
            yield return new WaitForSeconds(0.5f);
            while (Time.time < until && me.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(0.5f);
            float y = me.transform.position.y;
            done(Mathf.Abs(y - target.y) <= 3f
                ? $"'{line}' landed at y {y:0.00} (under the ground)"
                : $"'{line}' ended at y {y:0.00}, not under the ground (vanilla's teleport wants a floor under the target)");
        }

        private static void Place(Player me, Vector3 at, Quaternion? rot = null)
        {
            me.transform.position = at;
            if (rot.HasValue) me.transform.rotation = rot.Value;
            var body = me.GetComponent<Rigidbody>();
            if (body != null) { body.position = at; body.linearVelocity = Vector3.zero; }
        }

        // A console line as a player types it; devcommands goes on once first when this admin may.
        private static void Type(string line)
        {
            var console = global::Console.instance;
            if (console == null) return;
            if (!Terminal.m_cheat && ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost()) console.TryRunCommand("devcommands");
            console.TryRunCommand(line);
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
