using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── SKY ISLAND COMMANDS ─────────────────────────────────────────────────
    // The registry turned islands into stored data, which is what makes editing
    // them meaningful: a change survives the next load instead of being undone by
    // the generator. These are the Upgrade World location commands' shape applied
    // to islands — list / count / add / remove / reset.
    //
    // PLACED, borrowed from UpgradeWorld's `m_placed`. A location it has already
    // materialised is never re-rolled, only the pending ones are. An island counts
    // as placed once any zone its footprint touches has been generated: players
    // have been there, may have built on it, and its chunks may hold brush edits
    // keyed to its shape. `reset` re-rolls everything else and leaves those alone.
    // It is DERIVED from ZoneSystem's own generated-zone set rather than stored, so
    // it cannot drift out of step with the world.
    //
    // AUTHORITY. Reads answer locally from whatever set this peer holds. Every edit
    // runs on the server, admin-gated, then saves, re-arms the affected zones and
    // re-offers the file so clients converge — a client editing its own copy would
    // just be overwritten by the next offer.
    internal static partial class FiresSkyIslands
    {
        private const string CmdStatus = "fires_islands";
        private const string CmdList = "fires_islands_list";
        private const string CmdAdd = "fires_islands_add";
        private const string CmdRemove = "fires_islands_remove";
        private const string CmdReset = "fires_islands_reset";
        private const string CmdRedress = "fires_islands_redress";
        private const float DefaultRedressRadius = 300f;

        private const string RpcEdit = "FiresAdminTerrain_IslandEdit";
        private const string RpcEditResult = "FiresAdminTerrain_IslandEditResult";

        private const float DefaultAddRadius = 90f;
        private const float MinAddRadius = 12f;
        private const float MaxAddRadius = 400f;
        private const float DefaultRemoveRadius = 250f;
        private const float MaxListRadius = 20000f;
        private const float MinGroundClearance = 12f;
        private const float ServerReplyTimeoutSeconds = 20f;

        private static bool s_commandsRegistered;
        private static ZRoutedRpc s_editRpcOn;
        private static bool s_awaitingEditReply;

        // RegisterCommands and EnsureEditRpcs run from FAT's one Terminal.InitTerminal and ZNet.Awake hooks
        // (FatVanillaHooks).
        internal static void EnsureEditRpcs()
        {
            Zones.ZoneModeFinders.EnsureRpcs();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_editRpcOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcEdit, RPC_OnEdit);
                rpc.Register<ZPackage>(RpcEditResult, RPC_OnEditResult);
                s_editRpcOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresSkyIslands] edit RPC register failed: {ex.Message}"); }
        }

        internal static void RegisterCommands()
        {
            if (s_commandsRegistered) return;
            s_commandsRegistered = true;
            Zones.ZoneModeFinders.RegisterCommand();

            new Terminal.ConsoleCommand(CmdStatus,
                "Sky island set for this world: how many, whether it is frozen, and where it is stored.",
                args => { foreach (var line in Status()) args.Context.AddString(line); }, isCheat: true);

            new Terminal.ConsoleCommand(CmdList,
                "List this world's sky islands. Usage: " + CmdList + " [radius] — with a radius, only those "
                + "whose centre is within it of you.",
                args =>
                {
                    float radius = ParseFloat(args, 1, MaxListRadius, MaxListRadius);
                    foreach (var line in List(PlayerPos(), radius)) args.Context.AddString(line);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdAdd,
                "Add a sky island at your position — its BASE sits at your current height, so fly to where you "
                + "want it. Usage: " + CmdAdd + " [rolling|mountain|crater|grotto] [radius]. Added islands are dry; "
                + "use the water tools for a pool.",
                args =>
                {
                    byte kind = ParseKind(args, 1);
                    float radius = Mathf.Clamp(ParseFloat(args, 2, DefaultAddRadius, MaxAddRadius), MinAddRadius, MaxAddRadius);
                    SendEdit(args.Context, EditOp.Add, PlayerPos(), radius, kind);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdRemove,
                "Remove sky islands centred within a radius of you. Usage: " + CmdRemove + " [radius] (default "
                + DefaultRemoveRadius + "). Removes placed islands too — anything built on one is left floating.",
                args =>
                {
                    float radius = ParseFloat(args, 1, DefaultRemoveRadius, MaxListRadius);
                    SendEdit(args.Context, EditOp.Remove, PlayerPos(), radius, 0);
                }, isCheat: true);

            new Terminal.ConsoleCommand(CmdReset,
                "Re-roll every island in a zone nobody has generated yet, from the CURRENT generator and rarity. "
                + "Islands in generated zones are kept exactly as they are, so nothing anyone has visited moves.",
                args => SendEdit(args.Context, EditOp.Reset, PlayerPos(), 0f, 0), isCheat: true);

            // Fire 10-05: old worlds keep the dressing laid on the uncarved top (floating over river banks). This takes off ONLY
            // the dressing the skyland code placed (tagged objects, or, from before the tag, an object of the same prefab at
            // exactly the spot the old rule put it) and lays it again on the carved top. Builds and anything players placed stay.
            new Terminal.ConsoleCommand(CmdRedress,
                "Re-dress the zone skylands within a radius of you on their carved top. Usage: " + CmdRedress + " [radius] (default "
                + DefaultRedressRadius + "; 'all' for every skyland). Removes only dressing the skyland code placed, never builds.",
                args =>
                {
                    bool all = args.Length > 1 && string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase);
                    float radius = all ? MaxListRadius * 10f : ParseFloat(args, 1, DefaultRedressRadius, MaxListRadius);
                    SendEdit(args.Context, EditOp.Redress, PlayerPos(), radius, 0);
                }, isCheat: true);
        }

        private enum EditOp : byte { Add, Remove, Reset, Redress }

        // ── Reads ───────────────────────────────────────────────────────────
        private static List<string> Status()
        {
            var lines = new List<string>();
            if (ZNet.World == null) { lines.Add("[Islands] Not in a world."); return lines; }

            if (AwaitingServer)
            {
                lines.Add("[Islands] Waiting for the server's island set — islands are OFF until it arrives.");
                return lines;
            }

            int placed = 0, rolling = 0, mountain = 0, crater = 0, grotto = 0;
            foreach (var isl in s_islands)
            {
                if (IsPlaced(in isl)) placed++;
                switch (isl.Kind)
                {
                    case 1: mountain++; break;
                    case 2: crater++; break;
                    case 3: grotto++; break;
                    default: rolling++; break;
                }
            }

            lines.Add($"[Islands] {s_islands.Count} island(s), {ClusterCount} cluster(s) — "
                      + $"{rolling} rolling, {mountain} mountain, {crater} crater, {grotto} grotto.");
            lines.Add(s_seeded
                ? $"[Islands] Frozen for this world; the generator is not consulted. Seeded at rarity {s_seededRarity:0.00}"
                  + (FiresVoxelConfig.SkyIslandRarity != null && !Mathf.Approximately(s_seededRarity, CurrentRarity())
                      ? $" (config now {CurrentRarity():0.00} — does not apply here)." : ".")
                : "[Islands] This world is UNSEEDED — it seeds the first time it loads with islands enabled.");
            if (IsAuthority())
                lines.Add($"[Islands] {placed} placed (a zone they touch is generated), {s_islands.Count - placed} not.");
            else
                lines.Add("[Islands] Placed counts are server-side only; run this on the server to see them.");
            lines.Add($"[Islands] Store: {RegistryPath(ZNet.World.m_name)}");
            return lines;
        }

        private static List<string> List(Vector3 center, float radius)
        {
            var lines = new List<string>();
            if (s_islands.Count == 0)
            {
                lines.Add(AwaitingServer
                    ? "[Islands] Waiting for the server's island set."
                    : "[Islands] This world has no sky islands.");
                return lines;
            }

            bool authority = IsAuthority();
            int shown = 0;
            for (int i = 0; i < s_islands.Count; i++)
            {
                var isl = s_islands[i];
                float x = WorldX(in isl), z = WorldZ(in isl);
                float dist = Vector2.Distance(new Vector2(center.x, center.z), new Vector2(x, z));
                if (dist > radius) continue;
                lines.Add($"  [{i}] {KindName(isl.Kind),-8} ({x:0},{isl.BaseY + isl.OffY:0},{z:0}) r={isl.R:0} "
                          + $"{dist:0}m away" + (authority ? (IsPlaced(in isl) ? " PLACED" : " unplaced") : "")
                          + (IsMoved(in isl) ? " (from a blueprint)" : ""));
                shown++;
            }
            lines.Insert(0, radius >= MaxListRadius
                ? $"[Islands] {shown} island(s):"
                : $"[Islands] {shown} island(s) within {radius:0}m:");
            return lines;
        }

        // Placed = any zone this island's footprint touches has been generated.
        // Server-only truth: a client's generated-zone set is empty.
        private static bool IsPlaced(in Island isl)
        {
            float x = WorldX(in isl), z = WorldZ(in isl);
            var zmin = ZoneSystem.GetZone(new Vector3(x - isl.R, 0f, z - isl.R));
            var zmax = ZoneSystem.GetZone(new Vector3(x + isl.R, 0f, z + isl.R));
            for (int zz = zmin.y; zz <= zmax.y; zz++)
                for (int zx = zmin.x; zx <= zmax.x; zx++)
                    if (VoxelZoneStreamer.IsZoneGenerated(new Vector2s(zx, zz))) return true;
            return false;
        }

        private static bool IsAuthority() => ZNet.instance != null && ZNet.instance.IsServer();

        // ── Edit dispatch ───────────────────────────────────────────────────
        private static void SendEdit(Terminal context, EditOp op, Vector3 center, float radius, byte kind)
        {
            if (ZNet.instance == null || ZNet.World == null)
            {
                context.AddString("[Islands] Must be in a world.");
                return;
            }
            if (IsAuthority())
            {
                foreach (var line in RunEdit(op, center, radius, kind)) context.AddString(line);
                return;
            }

            var pkg = new ZPackage();
            pkg.Write((byte)op);
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(kind);
            s_awaitingEditReply = true;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcEdit, pkg);
            context.AddString("[Islands] Sent to the server...");
            var host = FiresAdminTerrain.Instance;
            if (host != null) host.StartCoroutine(WatchForEditReply(context));
        }

        private static IEnumerator WatchForEditReply(Terminal context)
        {
            float deadline = Time.realtimeSinceStartup + ServerReplyTimeoutSeconds;
            while (s_awaitingEditReply && Time.realtimeSinceStartup < deadline) yield return null;
            if (!s_awaitingEditReply) yield break;
            s_awaitingEditReply = false;
            context?.AddString($"[Islands] No reply from the server within {ServerReplyTimeoutSeconds:0}s — "
                               + "FiresAdminTerrain is probably missing or out of date there. Island edits are server-side.");
        }

        private static void RPC_OnEdit(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                SendEditResult(sender, "[Islands] Permission denied — admin only.");
                return;
            }
            var op = (EditOp)pkg.ReadByte();
            Vector3 center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            byte kind = pkg.ReadByte();
            foreach (var line in RunEdit(op, center, radius, kind)) SendEditResult(sender, line);
        }

        private static void RPC_OnEditResult(long sender, ZPackage pkg)
        {
            s_awaitingEditReply = false;
            string message = pkg.ReadString();
            try { if (Console.instance != null) Console.instance.AddString(message); } catch { }
            FiresLogger.LogInfo(message);
        }

        private static void SendEditResult(long target, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcEditResult, pkg);
        }

        // ── Edits (server) ──────────────────────────────────────────────────
        private static List<string> RunEdit(EditOp op, Vector3 center, float radius, byte kind)
        {
            var lines = new List<string>();
            var world = ZNet.World;
            if (world == null) { lines.Add("[Islands] No world loaded."); return lines; }
            if (op == EditOp.Redress) return Zones.ZoneModeSkylands.Redress(center, radius);

            var before = new List<Island>(s_islands);
            var next = new List<Island>(s_islands);
            int clusters = ClusterCount;

            switch (op)
            {
                case EditOp.Add:
                {
                    var added = MakeIsland(center.x, center.z, radius, center.y, kind);
                    next.Add(added);
                    lines.Add($"[Islands] Added a {KindName(kind)} island at ({added.X:0},{added.BaseY:0},{added.Z:0}) "
                              + $"r={added.R:0}, underside {added.Bot:0}, top {added.Top:0}.");
                    break;
                }
                case EditOp.Remove:
                {
                    int removedPlaced = 0;
                    next.RemoveAll(isl =>
                    {
                        float d = Vector2.Distance(new Vector2(center.x, center.z), new Vector2(WorldX(in isl), WorldZ(in isl)));
                        if (d > radius) return false;
                        if (IsPlaced(in isl)) removedPlaced++;
                        return true;
                    });
                    int removed = before.Count - next.Count;
                    if (removed == 0) { lines.Add($"[Islands] No island centred within {radius:0}m."); return lines; }
                    lines.Add($"[Islands] Removed {removed} island(s) within {radius:0}m"
                              + (removedPlaced > 0
                                  ? $" — {removedPlaced} of them PLACED; anything built on those is now floating."
                                  : "."));
                    break;
                }
                case EditOp.Reset:
                {
                    if (!BuildsSkyIslandsNow())
                    {
                        lines.Add("[Islands] Islands are disabled in config — nothing to re-roll.");
                        return lines;
                    }
                    var keep = new List<Island>();
                    foreach (var isl in before) if (IsPlaced(in isl)) keep.Add(isl);

                    Build(world.m_seed);                       // re-roll from the CURRENT generator + rarity
                    var rolled = new List<Island>(s_islands);
                    clusters = ClusterCount;

                    next = new List<Island>(keep);
                    int dropped = 0;
                    foreach (var isl in rolled)
                    {
                        if (OverlapsAny(in isl, keep)) { dropped++; continue; }
                        next.Add(isl);
                    }
                    lines.Add($"[Islands] Re-rolled: kept {keep.Count} placed, rolled {rolled.Count}, "
                              + $"dropped {dropped} that landed on a kept island → {next.Count} total.");
                    break;
                }
            }

            Reinstall(next, clusters);
            SaveAndPublish(world, before, lines);
            return lines;
        }

        // Same derived geometry Build uses, so a hand-placed island streams and
        // classifies exactly like a generated one. Dry by design: ponds are tied to
        // the generator's one-level-per-column water, so an added island gets none.
        private static Island MakeIsland(float wx, float wz, float r, float baseY, byte kind)
        {
            float floorY = GetGround(wx, wz, out float g) ? g : baseY - 120f;
            float baseUp = Mathf.Max(MinGroundClearance, baseY - floorY);
            var isl = new Island
            {
                X = wx,
                Z = wz,
                R = r,
                BaseY = baseY,
                Seed = wx * 0.61803f + wz * 0.31415f,
                MaxDepth = Mathf.Max(4f, baseUp - 12f),
                Kind = kind,
                RillAng = float.NaN,
            };
            isl.Top = baseY + (kind == 1 ? r * 0.48f : r * 0.04f) + 16f;
            isl.Bot = baseY - Mathf.Min(r * 0.90f, isl.MaxDepth) - 6f;
            return isl;
        }

        private static bool OverlapsAny(in Island candidate, List<Island> others)
        {
            foreach (var other in others)
            {
                float dx = WorldX(in candidate) - WorldX(in other), dz = WorldZ(in candidate) - WorldZ(in other);
                float reach = candidate.R + other.R;
                if (dx * dx + dz * dz < reach * reach) return true;
            }
            return false;
        }

        private static void Reinstall(List<Island> islands, int clusters)
        {
            Clear();
            foreach (var isl in islands) AddIsland(in isl);
            s_any = s_islands.Count > 0;
            ClusterCount = clusters;
            s_seeded = true;   // an edited world is authored, so it is seeded by definition
        }

        private static void SaveAndPublish(World world, List<Island> before, List<string> lines)
        {
            string path = RegistryPath(world.m_name);
            if (TrySaveRegistry(path, world, out string failure))
                lines.Add($"[Islands] Saved — {s_islands.Count} island(s) frozen to {System.IO.Path.GetFileName(path)}.");
            else
            {
                SetActiveHash(Serialize(world, CurrentRarity()));
                lines.Add($"[Islands] WARNING could not save ({failure}) — the change is live this session only "
                          + "and the next load reverts it.");
            }

            int marked = MarkChangedZonesDirty(before, s_islands);
            lines.Add(marked > 0
                ? $"[Islands] {marked} loaded zone-disc(s) re-armed; their chunks regenerate in place, edits kept."
                : "[Islands] No loaded zone was affected — the change applies as those zones stream in.");

            try { FiresCore.Net.WorldCacheSync.OfferKeyToAll(RegistrySyncKey); }
            catch (Exception ex) { lines.Add($"[Islands] WARNING could not notify clients: {ex.Message}"); }
        }

        // ── Parsing ─────────────────────────────────────────────────────────
        private static Vector3 PlayerPos()
            => Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;

        private static float ParseFloat(Terminal.ConsoleEventArgs args, int index, float fallback, float max)
        {
            if (args.Length <= index) return fallback;
            return float.TryParse(args[index], out float parsed) ? Mathf.Clamp(parsed, 0f, max) : fallback;
        }

        private static byte ParseKind(Terminal.ConsoleEventArgs args, int index)
        {
            if (args.Length <= index) return 0;
            string token = args[index]?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token)) return 0;
            if (token.StartsWith("mo")) return 1;
            if (token.StartsWith("cr")) return 2;
            if (token.StartsWith("gr")) return 3;
            return 0;
        }
    }
}
