using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    /// <summary>
    /// 0.2.1216, IH skip 0.2.1217, IH markers 0.2.1218, generated zones only 0.2.1222 (Fire 10-28: the VA DN vegetation / location reset + Deep North update; Tools\PLAN_DN_world_ops_20261028.md).
    /// `fires_regen veg|loc|both|dn <zones.txt | biome=A,B | x0 z0 x1 z1> <protect.txt | none> [apply]` - server / host only.
    /// Re-runs vanilla's own zone generation (ZoneSystem.SpawnZone, Ghost mode) for the chosen zones with FAT's BcImport generator
    /// active, after removing what that generation placed there before:
    ///   veg - creator-0 ZDOs whose prefab is one of ZoneSystem.m_vegetation's, plus the zone's _ZoneCtrl;
    ///   loc - the zone's location instance: every creator-0 ZDO within its radius (exterior and interior), re-placed at the same spot;
    ///   dn  - both, for the Deep North zone list (fires_bc_dn_census writes it).
    ///   both - veg and loc, for any zone selection (0.2.1355).
    /// Refuses unless BcMigratedWorld.Active and no peer is connected or still connecting. Skips protected zones (the wsr protect file,
    /// format agreed with [visual]: "x,z  reasons", # comments), zones with any creator != 0 ZDO (backstop), and zones loaded right now.
    /// Without "apply" it is a dry run that writes nothing to the world. Undo = [visual]'s wsr snapshot taken before each step.
    /// </summary>
    internal static class BcRegenCommand
    {
        private const string Command = "fires_regen";   // 0.2.1388 (Fire: "biome, location, and veg resets run on any world it has nothing to do with bc"); fires_bc_regen stays as a hidden alias
        private const string FatVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const float TerrainWaitSeconds = 30f;
        private const float InfiniteHealthAt = 1e15f;   // Infinity Hammer overwrites health with ~1e30; no vanilla piece comes near 1e15
        // 0.2.1218 ([ghost], from Infinity Hammer e4e170f7e4 CustomHealth.cs + Hashes.cs): invulnerable pieces save "health" -1 and
        // "WearNTear.m_health" -1/-2/-4; hardened trees / rocks / destructibles save "<Type>.m_minToolTier" = int.MaxValue / 2;
        // Legacy / "Overwrite health" saves 1e30 health (creatures: max_health 1e30). Any one of these protects.
        // 0.2.1221 (Infinity Tools "Admin Buildings", a player's yaml Fire shared): f=Destructible|TreeBase|MineRock5,minToolTier,999 and
        // f=Piece|ItemStand,canBeRemoved,false. So any tool tier above vanilla's (>= 5) and any canBeRemoved = false also protect.
        // Both key spellings ("Type.m_field" and "Type.field") are checked.
        private static readonly int WntHealth = "WearNTear.m_health".GetStableHashCode(), WntHealthBare = "WearNTear.health".GetStableHashCode(),
            MaxHealth = "max_health".GetStableHashCode();
        private static readonly int[] ToolTierKeys = KeysFor(new[] { "Destructible", "TreeBase", "TreeLog", "MineRock", "MineRock5" }, "minToolTier");
        private static readonly int[] RemovableKeys = KeysFor(new[] { "Piece", "ItemStand" }, "canBeRemoved");
        private const int AboveVanillaToolTier = 5;

        private static int[] KeysFor(string[] types, string field)
        {
            var keys = new List<int>();
            foreach (string t in types) { keys.Add($"{t}.m_{field}".GetStableHashCode()); keys.Add($"{t}.{field}".GetStableHashCode()); }
            return keys.ToArray();
        }

        private static bool IsBalVegetation(GameObject p)
        {
            if (!p.name.EndsWith("_bal", StringComparison.Ordinal)) return false;
            if (p.GetComponent<Character>() != null || p.GetComponent<ItemDrop>() != null || p.GetComponent<Piece>() != null || p.GetComponent<Container>() != null) return false;
            if (s_worldRun && (p.GetComponentInChildren<SpawnArea>(true) != null || p.GetComponentInChildren<CreatureSpawner>(true) != null || p.GetComponentInChildren<Plant>(true) != null)) return true;   // 0.2.1359: _bal nests and saplings go in a world run only
            if (p.GetComponentInChildren<SpawnArea>(true) != null || p.GetComponentInChildren<CreatureSpawner>(true) != null) return false;
            return p.GetComponentInChildren<TreeBase>(true) != null || p.GetComponentInChildren<Destructible>(true) != null || p.GetComponentInChildren<Pickable>(true) != null
                || p.GetComponentInChildren<MineRock>(true) != null || p.GetComponentInChildren<MineRock5>(true) != null;
        }

        private static bool BalKept(string name)
        {
            if (s_balInclude != null) foreach (string k in s_balInclude) if (BalNameMatches(name, k)) return false;
            if (s_balKeep != null) foreach (string k in s_balKeep) if (BalNameMatches(name, k)) return true;
            foreach (string k in BalKeepDefault) if (BalNameMatches(name, k)) return true;
            return false;
        }

        private static bool BalNameMatches(string name, string pattern)
        {
            bool head = pattern.StartsWith("*"), tail = pattern.EndsWith("*");
            string core = pattern.Trim('*');
            if (head && tail) return name.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0;
            if (tail) return name.StartsWith(core, StringComparison.OrdinalIgnoreCase);
            if (head) return name.EndsWith(core, StringComparison.OrdinalIgnoreCase);
            return name.Equals(core, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHardenedProp(ZDO z)
        {
            foreach (int k in ToolTierKeys) if (z.GetInt(k, 0) >= AboveVanillaToolTier) return true;
            return false;
        }

        private static bool IsInfinityHammered(ZDO z)
        {
            float wnt = Mathf.Min(z.GetFloat(WntHealth, 0f), z.GetFloat(WntHealthBare, 0f)), hp = z.GetFloat(ZDOVars.s_health, 0f), max = z.GetFloat(MaxHealth, 0f);
            if (wnt < 0f || hp < 0f || float.IsInfinity(hp) || hp >= InfiniteHealthAt || max >= InfiniteHealthAt) return true;
            foreach (int k in ToolTierKeys) if (z.GetInt(k, 0) >= AboveVanillaToolTier) return true;
            foreach (int k in RemovableKeys) if (z.GetInt(k, 1) == 0) return true;
            return false;
        }

        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedRef =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
        private static readonly AccessTools.FieldRef<ZNet, List<ZNetPeer>> PeersRef = AccessTools.FieldRefAccess<ZNet, List<ZNetPeer>>("m_peers");
        private static readonly MethodInfo FindObjectsMethod = AccessTools.Method(typeof(ZDOMan), "FindObjects");
        private static readonly MethodInfo SpawnZoneMethod = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
        private static readonly MethodInfo PokeMethod = AccessTools.Method(typeof(ZoneSystem), "PokeCanSpawnLocation");   // 0.2.1239
        private static readonly Type ClearAreaType = AccessTools.Inner(typeof(ZoneSystem), "ClearArea");

        private static bool s_running;
        private static bool s_quietTrace;
        private static HashSet<string> s_prefabFilter;      // 0.2.1230: veg mode only these prefabs (null = all)
        private static bool s_skipVegActive;                // 0.2.1230: loc mode - PlaceVegetation stands aside for the zone being regenerated
        // 0.2.1230 ([lead]: the doubled vegetation ran 13% unnoticed): every ZDO the regen's SpawnZone creates is counted by family -
        // made inside PlaceVegetation = vegetation, inside PlaceLocations = location objects, anything else = other (the zone control).
        private static bool s_counting;
        private static int s_phase;   // 0 other, 1 vegetation, 2 location
        private static readonly int[] s_placed = new int[3];
        private static List<ZDO> s_sample;   // 0.2.1232: location objects of the zone being spawned, for the per-type data proof
        private static List<ZDO> s_zoneCreated;   // 0.2.1238: every ZDO one regen SpawnZone created, for the per-zone placed-by-prefab line
        private static readonly Dictionary<int, bool> s_live = new Dictionary<int, bool>();
        // 0.2.1235: a creature, player or loose item - never part of a location's own spawn, so a loc regen leaves it alone.
        private static bool IsLiveThing(int prefabHash)
        {
            if (s_live.TryGetValue(prefabHash, out bool live)) return live;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            live = go != null && (go.GetComponent<Character>() != null || go.GetComponent<ItemDrop>() != null || go.GetComponent<Tameable>() != null);
            s_live[prefabHash] = live;
            return live;
        }
        private static Heightmap.Biome s_wrongScope; private static string s_wrongScopeName;   // 0.2.1360 (Fire 11-03: "for now were just handling the deep north wrong biome"): which biomes lose wrong-biome locations
        private static string s_regenAt = "";   // 0.2.1365 (11-03: a world apply sat silent past 99%): current zone + stage, logged every 60 s
        private static IEnumerator RegenBeat()
        {
            var wait = new WaitForSecondsRealtime(60f);
            while (s_running) { FiresLogger.LogDebug($"[BcImport] {Command}: at {s_regenAt}"); yield return wait; }
        }
        private static HashSet<int> s_orphanHashes; private static Heightmap.Biome s_replan;   // 0.2.1362 (Fire 11-03, floating vines; location spacing): both off unless named
        private static bool s_worldRun;   // 0.2.1359 (Fire 11-03: "a command that does locations and veg/spawners etc in one full world update")
        private static HashSet<string> s_newTypeNames, s_chainTypes;   // 0.2.1233: new location types to plan (types= / newfrom=), null = none
        private static string s_newFromNote, s_chainNote;
        // 0.2.1234: EWD blueprint locations share a stand-in prefab, so a type is named by m_prefabName / m_name, not m_prefab.Name.
        internal static string LocName(ZoneSystem.ZoneLocation l)
        {
            if (l == null) return "?";
            if (!string.IsNullOrEmpty(l.m_prefabName)) return l.m_prefabName;
            if (l.m_prefab.IsValid) return l.m_prefab.Name;
            return l.m_name ?? "?";
        }
        private static bool AnyName(ZoneSystem.ZoneLocation l, HashSet<string> set)
            => set != null && (set.Contains(LocName(l)) || (!string.IsNullOrEmpty(l.m_name) && set.Contains(l.m_name)) || (l.m_prefab.IsValid && set.Contains(l.m_prefab.Name)));
        private static int s_vegSkipped;
        // 0.2.1355 (Fire 11-03: "an argument to ignore safezones, so we can reset the whole deep north without worrying about anything that had been built there or explored so far"): the zone stops off. Built objects are never removed: pieces stay standing and the reset happens around them.
        private static bool s_ignoreProtect;
        // 0.2.1383 (Fire 11-04, the reduced reset: built zones + 1 zone, shrines stay): "realbuilds": an invulnerable object inside a placed location's radius is that location's own
        // (shrines are made of these) and no longer makes its zone count as built. It is still never removed.
        private static bool s_realBuilds;
        private static int s_locationOwned, s_locationVegKept, s_locationPiecesGone;
        // 0.2.1383 (read of the loc path: everything creator 0 inside the radius went): terrain, water and paint records and FAT's world records are not a location's objects.
        private static readonly HashSet<int> s_neverRemoved = new HashSet<int>
        {
            "_TerrainCompiler".GetStableHashCode(), "CustomRiver".GetStableHashCode(), "VATile".GetStableHashCode(), "VAVoxel".GetStableHashCode(), "VALandIce".GetStableHashCode(),
            "VALandSnow".GetStableHashCode(), "FiresLiquidWater".GetStableHashCode(), "FiresZonesRegistry".GetStableHashCode(),
        };

        // 0.2.1384 (Fire: ore is untouchable; the copy test re-rolled tin 33 % lower with its yaml unchanged): "noreroll=<file>" in FiresWorldGen/regen, one prefab a line. A veg reset neither
        // removes nor places these, so a re-roll can't move what players mine and pick for progress.
        private static HashSet<int> s_noReroll;
        private static void FilterVegetation(ZoneSystem zs)
        {
            if (s_prefabFilter == null && s_noReroll == null) return;
            s_savedVeg = zs.m_vegetation;
            zs.m_vegetation = s_savedVeg.FindAll(v => v.m_prefab != null && (s_prefabFilter == null || s_prefabFilter.Contains(v.m_prefab.name)) && (s_noReroll == null || !s_noReroll.Contains(v.m_prefab.name.GetStableHashCode())));
        }

        private static bool OwnedByLocation(ZoneSystem zs, Vector3 p, Vector2s zone)
        {
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (!zs.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var inst) || inst.m_location == null) continue;
                    float r = Mathf.Max(inst.m_location.m_exteriorRadius, inst.m_location.m_interiorRadius);
                    float ox = p.x - inst.m_position.x, oz = p.z - inst.m_position.z;
                    if (ox * ox + oz * oz <= r * r) return true;
                }
            return false;
        }
        private static int s_builtIgnored;
        private static readonly HashSet<Vector2s> s_builtZones = new HashSet<Vector2s>();
        // 0.2.1355 (lead for Fire 11-03, keep list not answered yet): pickups and ore stay unless include= names them; a later yaml cannot undo a deletion. A * at either end of a name matches any text.
        private static readonly string[] BalKeepDefault = { "Pickable_*", "MineRock_*", "*deposit*", "rock_silver*", "rock_nickel*", "gold_Rock*", "coal_rock*" };
        private static List<string> s_balKeep;   // keep=a,b adds to BalKeepDefault
        private static List<string> s_balInclude;
        private static List<ZoneSystem.ZoneVegetation> s_savedVeg;
        private const int Lookahead = 8;                    // HeightmapBuilder keeps 16 finished builds; stay well under
        private static float FrameBudgetMs => ZNet.instance != null && ZNet.instance.IsDedicated() ? 30f : 8f;
        /// <summary>0.2.1223: true while a regen runs without "Regen verbose logging" - FatLocationTrace stays silent then.</summary>
        internal static bool QuietTrace => s_running && s_quietTrace;
        private static bool Verbose => global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.RegenVerbose?.Value == true;
        private static Vector2s s_vegOnlyZone;
        private static bool s_vegOnlyActive;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fires_bc_regen", "Old name of " + Command + ".", args => FromConsole(args), isCheat: true, isSecret: true);
            new Terminal.ConsoleCommand(Command,
                "Admin: regenerate vegetation (veg), locations (loc), both, or Deep North zones (dn), on any world. On a client "
                + "joined to a server it runs ON the server. Usage: " + Command + " veg|loc|both|dn <zones.txt | census | biome=A,B | x0 z0 x1 z1> <protect.txt | none> [ignoreprotect] [keep=a,b] [include=a,b] [apply]"
                + " - on a server the files are names in its <savedir>/" + ServerFolderName + " folder; without apply it is a dry run.",
                args => FromConsole(args), isCheat: true);
        }

        // 0.2.1219 (Fire: "make it a command that an admin types and it pushes to the server"): a client sends the args to the server,
        // which checks the sender is an admin, runs it there (BcImport + the ZDOs live there) and sends the result lines back.
        private const string RpcRequest = "FAT_BcRegenRequest", RpcReply = "FAT_BcRegenReply";
        private const string ServerFolderName = "FiresWorldGen/regen";
        private const int MaxArgs = 10, MaxArgLength = 2048, MaxPackageBytes = 16384, ZoneLimit = 200;
        private static ZRoutedRpc s_rpcOn;
        private static long s_requester;   // 0 = the local console

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            s_rpcOn = rpc;
            rpc.Register<ZPackage>(RpcRequest, OnRequest);
            rpc.Register<string>(RpcReply, OnReply);
            FiresLogger.LogDebug($"[BcImport] {Command}: RPCs registered on this ZRoutedRpc ({FatVersion})");
        }

        private static string ServerFolder() => Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", "regen");

        private static void FromConsole(Terminal.ConsoleEventArgs args)
        {
            var parts = new string[Math.Max(0, args.Length - 1)];
            for (int i = 1; i < args.Length; i++) parts[i - 1] = args[i];
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                EnsureRpcs();
                if (ZRoutedRpc.instance == null) { args.Context?.AddString(Command + ": not connected."); return; }
                if (parts.Length > MaxArgs) { args.Context?.AddString(Command + ": too many arguments."); return; }
                var pkg = new ZPackage();
                pkg.Write(parts.Length);
                foreach (string p in parts) pkg.Write(p.Length > MaxArgLength ? p.Substring(0, MaxArgLength) : p);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcRequest, pkg);
                args.Context?.AddString(Command + ": sent to the server; its answer comes back here.");
                FiresLogger.LogDebug($"[BcImport] {Command}: sent to the server: {string.Join(" ", parts)}");
                return;
            }
            Execute(parts, 0L, false, s => { args.Context?.AddString(s); FiresLogger.LogInfo("[BcImport] " + s); });
        }

        private static void OnReply(long sender, string line)
        {
            var server = ZNet.instance?.GetServerPeer();
            if (server == null || sender != server.m_uid) { FiresLogger.LogWarning($"[BcImport] {Command}: ignored a reply from {sender} (not the server)"); return; }
            if (line != null && line.Length > 2000) line = line.Substring(0, 2000);
            global::Console.instance?.AddString(line);
            FiresLogger.LogInfo("[BcImport] (server) " + line);
        }

        private static void OnRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            void Reply(string s)
            {
                FiresLogger.LogInfo($"[BcImport] (for {sender}) " + s);
                ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcReply, s);
            }
            if (!global::VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender))
            { FiresLogger.LogWarning($"[BcImport] {Command}: REFUSED a request from {sender}: not on the admin list"); Reply(Command + ": refused - you are not an admin on this server."); return; }
            string[] parts;
            try
            {
                if (pkg == null || pkg.Size() > MaxPackageBytes) throw new Exception("request too large");
                int n = pkg.ReadInt();
                if (n < 0 || n > MaxArgs) throw new Exception("bad argument count " + n);
                parts = new string[n];
                for (int i = 0; i < n; i++) { string s = pkg.ReadString(); if (s == null || s.Length > MaxArgLength) throw new Exception("argument too long"); parts[i] = s; }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BcImport] {Command}: bad request from {sender}: {ex.Message}"); Reply(Command + ": refused - bad request (" + ex.Message + ")."); return; }
            FiresLogger.LogDebug($"[BcImport] {Command}: admin {sender} asked: {string.Join(" ", parts)}");
            Execute(parts, sender, true, Reply);
        }

        // A remote file argument is a bare name inside the server folder: no separators, no "..", no drive.
        private static bool ResolveFile(string arg, bool remote, out string path, out string error)
        {
            path = arg; error = null;
            if (!remote) return true;
            if (arg.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || arg.Contains("..") || arg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { error = $"'{arg}' must be a file name only, in the server's <savedir>/{ServerFolderName} folder"; return false; }
            path = Path.Combine(ServerFolder(), arg);
            return true;
        }

        private static void Execute(string[] a, long requester, bool remote, Action<string> Say)
        {
            string[] allArgs = a;   // 0.2.1357: the parser below strips flags from a; the biome scan re-runs the command with these, or ignoreprotect / include= / keep= are lost.
            if (s_running) { Say(Command + ": already running."); return; }
            s_worldRun = a.Length >= 2 && a[0].Equals("world", StringComparison.OrdinalIgnoreCase);
            if (ZNet.instance == null || ZoneSystem.instance == null || WorldGenerator.instance == null || Game.instance == null) { Say(Command + ": no world loaded."); return; }
            if (!ZNet.instance.IsServer()) { Say(Command + ": refused - server/host only."); return; }
            // 0.2.1388: any world may run this. The one refusal left: a world with BetterContinents data that isn't in use, because regenerating would then use vanilla's map.
            if (!BcMigratedWorld.Active && ZNet.World != null && File.Exists(Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "worlds_local", ZNet.World.m_name + ".BetterContinents")))
            { Say(Command + ": refused - this world has a BetterContinents map that is not in use (" + BcMigratedWorld.Describe() + "); regenerating would use vanilla's map."); return; }
            Say($"{Command}: {(BcMigratedWorld.Active ? "BcImport active (" + BcMigratedWorld.Describe() + ")" : "plain world")} on the server, seed {WorldGenerator.instance.GetSeed()}, FAT {FatVersion}.");
            int others = OtherPeers(requester);
            if (others != 0) { Say($"{Command}: refused - {others} other peer(s) connected or connecting; every other player must be off."); return; }
            if (a.Length < (s_worldRun ? 2 : 3)) { Say("Usage: " + Command + " world <protect.txt> [apply] | veg|loc|both|dn <zones.txt | census | biome=A,B | x0 z0 x1 z1> <protect.txt | none> [ignoreprotect] [keep=a,b] [include=a,b] [apply]" + (remote ? $" (files in the server's <savedir>/{ServerFolderName})" : "")); return; }

            // 0.2.1230 (the sulfur plan): optional "prefab=a,b" anywhere after the mode - only those prefabs are removed; vegetation among
            // them is re-placed by vanilla's own placement for just those entries; anything else in the list is "remove only".
            s_newTypeNames = null; s_newFromNote = null; s_ignoreProtect = false; s_realBuilds = false; s_locationOwned = 0; s_locationVegKept = 0; s_locationPiecesGone = 0; s_noReroll = null; s_builtIgnored = 0; s_builtZones.Clear(); s_balKeep = null; s_balInclude = null;
            s_prefabFilter = null;
            Vector2s? afterArg = null; HashSet<int> orphanArg = null; Heightmap.Biome replanArg = Heightmap.Biome.None;
            string wrongArg = null; Heightmap.Biome wrongMask = Heightmap.Biome.All;
            var rest = new List<string>();
            foreach (string arg in a)
            {
                if (arg.StartsWith("prefab=", StringComparison.OrdinalIgnoreCase))
                {
                    s_prefabFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string p in arg.Substring(7).Split(',')) if (p.Trim().Length > 0 && p.Trim().Length <= 64) s_prefabFilter.Add(p.Trim());
                    if (s_prefabFilter.Count == 0 || s_prefabFilter.Count > 32) { Say(Command + ": prefab= needs 1-32 prefab names."); return; }
                }
                else if (arg.StartsWith("after=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] ap = arg.Substring(6).Split((char)44);
                    if (ap.Length != 2 || !int.TryParse(ap[0], out int ax) || !int.TryParse(ap[1], out int az)) { Say($"{Command}: after= needs x,z."); return; }
                    afterArg = new Vector2s(ax, az);
                }
                else if (arg.StartsWith("orphans=", StringComparison.OrdinalIgnoreCase))
                {
                    orphanArg = new HashSet<int>();
                    foreach (string on in arg.Substring(8).Split((char)44)) if (on.Trim().Length > 0) orphanArg.Add(on.Trim().GetStableHashCode());
                }
                else if (arg.StartsWith("replan=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!BcImageMaps.TryParseBiome(arg.Substring(7).Trim(), out replanArg) || replanArg == Heightmap.Biome.None) { Say($"{Command}: unknown biome in {arg}."); return; }
                }
                else if (arg.StartsWith("wrongbiome=", StringComparison.OrdinalIgnoreCase))
                {
                    string wv = arg.Substring(11); wrongArg = wv; Heightmap.Biome wm = Heightmap.Biome.None;
                    if (!wv.Equals("all", StringComparison.OrdinalIgnoreCase))
                        foreach (string wb in wv.Split((char)44))
                        {
                            if (!BcImageMaps.TryParseBiome(wb.Trim(), out Heightmap.Biome w1) || w1 == Heightmap.Biome.None) { Say($"{Command}: unknown biome '{wb.Trim()}' in wrongbiome=."); return; }
                            wm |= w1;
                        }
                    wrongMask = wv.Equals("all", StringComparison.OrdinalIgnoreCase) ? Heightmap.Biome.All : wm;
                }
                else if (arg.StartsWith("include=", StringComparison.OrdinalIgnoreCase))
                {
                    s_balInclude = new List<string>();
                    foreach (string k in arg.Substring(8).Split(',')) if (k.Trim().Length > 0 && k.Trim().Length <= 64) s_balInclude.Add(k.Trim());
                    if (s_balInclude.Count == 0 || s_balInclude.Count > 64) { Say(Command + ": include= needs 1-64 prefab names (a * at either end matches any text)."); return; }
                }
                else if (arg.StartsWith("keep=", StringComparison.OrdinalIgnoreCase))
                {
                    s_balKeep = new List<string>();
                    foreach (string k in arg.Substring(5).Split(',')) if (k.Trim().Length > 0 && k.Trim().Length <= 64) s_balKeep.Add(k.Trim());
                    if (s_balKeep.Count == 0 || s_balKeep.Count > 64) { Say(Command + ": keep= needs 1-64 prefab names (a * at either end matches any text)."); return; }
                }
                else if (arg.Equals("ignoreprotect", StringComparison.OrdinalIgnoreCase)) s_ignoreProtect = true;
                else if (arg.Equals("realbuilds", StringComparison.OrdinalIgnoreCase)) s_realBuilds = true;
                else if (arg.StartsWith("noreroll=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ResolveFile(arg.Substring(9), remote, out string nrPath, out string nrErr)) { Say(Command + ": noreroll file " + nrErr); return; }
                    s_noReroll = new HashSet<int>();
                    foreach (string line in File.ReadAllLines(nrPath)) if (line.Trim().Length > 0 && !line.StartsWith("#")) s_noReroll.Add(line.Trim().GetStableHashCode());
                    if (s_noReroll.Count == 0) { Say(Command + ": noreroll file is empty."); return; }
                    Say($"{Command}: {s_noReroll.Count:N0} prefab(s) are not re-rolled.");
                }
                else if (arg.StartsWith("types=", StringComparison.OrdinalIgnoreCase))
                {
                    s_newTypeNames = s_newTypeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string t in arg.Substring(6).Split(',')) if (t.Trim().Length > 0 && t.Trim().Length <= 64) s_newTypeNames.Add(t.Trim());
                }
                else if (arg.StartsWith("newfrom=", StringComparison.OrdinalIgnoreCase))
                {
                    // 0.2.1233: a yaml in the server's BepInEx/config/expand_world (bare name only); its "- prefab: X" entries are the types to plan
                    string fn = arg.Substring(8);
                    if (fn.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || fn.Contains("..") || !fn.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) { Say(Command + ": newfrom= takes a .yaml file name in the server's config/expand_world folder."); return; }
                    string fp = Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "expand_world"), fn);
                    if (!File.Exists(fp)) { Say(Command + ": newfrom= file not found: " + fp); return; }
                    s_newTypeNames = s_newTypeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int added = 0;
                    foreach (string line in File.ReadAllLines(fp))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*-\s*prefab:\s*([A-Za-z0-9_\-]+)");
                        if (m.Success && s_newTypeNames.Add(m.Groups[1].Value)) added++;
                    }
                    s_newFromNote = $"{fn} ({added} type(s))";
                }
                else rest.Add(arg);
            }
            a = rest.ToArray();
            // 0.2.1383 (Fire 11-04: "run another reset with reduced amounts in both"): "thin <protect.txt | none> [apply]": a lowered yaml quantity changes nothing already placed, and a loc reset
            // re-places each location at its spot, so this takes each type down to its quantity. Nothing is placed back.
            // 0.2.1388: pieces left at a spot whose location record is already gone (a paint on 0.2.1387 took the shrine's record and left its pieces). The file comes from a save made before: type x z per line.
            if (a.Length > 0 && a[0].Equals("orphans", StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length < 2) { Say("Usage: " + Command + " orphans <spots.txt> [apply]"); return; }
                string oPath = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), ServerFolderName, Path.GetFileName(a[1]));
                if (!File.Exists(oPath)) { Say(Command + ": no file " + oPath); return; }
                bool oApply = a.Length > 2 && a[2].Equals("apply", StringComparison.OrdinalIgnoreCase);
                RunOrphans(oPath, oApply, Say);
                return;
            }
            if (a.Length > 0 && a[0].Equals("thin", StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length < 2) { Say("Usage: " + Command + " thin <protect.txt | none> [percent=N] [apply]"); return; }
                bool tApply = false;
                int tPercent = 0;   // 0.2.1385 (Fire: "I said reduce the rate of EVERYTHING evenly"): N % of each type, from what stands, not down to the yaml's quantity
                for (int i = 2; i < a.Length; i++)
                {
                    if (a[i].Equals("apply", StringComparison.OrdinalIgnoreCase)) tApply = true;
                    else if (a[i].StartsWith("percent=", StringComparison.OrdinalIgnoreCase) && (!int.TryParse(a[i].Substring(8), out tPercent) || tPercent < 1 || tPercent > 90)) { Say(Command + ": percent= takes 1 to 90."); return; }
                }
                var tProtect = new HashSet<Vector2s>(); string tPath = a[1];
                if (!a[1].Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ResolveFile(a[1], remote, out string pp, out string perr)) { Say(Command + ": protection file " + perr); return; }
                    if (!ReadZoneFile(pp, null, tProtect, out string err)) { Say(Command + ": protection file " + err); return; }
                    tPath = pp;
                }
                if (FindObjectsMethod == null) { Say(Command + ": refused - a vanilla member it needs is missing."); return; }
                s_requester = requester;
                Say($"{Command} thin {(tApply ? "APPLY" : "dry run")}: protect {tPath} ({tProtect.Count:N0} zones)");
                Game.instance.StartCoroutine(RunThin(tProtect, tPath, tApply, tPercent, Say));
                return;
            }
            // 0.2.1231 (Fire's shrine yaml): "genloc <protect.txt | none> [types=a,b] [apply]" plans location types that have NO instance yet
            // (or the listed types) with FAT's accelerator under vanilla's rules, refusing protected zones; apply registers them and spawns the
            // ones that landed in already generated zones (location objects only; vegetation is never placed).
            if (a.Length > 0 && a[0].Equals("genloc", StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length < 2) { Say("Usage: " + Command + " genloc <protect.txt | none> [types=a,b] [apply]"); return; }
                bool gApply = false;
                for (int i = 2; i < a.Length; i++) if (a[i].Equals("apply", StringComparison.OrdinalIgnoreCase)) gApply = true;
                // 0.2.1233 ([lead]: the default planned 19 types nobody asked for): genloc plans ONLY the named types
                var only = s_newTypeNames;
                if (only == null || only.Count == 0) { Say("Usage: " + Command + " genloc <protect.txt | none> types=a,b | newfrom=<file.yaml in config/expand_world> [apply] - name the types to plan."); return; }
                var gProtect = new HashSet<Vector2s>(); string gHeader = "none", gPath = a[1];
                if (!a[1].Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ResolveFile(a[1], remote, out string pp, out string perr)) { Say(Command + ": protection file " + perr); return; }
                    if (!ReadZoneFile(pp, null, gProtect, out string err, h => gHeader = h)) { Say(Command + ": protection file " + err); return; }
                    gPath = pp;
                }
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var inst in ZoneSystem.instance.m_locationInstances.Values) if (inst.m_location != null) { string n = LocName(inst.m_location); counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1; }
                var types = new List<ZoneSystem.ZoneLocation>();
                foreach (var l in ZoneSystem.instance.m_locations)
                {
                    if (l == null || !l.m_enable || l.m_quantity <= 0) continue;
                    string n = LocName(l);
                    if (AnyName(l, only) && (counts.TryGetValue(n, out int have) ? have : 0) < l.m_quantity) types.Add(l);   // 0.2.1241: top up to quantity
                }
                if (types.Count == 0) { Say($"{Command} genloc: no location type to plan ({(only != null ? "none of types= is a loaded, enabled location" : "every loaded type already has instances")})."); return; }
                if (SpawnZoneMethod == null || FindObjectsMethod == null || GeneratedRef(ZoneSystem.instance) == null) { Say(Command + ": refused - a vanilla member it needs is missing."); return; }
                s_requester = requester;
                Say($"{Command} genloc {(gApply ? "APPLY" : "dry run")}: {types.Count} location type(s) ({string.Join(", ", types.ConvertAll(t => LocName(t)).GetRange(0, Math.Min(8, types.Count)))}{(types.Count > 8 ? ", ..." : "")}); protect list {gProtect.Count} zone(s) ({gHeader}); the total comes back when it ends.");
                Game.instance.StartCoroutine(RunGenLoc(types, gProtect, gPath, gApply, Say));
                return;
            }
            if (a.Length < (s_worldRun ? 2 : 3)) { Say("Usage: " + Command + " world <protect.txt> [apply] | veg|loc|both|dn <zones.txt | census | biome=A,B | x0 z0 x1 z1> <protect.txt | none> [ignoreprotect] [keep=a,b] [include=a,b] [apply] [prefab=a,b]"); return; }
            string mode = a[0].ToLowerInvariant();
            if (s_worldRun) mode = "both";
            s_orphanHashes = orphanArg; s_replan = replanArg;
            // world defaults to Deep North; biome= / list runs keep removing every wrong-biome location unless wrongbiome= says otherwise.
            s_wrongScope = wrongArg != null ? wrongMask : s_worldRun ? Heightmap.Biome.DeepNorth : Heightmap.Biome.All;
            s_wrongScopeName = wrongArg ?? (s_worldRun ? "DeepNorth" : "all");
            if (mode != "veg" && mode != "loc" && mode != "dn" && mode != "both") { Say(Command + ": mode must be veg, loc, both or dn."); return; }
            if (s_prefabFilter != null && mode != "veg") { Say(Command + ": prefab= works with veg mode only."); return; }
            var zones = new List<Vector2s>();
            int next;
            var inv = CultureInfo.InvariantCulture;            if (s_worldRun)
            {
                if (s_ignoreProtect) { Say(Command + ": world keeps build protection; ignoreprotect is refused."); s_worldRun = false; return; }
                zones.AddRange(GeneratedRef(ZoneSystem.instance));
                zones.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
                if (afterArg.HasValue) { Vector2s af = afterArg.Value; int before = zones.Count; zones.RemoveAll(p => (p.x < af.x || (p.x == af.x && p.y <= af.y)) && !zc0Past(p)); Say($"{Command} world: after={af.x},{af.y}: {zones.Count:N0} of {before:N0} zone(s) left."); }
                s_balInclude = new List<string> { "*" };
                s_newTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var wl in ZoneSystem.instance.m_locations) if (wl != null && wl.m_enable && wl.m_quantity > 0) s_newTypeNames.Add(LocName(wl));
                Say($"{Command} world: {zones.Count:N0} generated zone(s), locations + vegetation + _bal + _bal spawners and saplings, then a top-up of every location type below its quantity.");
                next = 1;
            }
            else

            if (a.Length >= 6 && int.TryParse(a[1], NumberStyles.Integer, inv, out int x0) && int.TryParse(a[2], NumberStyles.Integer, inv, out int z0)
                && int.TryParse(a[3], NumberStyles.Integer, inv, out int x1) && int.TryParse(a[4], NumberStyles.Integer, inv, out int z1))
            {
                if (Math.Abs(x0) > ZoneLimit || Math.Abs(z0) > ZoneLimit || Math.Abs(x1) > ZoneLimit || Math.Abs(z1) > ZoneLimit) { Say($"{Command}: zone coordinates must be within +-{ZoneLimit}."); return; }
                for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                    for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++) zones.Add(new Vector2s(x, z));
                next = 5;
            }
            // 0.2.1355 (Fire 11-03: "target resets to any particular biome ... a veg reset, a location reset, or both at once"; [saddles]' drop): names go through BcImageMaps' parser, so Expand World biomes work too. One biome per name: "All" is refused, a whole-world reset is not a typo away.
            else if (a[1].StartsWith("biome=", StringComparison.OrdinalIgnoreCase))
            {
                Heightmap.Biome mask = Heightmap.Biome.None;
                foreach (string b in a[1].Substring(6).Split(','))
                {
                    if (!BcImageMaps.TryParseBiome(b.Trim(), out Heightmap.Biome one) || one == Heightmap.Biome.None || ((int)one & ((int)one - 1)) != 0)
                    { Say($"{Command}: unknown biome '{b.Trim()}'."); return; }
                    mask |= one;
                }
                if (s_biomeScanned == null) { Game.instance.StartCoroutine(BiomeScanThenExecute(allArgs, requester, remote, Say, mask)); return; }
                zones.AddRange(s_biomeScanned); s_biomeScanned = null;
                Say($"{Command}: biome - {zones.Count} zone(s) read {a[1].Substring(6)} through the live generator (any of centre + 4 samples at 16 m).");
                next = 2;
            }
            else if (a[1].Equals("census", StringComparison.OrdinalIgnoreCase))
            {
                DeepNorthZones(zones);
                Say($"{Command}: census - {zones.Count} zone(s) read Deep North through the live generator.");
                next = 2;
            }
            else
            {
                if (!ResolveFile(a[1], remote, out string zp, out string perr)) { Say(Command + ": zone list " + perr); return; }
                if (!ReadZoneFile(zp, zones, null, out string err)) { Say(Command + ": zone list " + err); return; }
                next = 2;
            }
            if (a.Length <= next) { Say(Command + ": give a protection file or \"none\"."); return; }
            string protectArg = a[next];
            if (s_ignoreProtect && !protectArg.Equals("none", StringComparison.OrdinalIgnoreCase)) { Say(Command + ": ignoreprotect and a protection file contradict each other; pass \"none\""); return; }
            if (s_ignoreProtect) Say(Command + ": ignoreprotect - player-built and admin objects no longer stop a zone or keep a location; they stay standing and the reset happens around them.");
            bool apply = a.Length > next + 1 && a[next + 1].Equals("apply", StringComparison.OrdinalIgnoreCase);
            var protect = new HashSet<Vector2s>();
            string protectHeader = "none";
            if (!protectArg.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (!ResolveFile(protectArg, remote, out string pp, out string perr)) { Say(Command + ": protection file " + perr); return; }
                if (!ReadZoneFile(pp, null, protect, out string err, h => protectHeader = h)) { Say(Command + ": protection file " + err); return; }
                if (protect.Count == 0 && apply) { Say(Command + ": refused - the protection file lists no zones; pass \"none\" to mean none."); return; }
                protectArg = pp;
            }
            if (SpawnZoneMethod == null || FindObjectsMethod == null || ClearAreaType == null || GeneratedRef(ZoneSystem.instance) == null)
            { Say(Command + ": refused - a vanilla member it needs is missing (SpawnZone / FindObjects / ClearArea / m_generatedZones)."); return; }

            s_requester = requester;
            int inRegion = 0, ungenerated = 0; var gen0 = GeneratedRef(ZoneSystem.instance); foreach (var zid in zones) { if (protect.Contains(zid)) inRegion++; if (!gen0.Contains(zid)) ungenerated++; }
            Say($"{Command} {mode} {(apply ? "APPLY" : "dry run")}: {zones.Count} zone(s) selected; protect list {protect.Count} zone(s) world-wide ({protectHeader}), {inRegion} of them in this selection; {ungenerated} selected zone(s) never generated (left alone); the total comes back when it ends.");   // 0.2.1220 wording
            s_chainTypes = s_newTypeNames; s_chainNote = s_newFromNote;
            Game.instance.StartCoroutine(Run(mode, zones, protect, protectArg, apply, Say));
        }

        private static int OtherPeers(long requester)
        {
            var peers = PeersRef(ZNet.instance);
            if (peers == null) return -1;
            int n = 0;
            foreach (var p in peers) if (p == null || requester == 0L || p.m_uid != requester) n++;
            return n;
        }

        // The same 5-sample test as fires_bc_dn_census, run on the server so a client admin needs no file.
        private static void DeepNorthZones(List<Vector2s> zones) => BiomeZones(zones, Heightmap.Biome.DeepNorth);

        // 0.2.1356 (11-03 r39: the whole scan in one frame stalled past ZRpc's 30 s and dropped the client): the scan yields under the frame budget, then the command runs again with the list.
        // A surface prop is an orphan when no location instance reaches it: its parent location was removed or moved.
        private static bool OrphanProp(ZoneSystem zs, Vector3 p)
        {
            Vector2s c = ZoneSystem.GetZone(p);
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    if (zs.m_locationInstances.TryGetValue(new Vector2s(c.x + dx, c.y + dz), out var li) && li.m_location != null
                        && new Vector2(p.x - li.m_position.x, p.z - li.m_position.z).magnitude <= Mathf.Max(li.m_location.m_exteriorRadius, li.m_location.m_interiorRadius) + 8f) return false;
            return true;
        }

        private static bool zc0Past(Vector2s zone) { Vector3 c = ZoneSystem.GetZonePos(zone); return new Vector2(c.x, c.z).magnitude > global::VerdantsAscent.Terrain.Voxel.FatWorldSize.EdgeRadius; }
        private static List<Vector2s> s_biomeScanned;
        private static IEnumerator BiomeScanThenExecute(string[] a, long requester, bool remote, Action<string> say, Heightmap.Biome mask)
        {
            s_running = true;
            say($"{Command}: scanning the world for {mask}, spread over frames...");
            var found = new List<Vector2s>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            IEnumerator scan = BiomeZonesStep(found, mask, FrameBudgetMs);
            while (scan.MoveNext()) yield return null;
            s_running = false;
            FiresLogger.LogDebug($"[BcImport] {Command}: biome scan {found.Count} zone(s) in {clock.Elapsed.TotalSeconds:0.0} s (why: over frames, a client times out at 30 s)");
            if (ZNet.instance == null || WorldGenerator.instance == null) yield break;
            s_biomeScanned = found;
            Execute(a, requester, remote, say);
        }

        private static void BiomeZones(List<Vector2s> zones, Heightmap.Biome mask)
        {
            IEnumerator scan = BiomeZonesStep(zones, mask, float.MaxValue);
            while (scan.MoveNext()) { }
        }

        private static void RunOrphans(string path, bool apply, Action<string> say)
        {
            var zs = ZoneSystem.instance; long me = ZDOMan.GetSessionID();
            int spots = 0, missing = 0, live = 0, built = 0, gone = 0;
            var gathered = new List<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>(); var seen = new HashSet<ZDO>();
            foreach (string line in File.ReadAllLines(path))
            {
                var f = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3 || f[0].StartsWith("#", StringComparison.Ordinal)) continue;
                if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                spots++;
                float radius = f.Length > 3 && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float fr) ? fr : 0f;
                var pieces = Zones.BiomePaint.ReadBlueprint(f[0]);
                if (pieces == null) { missing++; continue; }
                Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
                if (zs.m_locationInstances.TryGetValue(zone, out var inst) && inst.m_location != null && inst.m_placed && LocName(inst.m_location) == f[0]) { live++; continue; }   // it still has its record: loc owns it
                gathered.Clear(); visited.Clear(); seen.Clear();
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { new Vector2s(zone.x + dx, zone.y + dz), gathered, visited });
                foreach (ZDO o in gathered)
                {
                    if (!seen.Add(o)) continue;
                    Vector3 p = o.GetPosition();
                    float d = new Vector2(p.x - x, p.z - z).magnitude;
                    // 0.2.1391 (Fire: "the old shrines mainly match the new names so we could infer the radius from that"): an old shrine built before its blueprint changed: its invulnerable pieces inside the yaml radius of the shrine of that name go too.
                    bool inRadius = radius > 0f && d <= radius && o.GetLong(ZDOVars.s_creator, 0L) == 0 && IsInfinityHammered(o);
                    if (!pieces.Holds(o.GetPrefab(), d) && !inRadius) continue;
                    if (o.GetLong(ZDOVars.s_creator, 0L) != 0) { built++; continue; }
                    gone++;
                    if (!apply) continue;
                    if (!o.IsOwner()) o.SetOwner(me);
                    Zones.BiomePaint.DisarmRespawn(o);
                    ZDOMan.instance.DestroyZDO(o);
                }
            }
            say($"{Command} orphans {(apply ? "APPLY" : "dry run")}: {spots} spot(s), {gone} piece(s) {(apply ? "removed" : "would go")}; {live} spot(s) still have their location, {missing} with no blueprint, {built} matching piece(s) with a creator left alone.");
        }

        private static IEnumerator BiomeZonesStep(List<Vector2s> zones, Heightmap.Biome mask, float budgetMs)
        {
            float frameStart = Time.realtimeSinceStartup;
            var wg = WorldGenerator.instance; var gen = BcMigratedWorld.Generator;
            float radius = Mathf.Max(gen != null ? gen.Settings.WorldSize : 10000f, global::VerdantsAscent.Terrain.Voxel.FatWorldSize.EdgeRadius);   // 0.2.1358 (11-03: Deep North's ring past 10,064 m was never selected; the land runs to the edge)
            int zr = Mathf.CeilToInt(radius / ZoneSystem.c_ZoneSize);
            for (int zx = -zr; zx <= zr; zx++)
                for (int zz = -zr; zz <= zr; zz++)
                {
                    if ((Time.realtimeSinceStartup - frameStart) * 1000f >= budgetMs) { yield return null; frameStart = Time.realtimeSinceStartup; }
                    Vector3 c = ZoneSystem.GetZonePos(new Vector2s(zx, zz));
                    if (new Vector2(c.x, c.z).magnitude > radius + ZoneSystem.c_ZoneSize) continue;
                    for (int s = 0; s < 5; s++)
                    {
                        float x = c.x + (s == 0 ? 0f : (s == 1 || s == 3 ? -16f : 16f)), z = c.z + (s == 0 ? 0f : (s <= 2 ? -16f : 16f));
                        if ((wg.GetBiome(x, z) & mask) != 0) { zones.Add(new Vector2s(zx, zz)); break; }
                    }
                }
        }

        // "x,z  anything" per line, # comments. zoneList gets the order, set gets membership.
        private static bool ReadZoneFile(string path, List<Vector2s> zoneList, HashSet<Vector2s> set, out string error, Action<string> header = null)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "not found: " + path; return false; }
                bool first = true;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == '#') { if (first) header?.Invoke(line.TrimStart('#', ' ')); first = false; continue; }
                    first = false;
                    int cut = line.IndexOfAny(new[] { ' ', '\t' });
                    string key = cut > 0 ? line.Substring(0, cut) : line;
                    string[] xz = key.Split(',');
                    if (xz.Length != 2 || !int.TryParse(xz[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                        || !int.TryParse(xz[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
                    { error = $"bad line '{raw}' in {path}"; return false; }
                    var id = new Vector2s(x, z);
                    zoneList?.Add(id); set?.Add(id);
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static IEnumerator Run(string mode, List<Vector2s> zones, HashSet<Vector2s> protect, string protectPath, bool apply, Action<string> say)
        {
            s_running = true;
            var zs = ZoneSystem.instance; var wg = WorldGenerator.instance; var gen = BcMigratedWorld.Generator;
            string world = ZNet.instance.GetWorldName();
            string stamp = $"BcImport active {BcMigratedWorld.Active}, settings v{gen?.Settings.Version}, seed {wg.GetSeed()}, FAT {FatVersion}";
            bool doVeg = mode != "loc", doLoc = mode != "veg";
            var vegHashes = new HashSet<int>();
            var vegNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in zs.m_vegetation) if (v.m_prefab != null && v.m_enable) vegNames.Add(v.m_prefab.name);
            foreach (var v in zs.m_vegetation) if (v.m_prefab != null && (s_prefabFilter == null || s_prefabFilter.Contains(v.m_prefab.name))) vegHashes.Add(v.m_prefab.name.GetStableHashCode());
            var extraHashes = new HashSet<int>();   // prefab= names that are NOT vegetation: removed only
            if (s_prefabFilter != null) foreach (string n in s_prefabFilter) if (!vegNames.Contains(n)) extraHashes.Add(n.GetStableHashCode());
            // 0.2.1355 (Fire 11-03: "resets should not ignore _bal veg they should include it"; the 11-03 02:46 report left 353 Pickable_Straw_bal, 37 Willow_bal and more): the world's vegetation list no longer holds them, so the list could never remove them. Removed only, the list does not place them back. Only trees, destructibles, pickables and mine rocks count ([saddles]: the 01:07 report also holds spawners, hives, Waystone_bal, webs and sfx); creatures, drops, pieces, containers and spawners stay, and so does anything named by keep=.
            var balHashes = new HashSet<int>(); int balKept = 0;
            if (s_prefabFilter == null && ZNetScene.instance != null)
                foreach (GameObject bp in ZNetScene.instance.m_prefabs)
                    if (bp != null && IsBalVegetation(bp))
                    {
                        if (BalKept(bp.name)) balKept++;
                        else balHashes.Add(bp.name.GetStableHashCode());
                    }
            if (s_prefabFilter == null) say($"{Command}: {balHashes.Count} _bal vegetation prefab(s) count as vegetation, {balKept} kept as pickups or ore (keep=a,b adds, include=a,b takes them).");
            var leftByPrefab = new Dictionary<string, int>();
            s_vegSkipped = 0;
            var requested = new HashSet<Vector2s>();
            var vegAll = new HashSet<int>(); foreach (var v in zs.m_vegetation) if (v.m_prefab != null) vegAll.Add(v.m_prefab.name.GetStableHashCode());
            int keptLive = 0;
            int skippedNoLoc = 0;
            int wildGone = 0, dropsGone = 0, tamesKept = 0;
            var remStep = new int[3]; var remRun = new int[3]; var plStep = new int[3]; var plRun = new int[3];
            int vegSkipStep = 0, regenCount = 0; bool firstCheckSaid = false;
            string[] fam = { "other", "vegetation", "location objects" };
            int zoneCtrl = "_ZoneCtrl".GetStableHashCode(), player = "Player".GetStableHashCode(), tomb = "Player_tombstone".GetStableHashCode();
            var report = new StringBuilder();
            // 0.2.1238 ([lead]: a killed run left no list): the list is rewritten every 1% to a _live file, so a stopped run keeps its evidence.
            string livePath = null;
            try { string ld = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census"); Directory.CreateDirectory(ld); livePath = Path.Combine(ld, $"regen_{mode}_{(apply ? "apply" : "dry")}_{DateTime.Now:yyyyMMdd_HHmmss}_live.txt"); } catch { }
            report.AppendLine($"# {Command} {mode} {(apply ? "APPLY" : "dry run")} '{world}' {DateTime.Now:yyyy-MM-dd HH:mm}: {stamp}; protect {protectPath}");
            report.AppendLine("# zone x,z  result  removed  (prefab counts)");
            report.AppendLine($"# vegetation prefabs on this world ({vegNames.Count}): {string.Join(",", vegNames)}");
            // 0.2.1230 (Fire: "make sure were using our locations from our location yaml"): the location set the run uses, and where it came from.
            if (doLoc)
            {
                int types = 0, enabled = 0; foreach (var l in zs.m_locations) { types++; if (l.m_enable) enabled++; }
                string ewdDir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "expand_world");
                string[] ewd = System.IO.Directory.Exists(ewdDir) ? System.IO.Directory.GetFiles(ewdDir, "expand_locations*.yaml") : new string[0];
                for (int i = 0; i < ewd.Length; i++) ewd[i] = System.IO.Path.GetFileName(ewd[i]);
                string setLine = $"{Command} loc: location set: {types} type(s) in ZoneSystem.m_locations ({enabled} enabled) as loaded at world start, EWD yaml present: {(ewd.Length > 0 ? string.Join(", ", ewd) : "none")}; {zs.m_locationInstances.Count:N0} instance(s) in this world - the regen re-places EXISTING instances at their positions with these definitions; it does not add or move instances";
                say(setLine); report.AppendLine("# " + setLine);
            }
            if (s_prefabFilter != null) report.AppendLine($"# prefab filter: {string.Join(",", s_prefabFilter)} (re-placed: vegetation among them; removed only: {extraHashes.Count} non-vegetation)");
            var removedByPrefab = new Dictionary<string, int>();
            var pastPurge = new List<ZDO>(); var orphanGone = new Dictionary<string, int>(); int replanPlaced = 0, replanWaiting = 0, replanProtected = 0;
            var wrongStayed = new Dictionary<string, int>(); var wrongByType = new Dictionary<string, int>(); var balLeft = new Dictionary<string, int>(); var pastTop = new Dictionary<string, int>(); int pastProt = 0, pastReset = 0;
            int done = 0, skippedProt = 0, skippedSelf = 0, skippedLoaded = 0, skippedIh = 0, skippedNever = 0, skippedKept = 0, failed = 0, removedTotal = 0, locsRedone = 0;
            var frame = System.Diagnostics.Stopwatch.StartNew();
            var gathered = new List<ZDO>(); var seen = new HashSet<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>();
            var doomed = new List<ZDO>();
            var hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
            // 0.2.1223 (Fire: "without the debug logging shown it would just give a progress update once every 1% until done and then a
            // printout at the end"): per-zone lines only with "Regen verbose logging"; otherwise one progress line per 1% and the summary.
            bool verbose = Verbose; s_quietTrace = !verbose;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            s_regenAt = "start"; Game.instance.StartCoroutine(RegenBeat());
            int idx = 0, lastPct = 0, total = zones.Count;
            // 0.2.1225 (regen speed-up step 0, Fire 10-29 "All of it"): where a regenerated zone's time goes, summed per 1% step and for the run.
            var tw = new System.Diagnostics.Stopwatch();
            double msScan = 0, msWait = 0, msRemove = 0, msSpawn = 0, runScan = 0, runWait = 0, runRemove = 0, runSpawn = 0;
            int waitFrames = 0, stepZones = 0, runFrames = 0;

            foreach (Vector2s zone in zones)
            {
                // 0.2.1230 speed-up step 1: ask the builder for the next zones' terrain now, so it is ready when their turn comes
                if (apply)
                    for (int k = idx; k < Math.Min(zones.Count, idx + Lookahead); k++)
                    {
                        var nz = zones[k];
                        if (requested.Contains(nz) || protect.Contains(nz) || !GeneratedRef(zs).Contains(nz)) continue;
                        requested.Add(nz);
                        HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(nz), hm.m_width, hm.m_scale, hm.IsDistantLod, wg);
                    }
                int pct = total > 0 ? (int)(100L * idx / total) : 100; idx++;
                if (pct > lastPct)
                {
                    lastPct = pct;
                    if (livePath != null) { try { File.WriteAllText(livePath, report.ToString()); } catch { } }
                    double eta = clock.Elapsed.TotalSeconds / Math.Max(1, idx - 1) * (total - idx + 1);
                    say($"{Command} {mode}: {pct}% ({idx - 1:N0} / {total:N0} zones, {removedTotal:N0} objects {(apply ? "removed" : "to remove")}, ETA ~{(eta >= 90 ? $"{eta / 60:0} min" : $"{eta:0} s")})"
                        + (stepZones > 0 ? $"; per regenerated zone: scan {msScan / stepZones:0.0} ms, terrain wait {msWait / stepZones:0.0} ms ({(float)waitFrames / stepZones:0.0} frame(s)), remove {msRemove / stepZones:0.0} ms, SpawnZone {msSpawn / stepZones:0.0} ms over {stepZones} zone(s)" : "; no zone regenerated in this step")
                        + $"; this step {(apply ? "removed" : "would remove")} vegetation {remStep[1]:N0} / location objects {remStep[2]:N0} / other (zone control, wild creatures, drops) {remStep[0]:N0}" + (apply ? $", placed vegetation {plStep[1]:N0} / location objects {plStep[2]:N0} / other {plStep[0]:N0}" + (doLoc && !doVeg ? $", PlaceVegetation skipped {vegSkipStep:N0}" : "") : ""));
                    // 0.2.1235: a loc step that removes far more location objects than it places (beyond RandomSpawn rolls) is a STOP
                    if (apply && doLoc && (doVeg ? plRun[1] + plStep[1] > (remRun[1] + remStep[1]) * 1.2 + 20 : plStep[1] > remStep[1] * 1.2 + 20)) {   // 0.2.1356: both/dn read running totals (r39: 326 -> 445 in a step, 14,077 -> 10,746 overall)
                         FiresLogger.LogWarning($"[BcImport] {Command} {mode}: WARNING vegetation removed {remStep[1]:N0} but placed {plStep[1]:N0} (doubling or loss) - STOP the run"); say($"WARNING {Command} {mode}: vegetation removed {remStep[1]:N0}, placed {plStep[1]:N0} - STOP the run"); }
                    for (int f = 0; f < 3; f++) { remRun[f] += remStep[f]; plRun[f] += plStep[f]; remStep[f] = 0; plStep[f] = 0; }
                    vegSkipStep = 0;
                    runScan += msScan; runWait += msWait; runRemove += msRemove; runSpawn += msSpawn; runFrames += waitFrames;
                    msScan = msWait = msRemove = msSpawn = 0; waitFrames = 0; stepZones = 0;
                }
                if (WorldGenerator.instance != wg || ZNet.instance == null) break;
                if (OtherPeers(s_requester) != 0) { report.AppendLine("# stopped: a peer connected"); say($"{Command}: STOPPED - another peer connected ({done} zone(s) done)"); break; }
                string tag = $"{zone.x},{zone.y}";
                s_regenAt = $"zone {tag} scan ({idx:N0} / {total:N0})";
                bool pastEdge = zc0Past(zone);
                if (protect.Contains(zone))
                {
                    skippedProt++; report.AppendLine(tag + "  skipped-protected");
                    if (pastEdge) pastProt++;
                    if (s_worldRun)
                    {
                        var kept = new List<ZDO>(); var keptSeen = new HashSet<ZoneSystem.SectorIndex>();
                        FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { zone, kept, keptSeen });
                        foreach (ZDO kz in kept) { string kn = ZNetScene.instance?.GetPrefab(kz.GetPrefab())?.name; if (kn != null && kn.EndsWith("_bal", StringComparison.Ordinal)) balLeft[kn] = balLeft.TryGetValue(kn, out int kc) ? kc + 1 : 1; }
                    }
                    continue;
                }
                // 0.2.1222 (the full-world veg run generated ~20k zones nobody had visited): regenerate ONLY zones the world already generated;
                // an ungenerated zone is left for the game to generate normally when a player first comes near.
                if (!GeneratedRef(zs).Contains(zone)) { skippedNever++; report.AppendLine(tag + "  skipped: never generated"); continue; }
                // A generated zone whose location instance is still unplaced would get it placed by SpawnZone: never, in any mode.
                if (zs.m_locationInstances.TryGetValue(zone, out var pend) && !pend.m_placed && pend.m_location != null) { skippedNever++; report.AppendLine(tag + "  skipped: unplaced location (the regen never places a new one)"); continue; }
                if (zs.IsZoneLoaded(zone)) { skippedLoaded++; report.AppendLine(tag + "  skipped-loaded (zone active; move away)"); continue; }
                // 0.2.1366 (Fire 11-03: "if the zones are far past the edge and not a build yard they should not be regenerated only removed"): stays in the generated set, so vanilla never fills it again.
                if (s_worldRun && pastEdge)
                {
                    gathered.Clear(); visited.Clear();
                    FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { zone, gathered, visited });
                    var pastGoes = new List<ZDO>(); bool pastBuilt = false; string pastWhy = null;
                    foreach (ZDO z in gathered)
                    {
                        if (ZoneSystem.GetZone(z.GetPosition()) != zone) continue;
                        if (z.GetLong(ZDOVars.s_creator, 0L) != 0 || IsInfinityHammered(z)) { pastBuilt = true; pastWhy = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString(CultureInfo.InvariantCulture); break; }
                        int ph = z.GetPrefab();
                        if (ph != player && ph != tomb) pastGoes.Add(z);
                    }
                    if (pastBuilt) { skippedSelf++; report.AppendLine(tag + "  past-edge kept: built object " + pastWhy); continue; }
                    foreach (ZDO z in pastGoes)
                    {
                        string pn = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString(CultureInfo.InvariantCulture);
                        pastTop[pn] = pastTop.TryGetValue(pn, out int pc2) ? pc2 + 1 : 1;
                        removedByPrefab[pn] = removedByPrefab.TryGetValue(pn, out int pt2) ? pt2 + 1 : 1;
                    }
                    pastReset++; done++; removedTotal += pastGoes.Count;
                    if (apply) pastPurge.AddRange(pastGoes);
                    report.AppendLine($"{tag}  past-edge removed {pastGoes.Count}");
                    if (frame.ElapsedMilliseconds >= FrameBudgetMs) { yield return null; frame.Restart(); }
                    continue;
                }

                tw.Restart();
                bool hasLoc = zs.m_locationInstances.TryGetValue(zone, out var inst) && inst.m_placed && inst.m_location != null;
                // 0.2.1236 (Fire: "locations can replace the veg that they removed thats fine"): in loc mode a zone whose location is reset is
                // reset as a whole - its creator-0 vegetation goes too and vanilla's SpawnZone places the location (with its clear area) AND the
                // zone's vegetation once: nothing lost, nothing doubled. A loc-mode zone with no placed location has nothing to reset: skipped.
                if (doLoc && !doVeg && !hasLoc) { skippedNoLoc++; report.AppendLine(tag + "  skipped: no placed location"); continue; }
                bool zoneVeg = doVeg || (doLoc && hasLoc);
                Vector3 zc = ZoneSystem.GetZonePos(zone);
                float locR = hasLoc ? Mathf.Max(inst.m_location.m_exteriorRadius, inst.m_location.m_interiorRadius) : 0f;
                gathered.Clear(); seen.Clear(); visited.Clear();
                int reach = doLoc && hasLoc ? 1 : 0;   // a location's radius may cross into the next zones
                for (int dx = -reach; dx <= reach; dx++)
                    for (int dz = -reach; dz <= reach; dz++)
                        FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { new Vector2s(zone.x + dx, zone.y + dz), gathered, visited });

                doomed.Clear(); bool selfProt = false, ihProt = false; long creatorSeen = 0; string ihName = null;
                foreach (ZDO z in gathered)
                {
                    if (!seen.Add(z)) continue;
                    long creator = z.GetLong(ZDOVars.s_creator, 0L);
                    Vector3 p = z.GetPosition();
                    bool inZone = ZoneSystem.GetZone(p) == zone;
                    if (creator != 0 && inZone) { if (s_ignoreProtect) { s_builtIgnored++; s_builtZones.Add(zone); continue; } selfProt = true; creatorSeen = creator; break; }
                    if (creator != 0) continue;
                    // 0.2.1388 (Fire: "it should never double shrines"): a location that is placed again takes away everything it contains first, by what it contains, invulnerable pieces
                    // and vegetation-list prefabs too, in the next zone too. Before, realbuilds left those standing and the new location went on top.
                    if (doLoc && hasLoc && z.GetPrefab() != player && z.GetPrefab() != tomb && !s_neverRemoved.Contains(z.GetPrefab()) && !IsLiveThing(z.GetPrefab())
                        && !protect.Contains(ZoneSystem.GetZone(p)) && Zones.BiomePaint.IsLocationPiece(zs, inst.m_location, inst.m_position, p, z.GetPrefab()))
                    {
                        s_locationPiecesGone++;
                        doomed.Add(z);
                        continue;
                    }
                    if (IsInfinityHammered(z))   // Fire 10-28: creator-0 + Infinity Hammer invulnerable = an admin-built structure
                    {
                        // 0.2.1355: a location's own hardened props (Infinity Hammer blueprints carry the tool tier, 51 on Location_StoneCircleToadstool) would double when the location is placed again. Pieces stay.
                        if (s_ignoreProtect && doLoc && hasLoc && IsHardenedProp(z) && new Vector2(p.x - inst.m_position.x, p.z - inst.m_position.z).magnitude <= locR) { doomed.Add(z); continue; }
                        if (s_ignoreProtect) { if (inZone) { s_builtIgnored++; s_builtZones.Add(zone); } continue; }
                        if (s_realBuilds && OwnedByLocation(zs, p, zone)) { if (inZone) s_locationOwned++; continue; }
                        if (inZone) { ihProt = true; ihName = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString(CultureInfo.InvariantCulture); break; }
                        continue;   // never removed, even in a neighbouring zone inside a location radius
                    }
                    int h = z.GetPrefab();
                    if (h == player || h == tomb) continue;
                    if (s_noReroll != null && s_noReroll.Contains(h)) continue;   // 0.2.1384
                    // 0.2.1384 (copy test 11-04: Ashlands_Pillar4 25,181 -> 5,297): a location builds with prefabs that are also on the vegetation list, and a veg reset took them with nothing to put them back.
                    if (s_realBuilds && inZone && doVeg && !doLoc && vegHashes.Contains(h) && OwnedByLocation(zs, p, zone)) { s_locationVegKept++; continue; }
                    if (inZone && zoneVeg && (vegHashes.Contains(h) || extraHashes.Contains(h) || h == zoneCtrl)) { doomed.Add(z); continue; }
                    if (inZone && doVeg && balHashes.Contains(h) && !(hasLoc && new Vector2(p.x - inst.m_position.x, p.z - inst.m_position.z).magnitude <= locR)) { doomed.Add(z); continue; }
                    if (inZone && doVeg && s_orphanHashes != null && s_orphanHashes.Contains(h) && p.y <= 1000f && OrphanProp(zs, p))
                    {
                        string on = ZNetScene.instance?.GetPrefab(h)?.name ?? h.ToString(CultureInfo.InvariantCulture);
                        orphanGone[on] = orphanGone.TryGetValue(on, out int oc) ? oc + 1 : 1;
                        doomed.Add(z); continue;
                    }
                    if (inZone && doVeg && s_prefabFilter == null && !(hasLoc && new Vector2(p.x - inst.m_position.x, p.z - inst.m_position.z).magnitude <= locR))
                    {
                        string ln = ZNetScene.instance?.GetPrefab(h)?.name ?? h.ToString(CultureInfo.InvariantCulture);
                        leftByPrefab[ln] = leftByPrefab.TryGetValue(ln, out int lc) ? lc + 1 : 1;   // creator 0, not vegetation, not a location: left in place
                    }
                    if (inZone && h == zoneCtrl) { doomed.Add(z); continue; }   // SpawnZone places a fresh one
                    // 0.2.1235 (the 02:47 loc apply removed vegetation, creatures and drops inside every location radius and placed none back):
                    // loc mode removes only what a location itself spawns - never vegetation, characters or item drops.
                    if (s_neverRemoved.Contains(h)) continue;   // 0.2.1383
                    if (doLoc && hasLoc && !protect.Contains(ZoneSystem.GetZone(p))
                        && new Vector2(p.x - inst.m_position.x, p.z - inst.m_position.z).magnitude <= locR)
                    {
                        if (vegAll.Contains(h)) { keptLive++; continue; }
                        // 0.2.1237 (Fire: "dropped items, creatures and etc within the location footprint should be cleaned up and then replaced
                        // with the location"): loose item drops and WILD creatures in the footprint go; tamed animals, companions and anything
                        // following someone stay.
                        if (IsLiveThing(h))
                        {
                            var lp = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
                            bool drop = lp != null && lp.GetComponent<ItemDrop>() != null && lp.GetComponent<Character>() == null;
                            bool keep = !drop && (z.GetBool(ZDOVars.s_tamed) || z.GetString(ZDOVars.s_follow, "").Length > 0
                                || (lp != null && lp.name.IndexOf("Companion", StringComparison.OrdinalIgnoreCase) >= 0) || lp == null || lp.GetComponent<Player>() != null);
                            if (keep) { tamesKept++; continue; }
                            if (drop) dropsGone++; else wildGone++;
                        }
                        doomed.Add(z);
                        continue;
                    }
                }
                if (selfProt) { skippedSelf++; report.AppendLine($"{tag}  self-protected (creator {creatorSeen})"); continue; }
                if (ihProt) { skippedIh++; report.AppendLine($"{tag}  skipped: IH creator-0 structure ({ihName})"); continue; }
                // 0.2.1228 (Fire: "location resets should ignore boss temples that are built around as well"): a location whose footprint
                // (its radius + 16 m) touches a protected zone or holds ANY player-built or admin (IH / Infinity Tools) object - in this zone OR
                // a neighbour - is kept entirely: nothing removed, nothing re-placed. Every location, not only bosses (a built-around trader or
                // start temple is the same case).
                if (doLoc && hasLoc)
                {
                    float keepR = locR + 16f;
                    int built = 0; bool protTouch = false;
                    float ownReach = Zones.BiomePaint.LocationReach(zs, inst.m_location);   // 0.2.1388: a blueprint piece just past the yaml's radius is still the location's
                    for (int dx = -2; dx <= 2 && !protTouch; dx++)
                        for (int dz = -2; dz <= 2 && !protTouch; dz++)
                        {
                            var nz = new Vector2s(zone.x + dx, zone.y + dz);
                            if (!protect.Contains(nz)) continue;
                            Vector3 nc = ZoneSystem.GetZonePos(nz);
                            float ex = Mathf.Max(0f, Mathf.Abs(inst.m_position.x - nc.x) - 32f), ez = Mathf.Max(0f, Mathf.Abs(inst.m_position.z - nc.z) - 32f);
                            if (ex * ex + ez * ez <= ownReach * ownReach) protTouch = true;   // 0.2.1229: the location's own objects (its radius) would land in a protected zone
                        }
                    var counted = new HashSet<ZDO>();
                    foreach (ZDO z in gathered)
                    {
                        if (!counted.Add(z)) continue;
                        Vector3 p = z.GetPosition();
                        if (new Vector2(p.x - inst.m_position.x, p.z - inst.m_position.z).sqrMagnitude > keepR * keepR) continue;
                        if (z.GetLong(ZDOVars.s_creator, 0L) != 0) built++;
                        else if (IsInfinityHammered(z) && !Zones.BiomePaint.IsLocationPiece(zs, inst.m_location, inst.m_position, p, z.GetPrefab())) built++;   // 0.2.1388: its own pieces are not a build around it
                    }
                    // 0.2.1229 (Fire: "Only keep built-around"): kept when pieces stand within radius + 16 m; a protected zone near it no longer keeps it,
                    // unless re-placing would put the location's own objects (within its radius) INTO a protected zone - then it is kept as well.
                    if (!s_ignoreProtect && (protTouch || built > 0))
                    {
                        skippedKept++;
                        report.AppendLine(built > 0
                            ? $"{tag}  kept: location {inst.m_location.m_prefabName} built around ({built} piece(s) within {keepR:0} m)"
                            : $"{tag}  kept: location {inst.m_location.m_prefabName} - its {locR:0} m footprint reaches a protected zone (re-placing would put objects there)");
                        continue;
                    }
                }

                // 0.2.1356 (Fire 11-03, Mistlands structures in Deep North): an instance planned on an older map keeps its spot; one whose location does not allow the biome now at that spot goes and comes back only through genloc.
                bool wrongBiome = false;
                if (doLoc && hasLoc && inst.m_location.m_biome != Heightmap.Biome.None)
                {
                    Heightmap.Biome hereBiome = WorldGenerator.instance.GetBiome(inst.m_position.x, inst.m_position.z);
                    if ((hereBiome & inst.m_location.m_biome) == 0)
                    {
                        string wn = LocName(inst.m_location);
                        if ((hereBiome & s_wrongScope) != 0) { wrongBiome = true; wrongByType[wn] = wrongByType.TryGetValue(wn, out int wc) ? wc + 1 : 1; }
                        else wrongStayed[wn] = wrongStayed.TryGetValue(wn, out int ws) ? ws + 1 : 1;   // out of scope: re-placed at its spot, still counts for the top-up
                    }
                }

                if (s_replan != Heightmap.Biome.None && doLoc && hasLoc && !wrongBiome && inst.m_location.m_biome == s_replan) { wrongBiome = true; replanPlaced++; }   // goes like a wrong-biome one; the top-up plans it again

                if (pastEdge) { pastReset++; foreach (ZDO pz in doomed) { string pn = ZNetScene.instance?.GetPrefab(pz.GetPrefab())?.name ?? pz.GetPrefab().ToString(CultureInfo.InvariantCulture); pastTop[pn] = pastTop.TryGetValue(pn, out int pc) ? pc + 1 : 1; } }

                var counts = new Dictionary<string, int>();
                foreach (ZDO z in doomed)
                {
                    string n = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString(CultureInfo.InvariantCulture);
                    counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
                    removedByPrefab[n] = removedByPrefab.TryGetValue(n, out int t) ? t + 1 : 1;
                }
                var sb = new StringBuilder();
                foreach (var kv in counts) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(' ');
                string locNote = hasLoc && doLoc ? $" location {inst.m_location.m_prefabName}" : "";
                double scanMs = tw.Elapsed.TotalMilliseconds;

                if (!apply) { done++; removedTotal += doomed.Count; foreach (ZDO dz in doomed) { int dh = dz.GetPrefab(); remStep[dh == zoneCtrl || IsLiveThing(dh) ? 0 : (vegHashes.Contains(dh) || extraHashes.Contains(dh) || balHashes.Contains(dh)) ? 1 : 2]++; } report.AppendLine($"{tag}  would-regen{locNote}  {doomed.Count}  ({sb.ToString().TrimEnd()})"); }
                else
                {
                    // Terrain first, so a slow build never leaves a zone emptied and ungenerated.
                    s_regenAt = $"zone {tag} terrain wait ({idx:N0} / {total:N0})";
                    float until = Time.realtimeSinceStartup + TerrainWaitSeconds;
                    tw.Restart(); int frames = 0;
                    while (!HeightmapBuilder.instance.IsTerrainReady(zc, hm.m_width, hm.m_scale, hm.IsDistantLod, wg) && Time.realtimeSinceStartup < until) { frames++; yield return null; }
                    double waitMs = tw.Elapsed.TotalMilliseconds;
                    if (!HeightmapBuilder.instance.IsTerrainReady(zc, hm.m_width, hm.m_scale, hm.IsDistantLod, wg))
                    { failed++; report.AppendLine(tag + "  FAILED terrain not ready in 30 s; nothing removed"); continue; }

                    // 0.2.1239 (proof run 03:23: 3 zones "FAILED SpawnZone returned false after removing 159 / 156 / 79"): vanilla SpawnZone refuses
                    // a zone whose unplaced location prefab is still loading (PokeCanSpawnLocation starts the async load and says false until it is
                    // in), so the location must be loaded BEFORE anything is removed - otherwise the zone is left emptied.
                    if (hasLoc && doLoc && !wrongBiome && PokeMethod != null)
                    {
                        s_regenAt = $"zone {tag} location load ({idx:N0} / {total:N0})";
                        float pokeUntil = Time.realtimeSinceStartup + TerrainWaitSeconds;
                        while (!(bool)PokeMethod.Invoke(zs, new object[] { inst.m_location, true }) && Time.realtimeSinceStartup < pokeUntil) yield return null;
                        if (!(bool)PokeMethod.Invoke(zs, new object[] { inst.m_location, true }))
                        { failed++; report.AppendLine(tag + "  FAILED location prefab not loaded in 30 s; nothing removed"); continue; }
                    }
                    tw.Restart();
                    long me = ZDOMan.GetSessionID();
                    foreach (ZDO z in doomed)
                    {
                        int dh = z.GetPrefab();
                        remStep[dh == zoneCtrl || IsLiveThing(dh) ? 0 : (vegHashes.Contains(dh) || extraHashes.Contains(dh) || balHashes.Contains(dh)) ? 1 : 2]++;
                        if (!z.IsOwner()) z.SetOwner(me);
                        Zones.BiomePaint.DisarmRespawn(z);   // 0.2.1390: or Expand World Prefabs brings a marked object back on top of the new location
                        ZDOMan.instance.DestroyZDO(z);
                    }
                    removedTotal += doomed.Count;
                    if (hasLoc && doLoc) { if (wrongBiome) zs.m_locationInstances.Remove(zone); else { inst.m_placed = false; zs.m_locationInstances[zone] = inst; locsRedone++; } }
                    double removeMs = tw.Elapsed.TotalMilliseconds;
                    GeneratedRef(zs).Remove(zone);
                    tw.Restart();
                    s_vegOnlyZone = zone; s_vegOnlyActive = hasLoc && !doLoc;
                    s_skipVegActive = doLoc && !zoneVeg;
                    FilterVegetation(zs);
                    int skippedBefore = s_vegSkipped;
                    bool ok;
                    try
                    {
                        s_placed[0] = s_placed[1] = s_placed[2] = 0; s_phase = 0; s_counting = true;
                        s_zoneCreated = new List<ZDO>();
                        var a = new object[] { zone, ZoneSystem.SpawnMode.Ghost, null };
                        ok = (bool)SpawnZoneMethod.Invoke(zs, a);
                    }
                    catch (Exception ex) { ok = false; if (verbose) FiresLogger.LogWarning($"[BcImport] {Command}: zone {tag} SpawnZone threw {ex.InnerException?.Message ?? ex.Message}"); }
                    finally { s_counting = false; for (int f = 0; f < 3; f++) plStep[f] += s_placed[f]; vegSkipStep += s_vegSkipped - skippedBefore; s_vegOnlyActive = false; s_skipVegActive = false; if (s_savedVeg != null) { zs.m_vegetation = s_savedVeg; s_savedVeg = null; } }
                    double spawnMs = tw.Elapsed.TotalMilliseconds;
                    msScan += scanMs; msWait += waitMs; msRemove += removeMs; msSpawn += spawnMs; waitFrames += frames; stepZones++;
                    // 0.2.1239: a false here is vanilla's "not ready yet" (terrain or the location prefab); keep asking for up to 30 s rather than
                    // leave an emptied zone.
                    s_regenAt = $"zone {tag} spawn ({idx:N0} / {total:N0})";
                    for (float retryUntil = Time.realtimeSinceStartup + TerrainWaitSeconds; !ok && Time.realtimeSinceStartup < retryUntil;)
                    {
                        yield return null;
                        s_vegOnlyZone = zone; s_skipVegActive = doLoc && !zoneVeg; s_counting = true; FilterVegetation(zs);
                        try { ok = (bool)SpawnZoneMethod.Invoke(zs, new object[] { zone, ZoneSystem.SpawnMode.Ghost, null }); }
                        catch { ok = false; }
                        finally { s_counting = false; for (int f = 0; f < 3; f++) plStep[f] += s_placed[f]; s_placed[0] = s_placed[1] = s_placed[2] = 0; s_skipVegActive = false; if (s_savedVeg != null) { zs.m_vegetation = s_savedVeg; s_savedVeg = null; } }
                    }
                    if (!ok)
                    {
                        GeneratedRef(zs).Add(zone);   // never leave it to regenerate on its own later without the run's checks
                        failed++; report.AppendLine($"{tag}  FAILED SpawnZone returned false after removing {doomed.Count}");
                        if (verbose) FiresLogger.LogWarning($"[BcImport] {Command}: zone {tag} removed {doomed.Count} but SpawnZone failed; zone left marked generated");
                        continue;
                    }
                    if (++regenCount == 50 && !firstCheckSaid)
                    {
                        firstCheckSaid = true;
                        int pv = plRun[1] + plStep[1], pl = plRun[2] + plStep[2], rv = remRun[1] + remStep[1], rl = remRun[2] + remStep[2];
                        bool bad = (doLoc && pv > rv * 1.2 + 20) || (doVeg && !doLoc && pl > 0);   // 0.2.1240: loc mode resets whole zones since 1236 - only vegetation DOUBLING is wrong there
                        string chk = $"{Command} {mode}: check after the first 50 regenerated zones - vegetation removed {rv:N0} / placed {pv:N0}, location objects removed {rl:N0} / placed {pl:N0}";
                        if (bad) { FiresLogger.LogWarning("[BcImport] " + chk + " - WARNING: vegetation doubled, or location objects placed in veg mode (stop the run)"); say("WARNING " + chk + " - vegetation doubled, or location objects placed in veg mode: STOP the run"); }
                        else say(chk + " - OK for this mode");
                    }
                    done++;
                    var placedBy = new Dictionary<string, int>();
                    if (s_zoneCreated != null) foreach (ZDO cz in s_zoneCreated) { string pn = ZNetScene.instance?.GetPrefab(cz.GetPrefab())?.name ?? cz.GetPrefab().ToString(CultureInfo.InvariantCulture); placedBy[pn] = placedBy.TryGetValue(pn, out int pc2) ? pc2 + 1 : 1; }
                    var pb = new StringBuilder(); foreach (var kv2 in placedBy) pb.Append(kv2.Key).Append(':').Append(kv2.Value).Append(' ');
                    s_zoneCreated = null;
                    // 0.2.1390: a blueprint location is not always placed through a proxy, so this would stop the run on a shrine that came back fine.
                    if (hasLoc && doLoc && !wrongBiome && !placedBy.ContainsKey("LocationProxy") && !Zones.BiomePaint.IsBlueprintLocation(zs, inst.m_location))
                    {
                        FiresLogger.LogWarning($"[BcImport] {Command} {mode}: WARNING zone {tag}: location {LocName(inst.m_location)} was removed but NOT placed back - STOP the run");
                        say($"WARNING {Command} {mode}: zone {tag} lost its location {LocName(inst.m_location)} - STOP the run");
                    }
                    report.AppendLine($"{tag}  regenerated{locNote}  removed {doomed.Count} ({sb.ToString().TrimEnd()})  placed {placedBy.Values.Sum()} ({pb.ToString().TrimEnd()})");
                    if (verbose) FiresLogger.LogDebug($"[BcImport] {Command}: zone {tag} generated with {stamp}; removed {doomed.Count}{locNote}");
                }
                if (frame.ElapsedMilliseconds >= FrameBudgetMs) { yield return null; frame.Restart(); }   // 0.2.1230 step 2: 30 ms on a dedicated server
            }

            // Vanilla 1.0 reloads an emptied save chunk's old file, so a plain DestroyZDO would bring these back: same purge as the off-map controllers.
            if (pastPurge.Count > 0) say($"{Command} world: past-edge remove-only: {Zones.ZoneModePark.PurgeFromSave(pastPurge):N0} of {pastPurge.Count:N0} object(s) out of the world and its save.");
            string path;
            try
            {
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, $"regen_{mode}_{(apply ? "apply" : "dry")}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                report.AppendLine("# left in place (creator 0, not vegetation, not inside a location):");
                foreach (var kv in leftByPrefab) report.AppendLine($"#   {kv.Key} {kv.Value}");
                report.AppendLine("# removed by prefab:");
                foreach (var kv in removedByPrefab) report.AppendLine($"#   {kv.Key} {kv.Value}");
                File.WriteAllText(path, report.ToString());
            }
            catch (Exception ex) { path = "(not written: " + ex.Message + ")"; }
            say($"{Command} {mode} {(apply ? "APPLY" : "dry run")} '{world}': {done} zone(s) {(apply ? "regenerated" : "would regenerate")}, "
                + $"{removedTotal} ZDO(s) {(apply ? "removed" : "to remove")}, {locsRedone} location(s) re-placed; skipped {skippedProt} protected, {skippedSelf} self-protected, {skippedIh} IH creator-0 structure, {skippedKept} location(s) kept (built around), "
                + $"{skippedLoaded} loaded, {skippedNever} never generated (left alone); {failed} failed; {stamp}; list: {path}");
            var top = new List<KeyValuePair<string, int>>(removedByPrefab); top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var topText = new StringBuilder();
            for (int i = 0; i < top.Count && i < 10; i++) topText.Append(i == 0 ? "" : ", ").Append(top[i].Key).Append(' ').Append(top[i].Value.ToString("N0"));
            if (s_worldRun)
            {
                say($"{Command} world: past-edge zones: {pastProt:N0} protected, {pastReset:N0} reset; removed there: {string.Join(", ", pastTop.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key} {k.Value:N0}"))}");
                say($"{Command} world: _bal left standing in protected zones: {balLeft.Values.Sum():N0} ({string.Join(", ", balLeft.OrderByDescending(k => k.Value).Take(20).Select(k => $"{k.Key} {k.Value:N0}"))})");
            }
            if (doLoc) say($"{Command} {mode}: wrong-biome scope {s_wrongScopeName}: {wrongByType.Values.Sum():N0} {(apply ? "removed" : "would go")}, {wrongStayed.Values.Sum():N0} outside the scope stay at their spots ({string.Join(", ", wrongStayed.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key} {k.Value:N0}"))})");
            if (wrongByType.Count > 0)
            {
                string wrongList = string.Join(", ", wrongByType.OrderByDescending(k => k.Value).Take(30).Select(k => $"{k.Key} {k.Value:N0}"));
                say($"{Command} {mode}: wrong-biome locations {(apply ? "removed, not re-placed" : "that would go")}: {wrongByType.Values.Sum():N0} ({wrongList})");
            }
            say($"{Command} {mode} summary: {done:N0} regenerated, {skippedProt:N0} protected, {skippedSelf:N0} player-built, {skippedIh:N0} admin (IH), {skippedKept:N0} location(s) kept (built around), {skippedLoaded:N0} loaded, "
                + $"{skippedNever:N0} never generated, {failed:N0} failed; {removedTotal:N0} objects {(apply ? "removed" : "to remove")} (top: {(topText.Length > 0 ? topText.ToString() : "none")}); "
                + $"{locsRedone:N0} location(s) re-placed; took {clock.Elapsed.TotalMinutes:0.0} min"
                + (done > 0 && apply ? $"; time per regenerated zone: scan {(runScan + msScan) / done:0.0} ms, terrain wait {(runWait + msWait) / done:0.0} ms ({(float)(runFrames + waitFrames) / done:0.0} frame(s)), remove {(runRemove + msRemove) / done:0.0} ms, SpawnZone {(runSpawn + msSpawn) / done:0.0} ms" : ""));
            for (int f = 0; f < 3; f++) { remRun[f] += remStep[f]; plRun[f] += plStep[f]; }
            if (doLoc && !doVeg) say($"{Command} loc: {(apply ? "removed" : "would remove")} wild creatures {wildGone:N0} / item drops {dropsGone:N0} inside location footprints (kept tames / companions / followers {tamesKept:N0})");
            if (doLoc && !doVeg) say($"{Command} loc: {skippedNoLoc:N0} zone(s) skipped (no placed location); each reset zone re-placed its location and its vegetation once");
            if (doLoc && !doVeg) say($"{Command} loc: left in place inside location radii: {keptLive:N0} vegetation / creature / item object(s) (never removed in loc mode)");
            if (apply) say($"{Command} {mode} totals: removed vegetation {remRun[1]:N0} / location objects {remRun[2]:N0} / other {remRun[0]:N0}; placed vegetation {plRun[1]:N0} / location objects {plRun[2]:N0} / other {plRun[0]:N0}");
            if (doLoc) say($"{Command} {mode}: {s_locationPiecesGone:N0} object(s) removed because a location being placed again contains them (0.2.1388)");
            if (s_realBuilds) say($"{Command} {mode}: realbuilds - {s_locationOwned:N0} invulnerable object(s) inside a location radius left standing, their zones not counted as built; {s_locationVegKept:N0} vegetation-list object(s) inside a location radius kept (0.2.1384)");
            if (s_ignoreProtect) say($"{Command}: ignoreprotect - {s_builtIgnored:N0} built or admin object(s) left standing in {s_builtZones.Count:N0} zone(s) the reset entered");
            if (doLoc && !doVeg) say($"{Command} loc: PlaceVegetation skipped for {s_vegSkipped:N0} zone(s) (loc mode never re-places vegetation)");
            if (doVeg && leftByPrefab.Count > 0)
            {
                var left = new List<KeyValuePair<string, int>>(leftByPrefab); left.Sort((x, y) => y.Value.CompareTo(x.Value));
                var lt = new StringBuilder();
                for (int i = 0; i < left.Count && i < 12; i++) lt.Append(i == 0 ? "" : ", ").Append(left[i].Key).Append(' ').Append(left[i].Value.ToString("N0"));
                say($"{Command} {mode}: left in place (creator 0, not vegetation, not a location): {lt} - full list in the list file");
            }
            s_prefabFilter = null;
            s_quietTrace = false;
            // 0.2.1231 (Fire: "add the shrines to our own locations reset"): a loc run then plans the location types that have NO instance yet
            // (new yaml types, e.g. the shrines) and spawns those landing in generated zones - the same dry run / apply.
            if (doLoc && s_replan != Heightmap.Biome.None)
            {
                var waiting = new List<Vector2s>();
                foreach (var kv in zs.m_locationInstances)
                    if (!kv.Value.m_placed && kv.Value.m_location != null && kv.Value.m_location.m_biome == s_replan)
                    { if (protect.Contains(kv.Key)) replanProtected++; else waiting.Add(kv.Key); }
                replanWaiting = waiting.Count;
                if (apply) foreach (Vector2s wz in waiting) zs.m_locationInstances.Remove(wz);
                say($"{Command} {mode}: replan {s_replan}: {replanPlaced:N0} placed + {replanWaiting:N0} waiting {(apply ? "cleared" : "would clear")}, {replanProtected:N0} waiting in protected zones stay. A dry run does not clear them, so its top-up numbers do not show the re-plan.");
            }
            if (s_orphanHashes != null) say($"{Command} {mode}: orphan props outside every location radius + 8 m: {orphanGone.Values.Sum():N0} {(apply ? "removed" : "would go")} ({string.Join(", ", orphanGone.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0}"))})");
            if (doLoc && (s_worldRun || (!doVeg && mode == "loc")))
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var li in zs.m_locationInstances.Values) if (li.m_location != null) { string n = LocName(li.m_location); counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1; }
                var newTypes = new List<ZoneSystem.ZoneLocation>();
                var want = s_chainTypes;
                if (want != null) foreach (var l in zs.m_locations) if (l != null && l.m_enable && l.m_quantity > 0 && AnyName(l, want) && (counts.TryGetValue(LocName(l), out int have2) ? have2 : 0) < l.m_quantity) newTypes.Add(l);   // 0.2.1241: top up to quantity
                if (want == null) say($"{Command} loc: new location types: none requested (add types=a,b or newfrom=<file.yaml> to plan new types)");
                else
                {
                    var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var l in zs.m_locations) if (l != null) { loaded.Add(LocName(l)); if (!string.IsNullOrEmpty(l.m_name)) loaded.Add(l.m_name); if (l.m_prefab.IsValid) loaded.Add(l.m_prefab.Name); }
                    if (FiresLogger.DebugEnabled) { var sb2 = new StringBuilder(); int k2 = 0; foreach (var l in zs.m_locations) { if (l == null || k2++ >= 400) continue; if ((l.m_name ?? "").IndexOf("Shrine", StringComparison.OrdinalIgnoreCase) < 0 && (l.m_prefabName ?? "").IndexOf("Shrine", StringComparison.OrdinalIgnoreCase) < 0) continue; sb2.Append($"[name {l.m_name} prefabName {l.m_prefabName} prefab {(l.m_prefab.IsValid ? l.m_prefab.Name : "invalid")} enabled {l.m_enable} qty {l.m_quantity}] "); } FiresLogger.LogDebug($"[BcImport] {Command} loc: shrine-like location entries: {(sb2.Length > 0 ? sb2.ToString() : "none")}"); }
                    var missing = new List<string>(); foreach (string n in want) if (!loaded.Contains(n)) missing.Add(n);
                    say($"{Command} loc: new types requested {want.Count}{(s_chainNote != null ? " from " + s_chainNote : "")}: {newTypes.Count} loaded and without instances; {missing.Count} NOT loaded by the game{(missing.Count > 0 ? " (" + string.Join(", ", missing.GetRange(0, Math.Min(8, missing.Count))) + (missing.Count > 8 ? ", ..." : "") + ") - is EWD 'Location data' on?" : "")}");
                }
                if (want != null && newTypes.Count > 0)
                {
                    say($"{Command} loc: {newTypes.Count} location type(s) have no instance yet (new types): planning them now ({string.Join(", ", newTypes.ConvertAll(t => LocName(t)).GetRange(0, Math.Min(10, newTypes.Count)))}{(newTypes.Count > 10 ? ", ..." : "")})");
                    yield return RunGenLoc(newTypes, protect, protectPath, apply, say);
                }
            }
            s_running = false; s_requester = 0L;
        }

        // 0.2.1383: take each location type down to its yaml quantity. Unplaced instances go first, then placed ones, in a scattered order. A location built around, one in or reaching a
        // protected zone, one in a loaded zone and anything vanilla needs (boss sites, one-per-world) stays. Only its own objects go: vegetation, creatures, drops, and terrain and water records stay.
        private static IEnumerator RunThin(HashSet<Vector2s> protect, string protectPath, bool apply, int percent, Action<string> say)
        {
            s_running = true;
            var zs = ZoneSystem.instance;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frame = System.Diagnostics.Stopwatch.StartNew();
            string world = ZNet.instance.GetWorldName();
            var report = new StringBuilder();
            report.AppendLine($"# {Command} thin {(apply ? "APPLY" : "dry run")} '{world}' {DateTime.Now:yyyy-MM-dd HH:mm}: FAT {FatVersion}; protect {protectPath}");
            if (percent > 0) report.AppendLine($"# percent {percent}");
            report.AppendLine("# type  quantity  had  unplaced gone  placed gone  objects  short");
            var vegAll = new HashSet<int>();
            foreach (var v in zs.m_vegetation) if (v.m_prefab != null) vegAll.Add(v.m_prefab.name.GetStableHashCode());
            int player = "Player".GetStableHashCode(), tomb = "Player_tombstone".GetStableHashCode();
            var byType = new Dictionary<string, List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>>(StringComparer.OrdinalIgnoreCase);
            var locationOfType = new Dictionary<string, ZoneSystem.ZoneLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in zs.m_locationInstances)
            {
                if (kv.Value.m_location == null) continue;
                string n = LocName(kv.Value.m_location);
                if (!byType.TryGetValue(n, out var list)) { byType[n] = list = new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>(); locationOfType[n] = kv.Value.m_location; }
                list.Add(kv);
            }
            int typesCut = 0, unplacedGone = 0, placedGone = 0, objectsGone = 0, keptBuilt = 0, keptProtected = 0, keptLoaded = 0, keptNeeded = 0, shortTotal = 0;
            var gathered = new List<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>(); var seen = new HashSet<ZDO>(); var doomed = new List<ZDO>();
            long me = ZDOMan.GetSessionID();
            foreach (var pair in byType)
            {
                var location = locationOfType[pair.Key];
                int placedHad = pair.Value.FindAll(kv => kv.Value.m_placed).Count, unplacedHad = pair.Value.Count - placedHad;
                int excess = percent > 0 ? Mathf.RoundToInt(placedHad * percent / 100f) : pair.Value.Count - location.m_quantity;
                int unplacedExcess = percent > 0 ? Mathf.RoundToInt(unplacedHad * percent / 100f) : excess;
                if (excess <= 0 && unplacedExcess <= 0) continue;
                if (Zones.BiomePaint.VanillaNeeds(location, out _)) { keptNeeded += excess + unplacedExcess; continue; }
                // 0.2.1385 (Fire: "The shrines don't need to stay"): shrines are built of invulnerable pieces, so here those inside the radius are the location's own.
                string typeName = LocName(location);
                // 0.2.1386 (Fire: "We want helder's dungeons to stay at the three that they were"): thin only; the biome wipe keeps its own VanillaNeeds list.
                if (typeName.StartsWith("Hildir_", StringComparison.OrdinalIgnoreCase))
                {
                    keptNeeded += excess + unplacedExcess;
                    report.AppendLine($"#kept {typeName} Hildir dungeon, stays at its count");
                    continue;
                }
                typesCut++;
                int had = pair.Value.Count, typeUnplaced = 0, typePlaced = 0, typeObjects = 0;
                foreach (var kv in pair.Value)
                {
                    if (kv.Value.m_placed) continue;
                    if (percent > 0 ? unplacedExcess <= 0 : excess <= 0) continue;
                    if (protect.Contains(kv.Key)) continue;
                    if (apply) zs.m_locationInstances.Remove(kv.Key);
                    typeUnplaced++; unplacedExcess--;
                    if (percent <= 0) excess--;
                }
                var placed = pair.Value.FindAll(kv => kv.Value.m_placed);
                placed.Sort((p, q) => ((p.Key.x * 73856093) ^ (p.Key.y * 19349663)).CompareTo((q.Key.x * 73856093) ^ (q.Key.y * 19349663)));
                float locR = Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius), keepR = locR + 16f;
                foreach (var kv in placed)
                {
                    if (excess <= 0) break;
                    if (WorldGenerator.instance == null || ZNet.instance == null) { s_running = false; yield break; }
                    if (OtherPeers(s_requester) != 0) { say($"{Command} thin: STOPPED - another peer connected"); s_running = false; yield break; }
                    Vector2s zone = kv.Key;
                    Vector3 at = kv.Value.m_position;
                    bool touchesProtected = false;
                    for (int dx = -2; dx <= 2 && !touchesProtected; dx++)
                        for (int dz = -2; dz <= 2 && !touchesProtected; dz++)
                        {
                            var nz = new Vector2s(zone.x + dx, zone.y + dz);
                            if (!protect.Contains(nz)) continue;
                            Vector3 nc = ZoneSystem.GetZonePos(nz);
                            float ex = Mathf.Max(0f, Mathf.Abs(at.x - nc.x) - 32f), ez = Mathf.Max(0f, Mathf.Abs(at.z - nc.z) - 32f);
                            if (ex * ex + ez * ez <= locR * locR) touchesProtected = true;
                        }
                    if (touchesProtected) { keptProtected++; continue; }
                    if (zs.IsZoneLoaded(zone)) { keptLoaded++; continue; }
                    gathered.Clear(); visited.Clear(); seen.Clear(); doomed.Clear();
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { new Vector2s(zone.x + dx, zone.y + dz), gathered, visited });
                    bool built = false;
                    foreach (ZDO z in gathered)
                    {
                        if (!seen.Add(z)) continue;
                        Vector3 p = z.GetPosition();
                        float ox = p.x - at.x, oz = p.z - at.z, d2 = ox * ox + oz * oz;
                        if (d2 > keepR * keepR) continue;
                        if (z.GetLong(ZDOVars.s_creator, 0L) != 0) { built = true; break; }
                        if (IsInfinityHammered(z))
                        {
                            if (!Zones.BiomePaint.IsLocationPiece(zs, location, at, p, z.GetPrefab())) { built = true; break; }   // 0.2.1388: by what the location contains, one method with loc and the paint
                            doomed.Add(z);
                            continue;
                        }
                        if (d2 > locR * locR) continue;
                        int h = z.GetPrefab();
                        if (h == player || h == tomb || s_neverRemoved.Contains(h) || vegAll.Contains(h) || IsLiveThing(h)) continue;
                        doomed.Add(z);
                    }
                    if (built) { keptBuilt++; continue; }
                    if (apply)
                    {
                        foreach (ZDO z in doomed) { if (!z.IsOwner()) z.SetOwner(me); Zones.BiomePaint.DisarmRespawn(z); ZDOMan.instance.DestroyZDO(z); }
                        zs.m_locationInstances.Remove(zone);
                    }
                    typePlaced++; typeObjects += doomed.Count; excess--;
                    report.AppendLine($"#gone {pair.Key} {at.x:0} {at.z:0}");
                    if (frame.ElapsedMilliseconds >= FrameBudgetMs) { yield return null; frame.Restart(); }
                }
                unplacedGone += typeUnplaced; placedGone += typePlaced; objectsGone += typeObjects; shortTotal += excess;
                report.AppendLine($"{pair.Key}  {location.m_quantity}  {had}  {typeUnplaced}  {typePlaced}  {typeObjects}  {excess}");
            }
            string path;
            try
            {
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, $"regen_thin_{(apply ? "apply" : "dry")}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(path, report.ToString());
            }
            catch (Exception ex) { path = "(not written: " + ex.Message + ")"; }
            say($"{Command} thin summary: {typesCut:N0} type(s) over quantity; {placedGone:N0} placed location(s) {(apply ? "removed" : "would go")} with {objectsGone:N0} object(s), {unplacedGone:N0} unplaced; kept {keptBuilt:N0} built around, {keptProtected:N0} in or reaching a protected zone, {keptLoaded:N0} loaded, {keptNeeded:N0} needed by vanilla; {shortTotal:N0} short of the quantity; took {clock.Elapsed.TotalMinutes:0.0} min; list: {path}");
            s_running = false; s_requester = 0L;
        }

        // 0.2.1231: plan new location types, then (apply) spawn those that landed in generated zones. Same frame budget, terrain wait,
        // per-1% removed/placed lines and 50-zone check as the regen. Never places vegetation; a zone that holds player-built or admin
        // objects, or is loaded right now, drops its new instance instead (logged), so nothing lands on a base.
        private static IEnumerator RunGenLoc(List<ZoneSystem.ZoneLocation> types, HashSet<Vector2s> protect, string protectPath, bool apply, Action<string> say)
        {
            s_running = true;
            var zs = ZoneSystem.instance; var wg = WorldGenerator.instance;
            string world = ZNet.instance.GetWorldName();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var report = new StringBuilder();
            report.AppendLine($"# {Command} genloc {(apply ? "APPLY" : "dry run")} '{world}' {DateTime.Now:yyyy-MM-dd HH:mm}: BcImport active {BcMigratedWorld.Active}, seed {wg.GetSeed()}, FAT {FatVersion}; protect {protectPath}");
            var planned = new List<KeyValuePair<ZoneSystem.ZoneLocation, Vector3>>();
            var generated = GeneratedRef(zs);
            Zones.FatLocationAccelerator.GenLocRefused = new HashSet<Vector2s>(protect);
            foreach (var l in types)
            {
                int pre = 0; foreach (var li in zs.m_locationInstances.Values) if (li.m_location != null && string.Equals(LocName(li.m_location), LocName(l), StringComparison.Ordinal)) pre++;
                Zones.FatLocationAccelerator.GenLocActive = true; Zones.FatLocationAccelerator.GenLocDry = !apply; Zones.FatLocationAccelerator.GenLocPlanned.Clear();
                IEnumerator it = Zones.FatLocationAccelerator.Place(zs, l, new ZPackage());
                while (true)
                {
                    bool more;
                    try { more = it.MoveNext(); }
                    catch (Exception ex) { say($"{Command} genloc: planning {LocName(l)} threw {ex.Message}"); more = false; }
                    if (!more) break;
                    yield return it.Current;
                }
                Zones.FatLocationAccelerator.GenLocActive = false;
                int g = 0, u = 0;
                foreach (var p in Zones.FatLocationAccelerator.GenLocPlanned) { if (generated.Contains(ZoneSystem.GetZone(p))) g++; else u++; planned.Add(new KeyValuePair<ZoneSystem.ZoneLocation, Vector3>(l, p)); }
                string tl = $"{LocName(l)}: quantity {l.m_quantity}, had {pre}, planned {g + u} ({g} in generated zone(s), {u} in zone(s) nobody generated yet)";
                report.AppendLine("# " + tl);
                FiresLogger.LogInfo($"[BcImport] {Command} genloc {tl}");
            }
            Zones.FatLocationAccelerator.GenLocPlanned.Clear();
            if (!apply)
            {
                say($"{Command} genloc dry run: {planned.Count:N0} new location(s) planned for {types.Count} type(s) (nothing registered or placed); per type in the log and the list file; took {clock.Elapsed.TotalSeconds:0} s");
                WriteGenLocReport(report, "dry", say);
                s_running = false; s_requester = 0L;
                yield break;
            }
            var targets = planned.FindAll(kv => generated.Contains(ZoneSystem.GetZone(kv.Value)));
            say($"{Command} genloc: {planned.Count:N0} new location(s) registered; {targets.Count:N0} land in generated zones and are spawned now, the rest spawn when a player first comes near.");
            var hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
            var vegHashes = new HashSet<int>(); foreach (var v in zs.m_vegetation) if (v.m_prefab != null) vegHashes.Add(v.m_prefab.name.GetStableHashCode());
            int zoneCtrl = "_ZoneCtrl".GetStableHashCode();
            var frame = System.Diagnostics.Stopwatch.StartNew();
            var gathered = new List<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>();
            int idx = 0, lastPct = 0, done = 0, dropped = 0, failed = 0; var remStep = new int[3]; var plStep = new int[3]; var remRun = new int[3]; var plRun = new int[3];
            bool checkSaid = false;
            var sampled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in targets)
            {
                var l = kv.Key; Vector3 pos = kv.Value; Vector2s zone = ZoneSystem.GetZone(pos); string tag = $"{zone.x},{zone.y}";
                int pct = targets.Count > 0 ? (int)(100L * idx / targets.Count) : 100; idx++;
                if (pct > lastPct)
                {
                    lastPct = pct;
                    say($"{Command} genloc: {pct}% ({idx - 1:N0} / {targets.Count:N0}); this step removed vegetation {remStep[1]:N0} / other {remStep[0]:N0}, placed location objects {plStep[2]:N0} / vegetation {plStep[1]:N0} / other {plStep[0]:N0}");
                    for (int f = 0; f < 3; f++) { remRun[f] += remStep[f]; plRun[f] += plStep[f]; remStep[f] = plStep[f] = 0; }
                }
                if (OtherPeers(s_requester) != 0) { say($"{Command} genloc: STOPPED - another peer connected ({done} spawned)"); break; }
                if (zs.IsZoneLoaded(zone)) { zs.m_locationInstances.Remove(zone); dropped++; report.AppendLine($"{tag}  dropped {LocName(l)}: zone loaded right now"); continue; }
                gathered.Clear(); visited.Clear();
                FindObjectsMethod.Invoke(ZDOMan.instance, new object[] { zone, gathered, visited });
                bool built = false; var doomed = new List<ZDO>();
                float r2 = l.m_exteriorRadius * l.m_exteriorRadius;
                foreach (ZDO z in gathered)
                {
                    if (ZoneSystem.GetZone(z.GetPosition()) != zone) continue;
                    if (z.GetLong(ZDOVars.s_creator, 0L) != 0 || IsInfinityHammered(z)) { built = true; break; }
                    int h = z.GetPrefab();
                    if (h == zoneCtrl) { doomed.Add(z); continue; }
                    Vector3 zp = z.GetPosition();
                    if (l.m_clearArea && vegHashes.Contains(h) && new Vector2(zp.x - pos.x, zp.z - pos.z).sqrMagnitude <= r2) doomed.Add(z);
                }
                if (built) { zs.m_locationInstances.Remove(zone); dropped++; report.AppendLine($"{tag}  dropped {LocName(l)}: the zone holds player-built or admin objects"); continue; }
                float until = Time.realtimeSinceStartup + TerrainWaitSeconds;
                while (!HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), hm.m_width, hm.m_scale, hm.IsDistantLod, wg) && Time.realtimeSinceStartup < until) yield return null;
                if (!HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), hm.m_width, hm.m_scale, hm.IsDistantLod, wg)) { failed++; report.AppendLine($"{tag}  FAILED terrain not ready; instance left for normal generation"); continue; }
                if (PokeMethod != null)   // 0.2.1239: the new location's prefab must be loaded before the footprint is cleared
                {
                    float pokeUntil = Time.realtimeSinceStartup + TerrainWaitSeconds;
                    while (!(bool)PokeMethod.Invoke(zs, new object[] { l, true }) && Time.realtimeSinceStartup < pokeUntil) yield return null;
                    if (!(bool)PokeMethod.Invoke(zs, new object[] { l, true })) { failed++; report.AppendLine($"{tag}  FAILED {LocName(l)} prefab not loaded in 30 s; nothing cleared, instance left for normal generation"); continue; }
                }
                long me = ZDOMan.GetSessionID();
                foreach (ZDO z in doomed) { remStep[z.GetPrefab() == zoneCtrl ? 0 : 1]++; if (!z.IsOwner()) z.SetOwner(me); ZDOMan.instance.DestroyZDO(z); }
                generated.Remove(zone);
                s_vegOnlyZone = zone; s_skipVegActive = true;
                bool sampleThis = sampled.Add(LocName(l));
                s_sample = sampleThis ? new List<ZDO>() : null;
                s_placed[0] = s_placed[1] = s_placed[2] = 0; s_phase = 0; s_counting = true;
                bool ok;
                try { ok = (bool)SpawnZoneMethod.Invoke(zs, new object[] { zone, ZoneSystem.SpawnMode.Ghost, null }); }
                catch (Exception ex) { ok = false; report.AppendLine($"{tag}  SpawnZone threw {ex.InnerException?.Message ?? ex.Message}"); }
                finally { s_counting = false; for (int f = 0; f < 3; f++) plStep[f] += s_placed[f]; s_skipVegActive = false; }
                if (!ok) { generated.Add(zone); failed++; report.AppendLine($"{tag}  FAILED SpawnZone"); continue; }
                done++;
                report.AppendLine($"{tag}  spawned {LocName(l)} at ({pos.x:0},{pos.z:0}); cleared {doomed.Count}");
                if (sampleThis && s_sample != null)
                {
                    // 0.2.1232 (Fire: the EWP shrine scripts will key on the pieces and their data): one proof line per type, from the ZDOs
                    // EWD's own blueprint spawn just made - prefab counts, every sign text, every item stand item.
                    var pc = new Dictionary<string, int>(); var texts = new List<string>(); var items = new List<string>();
                    foreach (ZDO z in s_sample)
                    {
                        string n = ZNetScene.instance?.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString(CultureInfo.InvariantCulture);
                        pc[n] = pc.TryGetValue(n, out int c) ? c + 1 : 1;
                        string t = z.GetString(ZDOVars.s_text, ""); if (t.Length > 0 && texts.Count < 12) texts.Add(n + "=\"" + (t.Length > 40 ? t.Substring(0, 40) : t) + "\"");
                        int it = z.GetInt(ZDOVars.s_item, 0); string its = z.GetString(ZDOVars.s_item, "");
                        if ((it != 0 || its.Length > 0) && items.Count < 12) items.Add(n + "=" + (its.Length > 0 ? its : (ObjectDB.instance?.GetItemPrefab(it)?.name ?? it.ToString(CultureInfo.InvariantCulture))));
                    }
                    var top = new List<KeyValuePair<string, int>>(pc); top.Sort((x, y) => y.Value.CompareTo(x.Value));
                    var tp = new StringBuilder(); for (int i = 0; i < top.Count && i < 10; i++) tp.Append(i == 0 ? "" : ", ").Append(top[i].Key).Append(' ').Append(top[i].Value);
                    string sl = $"{Command} genloc sample {LocName(l)} at ({pos.x:0},{pos.z:0}): {s_sample.Count} object(s) [{tp}]; sign texts: {(texts.Count > 0 ? string.Join("; ", texts) : "none")}; item stands: {(items.Count > 0 ? string.Join("; ", items) : "none")}";
                    FiresLogger.LogInfo("[BcImport] " + sl); report.AppendLine("# " + sl);
                }
                s_sample = null;
                if (done == 50 && !checkSaid)
                {
                    checkSaid = true;
                    int pv = plRun[1] + plStep[1], pl = plRun[2] + plStep[2];
                    string chk = $"{Command} genloc: check after 50 spawned - location objects placed {pl:N0}, vegetation placed {pv:N0}";
                    if (pv > 0) { FiresLogger.LogWarning("[BcImport] " + chk + " - WARNING: vegetation was placed (stop the run)"); say("WARNING " + chk + " - vegetation was placed: STOP the run"); }
                    else say(chk + " - OK");
                }
                if (frame.ElapsedMilliseconds >= FrameBudgetMs) { yield return null; frame.Restart(); }
            }
            for (int f = 0; f < 3; f++) { remRun[f] += remStep[f]; plRun[f] += plStep[f]; }
            say($"{Command} genloc summary: {done:N0} spawned in generated zones, {dropped:N0} dropped (loaded / built zone), {failed:N0} failed, {planned.Count - targets.Count:N0} left for normal generation; "
                + $"removed vegetation {remRun[1]:N0} / other {remRun[0]:N0}; placed location objects {plRun[2]:N0} / vegetation {plRun[1]:N0} / other {plRun[0]:N0}; took {clock.Elapsed.TotalMinutes:0.0} min");
            WriteGenLocReport(report, "apply", say);
            s_running = false; s_requester = 0L;
        }

        private static void WriteGenLocReport(StringBuilder report, string kind, Action<string> say)
        {
            try
            {
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"genloc_{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(path, report.ToString());
                say($"{Command} genloc list: {path}");
            }
            catch (Exception ex) { say($"{Command} genloc: list not written ({ex.Message})"); }
        }

        // veg-only on a zone whose location stays: vanilla only clears a location's area while placing it, so add that clear area
        // back or new trees would grow inside the kept location.
        // 0.2.1230 (the loc pass doubled vegetation): vanilla SpawnZone always runs PlaceVegetation; a loc-mode regen must not, or the zone
        // gets a second set on top of the one it kept.
        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class PhaseVeg
        {
            private static void Prefix() { if (s_counting) s_phase = 1; }
            private static void Postfix() { if (s_counting) s_phase = 0; }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        private static class PhaseLoc
        {
            private static void Prefix() { if (s_counting) s_phase = 2; }
            private static void Postfix() { if (s_counting) s_phase = 0; }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(Vector3), typeof(int) })]
        private static class CountPlaced
        {
            private static void Postfix(ZDO __result) { if (!s_counting) return; if (s_zoneCreated != null && __result != null && s_zoneCreated.Count < 8000) s_zoneCreated.Add(__result); s_placed[s_phase]++; if (s_phase == 2 && s_sample != null && __result != null && s_sample.Count < 2000) s_sample.Add(__result); }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class SkipVegInLocMode
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector2s zoneID)
            {
                if (!s_skipVegActive || !zoneID.Equals(s_vegOnlyZone)) return true;
                s_vegSkipped++;
                return false;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class KeepLocationClear
        {
            private static void Prefix(Vector2s zoneID, object[] __args)
            {
                if (!s_vegOnlyActive || !zoneID.Equals(s_vegOnlyZone) || ClearAreaType == null) return;
                if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zoneID, out var inst) || inst.m_location == null || !inst.m_location.m_clearArea) return;
                if (__args[4] is IList list) list.Add(Activator.CreateInstance(ClearAreaType, inst.m_position, inst.m_location.m_exteriorRadius));
            }
        }
    }
}
