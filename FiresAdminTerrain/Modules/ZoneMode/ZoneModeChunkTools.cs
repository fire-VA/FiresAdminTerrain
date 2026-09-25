using System;
using System.Collections.Generic;
using FiresCore.Sync;
using FiresCore.UI;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── THE CHAOS HAMMER'S BUILD MENU ───────────────────────────────────────
    // The hammer carries a piece table of its own, so right click opens vanilla's own build menu exactly as the hammer
    // and the hoe open theirs, and every tool is a piece in that grid. Two come baked in the bundle with their own
    // icons — copy the chunk you look at, place a saved chunk — and each saved chunk gets an entry of its own, cloned
    // from the placing tool: that is the grid of saved chunks. Placing one never builds anything. FAT's one
    // TryPlacePiece hook hands the piece here and the tool runs on the chunk the admin is looking at.
    //
    // Nothing here is learned as a recipe. Vanilla lists a piece only when the player knows its name, so these are
    // forced into the table's own available set for a local admin, the way the V-tools are (VoxelToolPieces).
    internal static class ZoneModeChunkTools
    {
        internal const string CopyToolPrefab = "FiresChunkToolCopy";
        internal const string PlaceToolPrefab = "FiresChunkToolPlace";
        private const string TableObject = "fires_chunktool_table";
        private const string PlaceClonePrefix = "fires_chunkplace_";

        private const string RpcAsk = "FiresAdminTerrain_ChunkTool";
        private const string RpcTell = "FiresAdminTerrain_ChunkToolReply";
        private const byte AskSave = 1;
        private const byte AskPlace = 2;
        private const byte AskList = 3;
        private const byte TellMessage = 1;
        private const byte TellList = 2;

        private static ZRoutedRpc s_registeredOn;
        private static readonly List<string> s_saved = new List<string>();
        private static readonly Dictionary<Piece, string> s_placeByPiece = new Dictionary<Piece, string>();
        private static readonly List<GameObject> s_clones = new List<GameObject>();
        private static PieceTable s_table;
        private static GameObject s_copyTool;
        private static GameObject s_placeTemplate;
        private static Sprite s_icon;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcAsk, RPC_Ask);
                rpc.Register<ZPackage>(RpcTell, RPC_Tell);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk tool RPC register failed: {ex.Message}"); }
        }

        internal static void Reset()
        {
            s_saved.Clear();
            s_placeByPiece.Clear();
        }

        // ── The table ───────────────────────────────────────────────────────
        // From FAT's one ObjectDB.Awake hook (FatVanillaHooks), beside the V-tools and the chunk machines.
        internal static void OnObjectDBAwake(ObjectDB odb)
        {
            try { Build(odb); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] chunk tool menu could not be built: {ex.Message}"); }
        }

        private static void Build(ObjectDB odb)
        {
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;
            if (!ZoneModePrefabs.TryGet(ZoneModePrefabs.ChaosHammerPrefab, out var hammer)
                || !ZoneModePrefabs.TryGet(CopyToolPrefab, out s_copyTool)
                || !ZoneModePrefabs.TryGet(PlaceToolPrefab, out s_placeTemplate))
                return;
            var shared = hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
            if (shared == null) return;

            if (s_table == null)
            {
                var holder = new GameObject(TableObject);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                holder.SetActive(false);
                s_table = holder.AddComponent<PieceTable>();
                s_table.m_canRemovePieces = false;
                s_table.m_canRemoveFeasts = false;
                s_table.m_hideAdvancedMenu = true;
            }
            shared.m_buildPieces = s_table;
            // Every tool wears the Chaos Hammer's own icon, the way Infinity Hammer's tools all wear its hammer: the
            // name is what tells them apart.
            s_icon = shared.m_icons != null && shared.m_icons.Length > 0 ? shared.m_icons[0] : null;
            Rebuild();
            FiresLogger.LogInfo($"[FiresZones] the Chaos Hammer carries its own build menu: {s_table.m_pieces.Count} tool(s).");
        }

        // The grid: the copying tool, then one entry per saved chunk.
        private static void Rebuild()
        {
            if (s_table == null) return;
            s_table.m_pieces.Clear();
            s_placeByPiece.Clear();
            foreach (var clone in s_clones) if (clone != null) UnityEngine.Object.Destroy(clone);
            s_clones.Clear();

            if (s_copyTool != null)
            {
                Wear(s_copyTool);
                s_table.m_pieces.Add(s_copyTool);
            }
            if (s_placeTemplate == null) return;
            foreach (string name in s_saved)
            {
                var clone = UnityEngine.Object.Instantiate(s_placeTemplate);
                clone.name = PlaceClonePrefix + name;
                clone.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(clone);
                var piece = clone.GetComponent<Piece>();
                if (piece == null) { UnityEngine.Object.Destroy(clone); continue; }
                piece.m_name = name;
                piece.m_description = "Look at the chunk this one should replace and swing.";
                if (piece.m_icon == null) piece.m_icon = s_icon;
                s_placeByPiece[piece] = name;
                s_clones.Add(clone);
                s_table.m_pieces.Add(clone);
            }
        }

        // Vanilla lists a piece only when the player knows its name, and these carry no recipe, so an admin gets them
        // forced in and everyone else gets them taken out again. From the Priority.High postfix of FAT's one
        // PieceTable.UpdateAvailable hook, ahead of FAP's own visibility pass.
        internal static void ApplyVisibility(PieceTable table)
        {
            if (s_table == null || table != s_table || table.m_pieces == null) return;
            bool show = AdminSyncing.IsLocalAdmin();
            // The grid itself reads the per-category lists, not the flat set, so a piece has to be put in both.
            var byCategory = ByCategory(table);
            foreach (var go in table.m_pieces)
            {
                var piece = go != null ? go.GetComponent<Piece>() : null;
                if (piece == null) continue;
                if (!show)
                {
                    table.m_availablePieces.Remove(piece);
                    table.m_enabledPieces.Remove(piece);
                    if (byCategory != null) foreach (var list in byCategory) list.Remove(piece);
                    continue;
                }
                table.m_enabledPieces.Add(piece);
                if (!table.m_availablePieces.Add(piece)) continue;
                if (byCategory == null) continue;
                int category = (int)piece.m_category;
                if (category >= 0 && category < byCategory.Count) byCategory[category].Add(piece);
            }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<PieceTable, List<List<Piece>>> ByCategory =
            HarmonyLib.AccessTools.FieldRefAccess<PieceTable, List<List<Piece>>>("m_availablePiecesByCategory");

        // A tool that carries a badged icon of its own keeps it; the rest wear the hammer's.
        private static void Wear(GameObject tool)
        {
            var piece = tool != null ? tool.GetComponent<Piece>() : null;
            if (piece != null && piece.m_icon == null) piece.m_icon = s_icon;
        }

        internal static bool IsOurPiece(Piece piece)
            => piece != null && (s_placeByPiece.ContainsKey(piece)
                || (s_copyTool != null && ReferenceEquals(piece.gameObject, s_copyTool)));

        // ── The swing ───────────────────────────────────────────────────────
        // Called from FAT's one Player.TryPlacePiece hook. Nothing is ever built.
        internal static void OnPlace(Player player, Piece piece)
        {
            if (player == null || piece == null) return;
            ZoneModeChaosHammer.Laugh(player);
            if (!ZoneModeCommands.TryGetLookedAtZone(out var zone))
            {
                Tell("Look at the ground of the chunk you mean.");
                return;
            }
            if (s_placeByPiece.TryGetValue(piece, out string name))
            {
                Ask(AskPlace, name, zone);
                return;
            }
            SaveDialog(zone);
        }

        private static void SaveDialog(Vector2s zone)
        {
            var name = new MenuDialog.TextField("Name", $"chunk_{zone.x}_{zone.y}");
            var overwrite = new MenuDialog.CheckField("Replace one of the same name", false);
            MenuDialog.Show($"Save chunk ({zone.x},{zone.y})", new MenuDialog.Field[] { name, overwrite },
                () => Ask(overwrite.Value ? (byte)(AskSave | 0x80) : AskSave, name.Value.Trim(), zone),
                validate: () => ZoneModeChunkBlueprint.IsUsableName(name.Value.Trim(), out string problem) ? null : problem,
                okLabel: "Save");
        }

        // The saved names come from the server, so the grid holds what is really on disk. Asked when the hammer is drawn.
        internal static void RefreshNames() => Ask(AskList, null, default);

        // ── Asking the server ───────────────────────────────────────────────
        private static void Ask(byte what, string name, Vector2s zone)
        {
            var net = ZNet.instance;
            if (net == null) return;
            if (net.IsServer())
            {
                Answer(ZNet.GetUID(), what, name, zone, local: true);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(what);
            pkg.Write(name ?? string.Empty);
            pkg.Write(zone);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk, pkg);
        }

        private static void RPC_Ask(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            byte what = pkg.ReadByte();
            string name = pkg.ReadString();
            var zone = pkg.ReadVector2s();
            if (!AdminSyncing.IsAdmin(sender))
            {
                Send(sender, TellMessage, "The Chaos Hammer's tools are an admin's.");
                return;
            }
            Answer(sender, what, name, zone, local: false);
        }

        private static void Answer(long who, byte what, string name, Vector2s zone, bool local)
        {
            bool overwrite = (what & 0x80) != 0;
            switch (what & 0x7F)
            {
                case AskSave:
                {
                    ZoneModeChunkBlueprint.TrySave(zone, name, overwrite, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    Names(who, local);
                    return;
                }
                case AskPlace:
                {
                    ZoneModeChunkBlueprint.TryPlace(zone, name, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    return;
                }
                case AskList:
                    Names(who, local);
                    return;
            }
        }

        private static void Names(long who, bool local)
        {
            var names = new List<string>();
            foreach (string line in ZoneModeChunkBlueprint.List())
            {
                int colon = line.IndexOf(':');
                names.Add(colon > 0 ? line.Substring(0, colon) : line);
            }
            if (local)
            {
                Adopt(names);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(TellList);
            pkg.Write(names.Count);
            foreach (string name in names) pkg.Write(name);
            ZRoutedRpc.instance.InvokeRoutedRPC(who, RpcTell, pkg);
        }

        private static void Reply(long who, bool local, byte kind, string message)
        {
            if (local)
            {
                Tell(message);
                return;
            }
            Send(who, kind, message);
        }

        private static void Send(long who, byte kind, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(kind);
            pkg.Write(message ?? string.Empty);
            ZRoutedRpc.instance.InvokeRoutedRPC(who, RpcTell, pkg);
        }

        private static void RPC_Tell(long sender, ZPackage pkg)
        {
            byte kind = pkg.ReadByte();
            if (kind == TellMessage)
            {
                Tell(pkg.ReadString());
                return;
            }
            int count = pkg.ReadInt();
            var names = new List<string>(count);
            for (int i = 0; i < count; i++) names.Add(pkg.ReadString());
            Adopt(names);
        }

        private static void Adopt(List<string> names)
        {
            if (Same(names)) return;
            s_saved.Clear();
            s_saved.AddRange(names);
            Rebuild();
            var player = Player.m_localPlayer;
            if (player != null && ZoneModeChaosHammer.IsHolding(player)) RefreshPieces.Invoke(player, null);
        }

        private static readonly System.Reflection.MethodInfo RefreshPieces =
            HarmonyLib.AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private static bool Same(List<string> names)
        {
            if (names.Count != s_saved.Count) return false;
            for (int i = 0; i < names.Count; i++) if (names[i] != s_saved[i]) return false;
            return true;
        }

        private static void Tell(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
            FiresLogger.LogInfo("[FiresZones] " + message);
        }
    }
}
