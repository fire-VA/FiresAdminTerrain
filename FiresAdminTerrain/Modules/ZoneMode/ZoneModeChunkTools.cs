using System;
using System.Collections.Generic;
using FiresCore.Pieces;
using FiresCore.Sync;
using FiresCore.UI;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // ── THE CHAOS HAMMER'S BUILD MENU ───────────────────────────────────────
    // The Chaos Hammer is the home of every admin terrain tool (Fire, 2026-09-27, Tools\FAT_CHUNK_BLUEPRINTS_PLAN.md).
    // It carries a piece table of its own, so right click opens vanilla's own build menu exactly as the hammer and the
    // hoe open theirs, and every tool is a piece in that grid. It is the hammer's advanced menu, not the hoe's flat grid:
    // Core's BuildMenuPages gives this table its own top tabs (Categories / Biomes / Blueprints, then vanilla's Favorites)
    // and the rows down the left of each.
    //
    // FAT's own tools never build anything: FAT's one TryPlacePiece hook hands them here and the tool runs on the chunk
    // the admin is looking at. The V-tools keep their own drag pump (VoxelToolPieces). The Clutter row is FAP's Clutter
    // tab, lent through Core's SharedPieceGroups; FAP's own place patch runs those, whatever table they came from.
    //
    // Nothing here is learned as a recipe. Vanilla lists a piece only when the player knows its name, so these are
    // forced into the table's own available set for a local admin.
    internal static class ZoneModeChunkTools
    {
        internal const string CopyToolPrefab = "FiresChunkToolCopy";
        internal const string PlaceToolPrefab = "FiresChunkToolPlace";
        internal const string RemoveToolPrefab = "FiresChunkToolRemove";
        internal const string UndoToolPrefab = "FiresChunkToolUndo";
        internal const string RedoToolPrefab = "FiresChunkToolRedo";
        private const string TableObject = "fires_chunktool_table";
        private const string PlaceClonePrefix = "fires_chunkplace_";
        private const string AreaPlaceClonePrefix = "fires_areaplace_";
        private const string SaveAreaToolObject = "fires_chunktool_savearea";
        private const string SaveAreaToolName = "Save an area";
        private const string SaveAreaToolDescription = "Aim at the middle of what you want and swing. Scroll = radius, "
            + "Shift+scroll = top, Ctrl+scroll = bottom; the popup lets you type them too.";
        private const string SkylandPlaceClonePrefix = "fires_skyplace_";
        private const string SaveSkylandToolObject = "fires_chunktool_saveskyland";
        private const string SaveSkylandToolName = "Save a skyland";
        private const string SaveSkylandToolDescription = "Stand on or near a skyland, aim at it and swing. The whole island goes: "
            + "its land and edits, everything growing and built on it.";
        private const string SkylandEntryDescription = "Aim where this skyland's middle should go and swing. It keeps the height it was "
            + "saved at, raised if the ground there stands too high.";

        // A saved blueprint's key in the list the server sends: its kind, a colon, its name.
        private const char ChunkKind = 'c';
        private const char AreaKind = 'a';
        private const char SkylandKind = 's';
        private const int KindPrefixLength = 2;

        // The menu reads like the hammer's (Fire, 2026-09-27): top tabs Categories / Biomes / Blueprints / Favorites, each
        // with its rows down the left. The values are this table's row keys on BuildMenuPages; an empty row is not shown.
        private enum Row { ChunkTools = 1, ChunkBlueprints, SkyAndAreaBlueprints, Voxel, Paint, Clutter }

        private const string CategoriesPage = "Categories";
        private const string BiomesPage = "Biomes";
        private const string BlueprintsPage = "Blueprints";

        private static readonly (Row Row, string Label)[] CategoryRows =
        {
            (Row.ChunkTools, "Chunk tools"),
            (Row.Voxel, "Voxel"),
            (Row.Paint, "Paint"),
            (Row.Clutter, "Clutter"),
        };

        // Each save tool heads the row of what it saves.
        private static readonly (Row Row, string Label)[] BlueprintRows =
        {
            (Row.ChunkBlueprints, "Chunk blueprints"),
            (Row.SkyAndAreaBlueprints, "Sky & area blueprints"),
        };

        // In the order a world grows them. A biome's row lists the paints and clutter of that biome, and clutter that
        // grows in several biomes is in each of their rows. The row key is the biome's own flag.
        private static readonly (Heightmap.Biome Biome, string Label)[] BiomeRows =
        {
            (Heightmap.Biome.Meadows, "$biome_meadows"),
            (Heightmap.Biome.BlackForest, "$biome_blackforest"),
            (Heightmap.Biome.Swamp, "$biome_swamp"),
            (Heightmap.Biome.Mountain, "$biome_mountain"),
            (Heightmap.Biome.Plains, "$biome_plains"),
            (Heightmap.Biome.Mistlands, "$biome_mistlands"),
            (Heightmap.Biome.AshLands, "$biome_ashlands"),
            (Heightmap.Biome.DeepNorth, "$biome_deepnorth"),
            (Heightmap.Biome.Ocean, "$biome_ocean"),
        };

        private static readonly Row[] BiomeSortedRows = { Row.Paint, Row.Clutter };

        private static readonly Dictionary<Row, List<GameObject>> s_rowPieces = new Dictionary<Row, List<GameObject>>();
        private static readonly Dictionary<GameObject, Heightmap.Biome> s_biomes = new Dictionary<GameObject, Heightmap.Biome>();
        private static readonly ChaosHammerPages s_pages = new ChaosHammerPages();

        private const string RpcAsk = "FiresAdminTerrain_ChunkTool";
        private const string RpcTell = "FiresAdminTerrain_ChunkToolReply";
        private const byte AskSave = 1;
        private const byte AskPlace = 2;
        private const byte AskList = 3;
        private const byte AskRemove = 4;
        private const byte AskUndo = 5;
        private const byte AskRedo = 6;
        private const byte AskIcon = 7;
        private const byte AskSaveArea = 8;
        private const byte AskPlaceArea = 9;
        private const byte AskSaveSkyland = 10;
        private const byte AskPlaceSkyland = 11;
        private const byte AskPaint = 12;
        private const byte AskOverwrite = 0x80;
        private const byte AskKind = 0x7F;
        private const byte TellMessage = 1;
        private const byte TellList = 2;
        private const byte TellIcon = 3;

        private static ZRoutedRpc s_registeredOn;
        private static readonly List<string> s_saved = new List<string>();
        private static readonly Dictionary<Piece, string> s_placeByPiece = new Dictionary<Piece, string>();
        private static readonly List<GameObject> s_clones = new List<GameObject>();
        private static PieceTable s_table;
        private static GameObject s_copyTool;
        private static GameObject s_placeTemplate;
        private static GameObject s_removeTool;
        private static GameObject s_undoTool;
        private static GameObject s_redoTool;
        private static GameObject s_saveAreaTool;
        private static GameObject s_saveSkylandTool;
        private static Sprite s_icon;
        private static bool s_confirming;

        internal static void EnsureRpcs()
        {
            ZoneModeChunkRemoval.EnsureRpcs();
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
            // Tools a bundle older than this build lacks are simply not offered. The whole-zone tools refuse on the
            // server while the world saves or after a refused load (ZoneModeChunkBlueprint.ServerReady).
            ZoneModePrefabs.TryGet(RemoveToolPrefab, out s_removeTool);
            ZoneModePrefabs.TryGet(UndoToolPrefab, out s_undoTool);
            ZoneModePrefabs.TryGet(RedoToolPrefab, out s_redoTool);

            if (s_table == null)
            {
                var holder = new GameObject(TableObject);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                holder.SetActive(false);
                s_table = holder.AddComponent<PieceTable>();
                s_table.m_canRemovePieces = false;
                s_table.m_canRemoveFeasts = false;
                s_table.m_hideAdvancedMenu = false;
            }
            shared.m_buildPieces = s_table;
            BuildMenuPages.Register(s_pages);
            // Every tool wears the Chaos Hammer's own icon, the way Infinity Hammer's tools all wear its hammer: the
            // name is what tells them apart.
            s_icon = shared.m_icons != null && shared.m_icons.Length > 0 ? shared.m_icons[0] : null;
            EnsureSaveAreaTool();
            EnsureSaveSkylandTool();
            ZoneModeAreaTool.Ensure();
            Rebuild();
            FiresLogger.LogInfo($"[FiresZones] the Chaos Hammer carries its own build menu: {s_table.m_pieces.Count} tool(s) "
                + $"({Count(Row.ChunkTools)} chunk tool(s), {Count(Row.Voxel)} V-tool(s), {Count(Row.Clutter)} clutter tool(s) lent by FAP).");
        }

        // Every row rebuilt: FAT's chunk tools, one entry per saved chunk, the V-tools, and whatever FAP lends.
        private static void Rebuild()
        {
            if (s_table == null) return;
            s_table.m_pieces.Clear();
            s_placeByPiece.Clear();
            foreach (var clone in s_clones) if (clone != null) UnityEngine.Object.Destroy(clone);
            s_clones.Clear();
            foreach (var row in s_rowPieces.Values) row.Clear();
            s_biomes.Clear();

            Wear(s_copyTool);
            if (s_copyTool != null) AddToRow(Row.ChunkBlueprints, s_copyTool);
            foreach (var tool in new[] { s_removeTool, s_undoTool, s_redoTool })
            {
                if (tool == null) continue;
                Wear(tool);
                AddToRow(Row.ChunkTools, tool);
            }
            if (s_saveSkylandTool != null) AddToRow(Row.SkyAndAreaBlueprints, s_saveSkylandTool);
            if (s_saveAreaTool != null) AddToRow(Row.SkyAndAreaBlueprints, s_saveAreaTool);
            if (s_placeTemplate != null)
            {
                foreach (string key in s_saved)
                {
                    char kind = KindOf(key);
                    var clone = UnityEngine.Object.Instantiate(s_placeTemplate);
                    clone.name = (kind == AreaKind ? AreaPlaceClonePrefix : kind == SkylandKind ? SkylandPlaceClonePrefix : PlaceClonePrefix) + NameOf(key);
                    clone.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(clone);
                    var piece = clone.GetComponent<Piece>();
                    if (piece == null) { UnityEngine.Object.Destroy(clone); continue; }
                    piece.m_name = NameOf(key);
                    piece.m_description = kind == AreaKind
                        ? "Aim where this area's middle should go and swing. It keeps the heights it was saved at."
                        : kind == SkylandKind ? SkylandEntryDescription : "Look at the chunk this one should replace and swing.";
                    piece.m_icon = IconFor(key);
                    s_placeByPiece[piece] = key;
                    s_clones.Add(clone);
                    AddToRow(kind == ChunkKind ? Row.ChunkBlueprints : Row.SkyAndAreaBlueprints, clone);
                }
            }
            foreach (var tool in VoxelToolPieces.Tools) AddToRow(Row.Voxel, tool);
            foreach (var paint in BiomePaintEntries.Ensure(s_copyTool)) AddToRow(Row.Paint, paint);
            var lent = new List<GameObject>();
            SharedPieceGroups.Collect(SharedPieceGroups.Clutter, lent);
            foreach (var piece in lent) AddToRow(Row.Clutter, piece);
        }

        // A runtime copy of the saving tool: it saves, so it wears the same envelope badge, and its name tells it apart.
        private static void EnsureSaveAreaTool()
        {
            if (s_saveAreaTool != null || s_copyTool == null) return;
            s_saveAreaTool = UnityEngine.Object.Instantiate(s_copyTool);
            s_saveAreaTool.name = SaveAreaToolObject;
            s_saveAreaTool.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(s_saveAreaTool);
            var piece = s_saveAreaTool.GetComponent<Piece>();
            if (piece == null) return;
            piece.m_name = SaveAreaToolName;
            piece.m_description = SaveAreaToolDescription;
        }

        private static void EnsureSaveSkylandTool()
        {
            if (s_saveSkylandTool != null || s_copyTool == null) return;
            s_saveSkylandTool = UnityEngine.Object.Instantiate(s_copyTool);
            s_saveSkylandTool.name = SaveSkylandToolObject;
            s_saveSkylandTool.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(s_saveSkylandTool);
            var piece = s_saveSkylandTool.GetComponent<Piece>();
            if (piece == null) return;
            piece.m_name = SaveSkylandToolName;
            piece.m_description = SaveSkylandToolDescription;
        }

        internal static bool IsSaveAreaSelected(Player player)
        {
            if (s_saveAreaTool == null || player == null || !ReferenceEquals(player.m_buildPieces, s_table)) return false;
            var selected = s_table.GetSelectedPiece();
            return selected != null && ReferenceEquals(selected.gameObject, s_saveAreaTool);
        }

        internal static bool IsPaintSelected(Player player)
        {
            if (s_table == null || player == null || !ReferenceEquals(player.m_buildPieces, s_table)) return false;
            return BiomePaintEntries.TryGet(s_table.GetSelectedPiece(), out _);
        }

        private static string KeyOf(char kind, string name) => kind + ":" + name;
        private static char KindOf(string key) => key.Length > 0 ? key[0] : ChunkKind;
        private static string NameOf(string key) => key.Length >= KindPrefixLength ? key.Substring(KindPrefixLength) : key;

        private static string PathOf(string key)
        {
            switch (KindOf(key))
            {
                case AreaKind: return ZoneModeAreaBlueprint.PathFor(NameOf(key));
                case SkylandKind: return ZoneModeSkylandBlueprint.PathFor(NameOf(key));
                default: return ZoneModeChunkBlueprint.PathFor(NameOf(key));
            }
        }

        private static void AddToRow(Row row, GameObject piece)
        {
            if (piece == null || s_table.m_pieces.Contains(piece)) return;
            if (!s_rowPieces.TryGetValue(row, out var list)) s_rowPieces[row] = list = new List<GameObject>();
            list.Add(piece);
            s_table.m_pieces.Add(piece);
        }

        // Borrowed pieces arrive late: FAP builds its clutter stand-ins after world load. Drawing the hammer re-reads
        // them and rebuilds only when something changed.
        private static void RefreshBorrowed()
        {
            var lent = new List<GameObject>();
            SharedPieceGroups.Collect(SharedPieceGroups.Clutter, lent);
            if (SameRow(Row.Clutter, lent) && SameRow(Row.Voxel, VoxelToolPieces.Tools) && !BiomePaintEntries.IsStale) return;
            Rebuild();
            FiresLogger.LogInfo($"[FiresZones] Chaos Hammer menu: {Count(Row.Voxel)} V-tool(s), {Count(Row.Clutter)} clutter tool(s) "
                + $"lent by FAP{(SharedPieceGroups.IsPublished(SharedPieceGroups.Clutter) ? "" : " (FAP not installed or not ready)")}.");
            RefreshOpenMenu();
        }

        private static bool SameRow(Row row, IReadOnlyList<GameObject> pieces)
        {
            s_rowPieces.TryGetValue(row, out var list);
            int have = list?.Count ?? 0;
            if (have != pieces.Count) return false;
            for (int i = 0; i < have; i++) if (!ReferenceEquals(list[i], pieces[i])) return false;
            return true;
        }

        private static int Count(Row row) => s_rowPieces.TryGetValue(row, out var list) ? list.Count : 0;

        private sealed class ChaosHammerPages : BuildMenuPages.ISource
        {
            private static readonly List<Piece> s_probe = new List<Piece>();

            public bool Owns(PieceTable table) => s_table != null && ReferenceEquals(table, s_table);

            public string PageLabel(int page)
            {
                switch (page)
                {
                    case BuildMenuPages.Categories: return CategoriesPage;
                    case BuildMenuPages.Materials: return BiomesPage;
                    case BuildMenuPages.Recent: return BlueprintsPage;
                    default: return null;
                }
            }

            public void CollectTabs(PieceTable table, int page, List<BuildMenuTabs.Tab> into)
            {
                if (page == BuildMenuPages.Materials)
                {
                    foreach (var (biome, label) in BiomeRows)
                        if (AnyAvailable(table, page, (int)biome)) into.Add(new BuildMenuTabs.Tab(label, (int)biome));
                    return;
                }
                foreach (var (row, label) in page == BuildMenuPages.Recent ? BlueprintRows : CategoryRows)
                    if (AnyAvailable(table, page, (int)row)) into.Add(new BuildMenuTabs.Tab(label, (int)row));
            }

            public void Fill(PieceTable table, int page, int key, IList<Piece> into)
            {
                if (page != BuildMenuPages.Materials)
                {
                    AddRow((Row)key, into);
                    return;
                }
                var biome = (Heightmap.Biome)key;
                foreach (var row in BiomeSortedRows)
                {
                    if (!s_rowPieces.TryGetValue(row, out var list)) continue;
                    foreach (var go in list)
                        if ((BiomesOf(go) & biome) != 0) AddPiece(go, into);
                }
            }

            private bool AnyAvailable(PieceTable table, int page, int key)
            {
                s_probe.Clear();
                Fill(table, page, key, s_probe);
                bool any = false;
                foreach (var piece in s_probe)
                    if (table.m_availablePieces.Contains(piece)) { any = true; break; }
                s_probe.Clear();
                return any;
            }

            private static void AddRow(Row row, IList<Piece> into)
            {
                if (!s_rowPieces.TryGetValue(row, out var list)) return;
                foreach (var go in list) AddPiece(go, into);
            }

            private static void AddPiece(GameObject go, IList<Piece> into)
            {
                var piece = go != null ? go.GetComponent<Piece>() : null;
                if (piece != null) into.Add(piece);
            }
        }

        // A paint entry belongs to its ground. FAP says where each lent clutter grows; a known answer is kept until the
        // next rebuild, and none is asked again, since FAP's clutter registry fills only once the world's ClutterSystem
        // exists.
        private static Heightmap.Biome BiomesOf(GameObject piece)
        {
            if (piece == null) return Heightmap.Biome.None;
            var ground = BiomePaintEntries.GroundOf(piece);
            if (ground != Heightmap.Biome.None) return ground;
            if (s_biomes.TryGetValue(piece, out var known)) return known;
            var biomes = SharedPieceGroups.BiomesOf(SharedPieceGroups.Clutter, piece);
            if (biomes != Heightmap.Biome.None) s_biomes[piece] = biomes;
            return biomes;
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
                // The V-tools answer to their own gate, which also honours the Show Tools In Build Menu setting.
                if (piece == null || VoxelToolPieces.Names.Contains(piece.m_name)) continue;
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

        // Another mod's UpdateAvailable pass may re-seed this table's selected category (FAP's visibility pass runs on every
        // table): vanilla keeps the selection as a category + grid index, so a Paint swatch then read as the Clutter piece at
        // the same index (Fire, 2026-09-29: Swamp ground placed a brown shrub, Mountain a bear painting). The piece selected
        // before the pass is selected again after every postfix has run.
        private static Piece s_selectedBefore;
        private static bool s_selectionFixLogged;

        internal static void RememberSelection(PieceTable table)
            => s_selectedBefore = s_table != null && ReferenceEquals(table, s_table) ? table.GetSelectedPiece() : null;

        internal static void RestoreSelection(PieceTable table)
        {
            var wanted = s_selectedBefore;
            s_selectedBefore = null;
            if (wanted == null || s_table == null || !ReferenceEquals(table, s_table)) return;
            var player = Player.m_localPlayer;
            if (player == null || !ReferenceEquals(player.m_buildPieces, s_table)) return;
            var now = table.GetSelectedPiece();
            if (ReferenceEquals(now, wanted) || !table.m_availablePieces.Contains(wanted)) return;
            bool back = player.SetSelectedPiece(wanted) && ReferenceEquals(table.GetSelectedPiece(), wanted);
            string line = $"[FiresZones] Chaos Hammer: an UpdateAvailable pass moved the selection from '{wanted.m_name}' to "
                + $"'{(now != null ? now.m_name : "nothing")}'; {(back ? "put back" : "could NOT put it back")}.";
            if (!back || !s_selectionFixLogged) FiresLogger.LogWarning(line);
            else FiresLogger.LogVerbose(line);
            s_selectionFixLogged = true;
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
            => piece != null && (s_placeByPiece.ContainsKey(piece) || Is(piece, s_copyTool) || Is(piece, s_removeTool)
                || Is(piece, s_undoTool) || Is(piece, s_redoTool) || Is(piece, s_saveAreaTool) || Is(piece, s_saveSkylandTool)
                || BiomePaintEntries.TryGet(piece, out _));

        private static bool Is(Piece piece, GameObject tool) => tool != null && ReferenceEquals(piece.gameObject, tool);

        // ── The swing ───────────────────────────────────────────────────────
        // Called from FAT's one Player.TryPlacePiece hook. Nothing is ever built.
        internal static void OnPlace(Player player, Piece piece)
        {
            if (player == null || piece == null) return;
            ZoneModeChaosHammer.Laugh(player);
            if (Is(piece, s_undoTool)) { Ask(AskUndo, null, default); return; }
            if (Is(piece, s_redoTool)) { Ask(AskRedo, null, default); return; }
            if (BiomePaintEntries.TryGet(piece, out var paint))
            {
                if (!ZoneModeAreaTool.TryGetAim(out var ground))
                {
                    Tell("Aim at the ground you mean to paint.");
                    return;
                }
                Ask(AskPaint, paint.Key, default, PaintPayload(ground, ZoneModeAreaTool.PaintRadius));
                return;
            }
            bool skylandEntry = s_placeByPiece.TryGetValue(piece, out string key) && KindOf(key) == SkylandKind;
            if (skylandEntry || Is(piece, s_saveSkylandTool))
            {
                if (!ZoneModeAreaTool.TryGetAim(out var target))
                {
                    Tell(skylandEntry ? "Aim at the spot the skyland's middle should go over." : "Aim at the skyland you mean.");
                    return;
                }
                if (skylandEntry) Ask(AskPlaceSkyland, NameOf(key), default, PointPayload(target));
                else SaveSkylandDialog(target);
                return;
            }
            bool areaEntry = key != null && KindOf(key) == AreaKind;
            if (areaEntry || Is(piece, s_saveAreaTool))
            {
                if (!ZoneModeAreaTool.TryGetAim(out var aim))
                {
                    Tell("Aim at the land, rock or build you mean.");
                    return;
                }
                if (areaEntry) Ask(AskPlaceArea, NameOf(key), default, PointPayload(aim));
                else SaveAreaDialog(aim);
                return;
            }
            if (!ZoneModeCommands.TryGetLookedAtZone(out var zone))
            {
                Tell("Look at the ground of the chunk you mean.");
                return;
            }
            if (key != null)
            {
                Ask(AskPlace, NameOf(key), zone);
                return;
            }
            if (Is(piece, s_removeTool))
            {
                RemoveDialog(zone);
                return;
            }
            SaveDialog(zone);
        }

        private static void RemoveDialog(Vector2s zone)
        {
            if (s_confirming) return;
            if (!UnifiedPopup.IsAvailable())
            {
                Ask(AskRemove, null, zone);
                return;
            }
            s_confirming = true;
            InputBlock.Block(true);
            UnifiedPopup.Push(new YesNoPopup($"Take back chunk ({zone.x},{zone.y})?",
                "Everything in it goes, land and builds, and it closes to sea so a different chunk can be placed there. "
                + $"It is set aside, not deleted: fires_zones_undo {zone.x} {zone.y} brings it back.",
                () => AnswerRemove(true, zone), () => AnswerRemove(false, zone), false));
        }

        private static void AnswerRemove(bool remove, Vector2s zone)
        {
            if (!s_confirming) return;
            s_confirming = false;
            UnifiedPopup.Pop();
            InputBlock.Block(false);
            if (remove) Ask(AskRemove, null, zone);
        }

        // A popup closed by anything but its own buttons must not leave the controls blocked.
        internal static void ClientTick()
        {
            if (!s_confirming || UnifiedPopup.IsVisible()) return;
            s_confirming = false;
            InputBlock.Block(false);
        }

        // The admin's own client photographs the chunk as it saves it; the picture rides with the save.
        private static void SaveDialog(Vector2s zone)
        {
            var name = new MenuDialog.TextField("Name", $"chunk_{zone.x}_{zone.y}");
            var overwrite = new MenuDialog.CheckField("Replace one of the same name", false);
            MenuDialog.Show($"Save chunk ({zone.x},{zone.y})", new MenuDialog.Field[] { name, overwrite },
                () => Ask(overwrite.Value ? (byte)(AskSave | AskOverwrite) : AskSave, name.Value.Trim(), zone,
                    ZoneModeBlueprintIcons.TakeSnapshot(zone)),
                validate: () => ZoneModeChunkBlueprint.IsUsableName(name.Value.Trim(), out string problem) ? null : problem,
                okLabel: "Save");
        }

        // The ring's numbers, editable, and a name. The admin's client photographs the area as it saves it.
        private static void SaveAreaDialog(Vector3 aim)
        {
            ZoneModeAreaTool.Shape(aim, out int radius, out int bottom, out int top);
            int x = Mathf.RoundToInt(aim.x), z = Mathf.RoundToInt(aim.z);
            var name = new MenuDialog.TextField("Name", $"area_{x}_{z}");
            var radiusField = new MenuDialog.TextField("Radius (m)", radius.ToString());
            var bottomField = new MenuDialog.TextField("Bottom (y)", bottom.ToString());
            var topField = new MenuDialog.TextField("Top (y)", top.ToString());
            var overwrite = new MenuDialog.CheckField("Replace one of the same name", false);
            MenuDialog.Show($"Save area round ({x},{z})", new MenuDialog.Field[] { name, radiusField, bottomField, topField, overwrite },
                () =>
                {
                    ParseShape(radiusField.Value, bottomField.Value, topField.Value, out int r, out int b, out int t);
                    var focus = new Vector3(x, (b + t) * 0.5f, z);
                    Ask(overwrite.Value ? (byte)(AskSaveArea | AskOverwrite) : AskSaveArea, name.Value.Trim(), default,
                        AreaPayload(new Vector3(x, aim.y, z), r, b, t, ZoneModeBlueprintIcons.TakeSnapshot(focus, r * 2f)));
                },
                validate: () =>
                {
                    if (!ZoneModeChunkBlueprint.IsUsableName(name.Value.Trim(), out string problem)) return problem;
                    if (!ParseShape(radiusField.Value, bottomField.Value, topField.Value, out int r, out int b, out int t))
                        return "Radius, bottom and top must be whole numbers.";
                    return ZoneModeAreaBlueprint.IsUsableShape(r, b, t, out problem) ? null : problem;
                },
                okLabel: "Save");
        }

        // A name for the skyland under the aim; the admin's client photographs the whole island as it saves it. Its land
        // must be loaded here (stand on or near it), because the aim has to land on it.
        private static void SaveSkylandDialog(Vector3 aim)
        {
            if (!FiresSkyIslands.TrySkylandAt(aim, out int index))
            {
                Tell("That is not a skyland. Stand on or near one and aim at its land.");
                return;
            }
            FiresSkyIslands.IslandFootprint(index, out var centre, out _);
            FiresSkyIslands.SkylandFrame(index, out var focus, out float span);
            var name = new MenuDialog.TextField("Name", $"skyland_{Mathf.RoundToInt(centre.x)}_{Mathf.RoundToInt(centre.z)}");
            var overwrite = new MenuDialog.CheckField("Replace one of the same name", false);
            MenuDialog.Show($"Save the {FiresSkyIslands.SkylandBiome(index)} skyland", new MenuDialog.Field[] { name, overwrite },
                () => Ask(overwrite.Value ? (byte)(AskSaveSkyland | AskOverwrite) : AskSaveSkyland, name.Value.Trim(), default,
                    SkylandPayload(aim, ZoneModeBlueprintIcons.TakeSnapshot(focus, span))),
                validate: () => ZoneModeChunkBlueprint.IsUsableName(name.Value.Trim(), out string problem) ? null : problem,
                okLabel: "Save");
        }

        private static byte[] SkylandPayload(Vector3 aim, byte[] snapshot)
        {
            var pkg = new ZPackage();
            pkg.Write(aim);
            pkg.Write(snapshot ?? Array.Empty<byte>());
            return pkg.GetArray();
        }

        private static bool ParseShape(string radius, string bottom, string top, out int r, out int b, out int t)
        {
            b = t = 0;
            return int.TryParse(radius?.Trim(), out r) & int.TryParse(bottom?.Trim(), out b) & int.TryParse(top?.Trim(), out t);
        }

        private static byte[] AreaPayload(Vector3 centre, int radius, int bottom, int top, byte[] snapshot)
        {
            var pkg = new ZPackage();
            pkg.Write(centre);
            pkg.Write(radius);
            pkg.Write(bottom);
            pkg.Write(top);
            pkg.Write(snapshot ?? Array.Empty<byte>());
            return pkg.GetArray();
        }

        private static byte[] PaintPayload(Vector3 centre, int radius)
        {
            var pkg = new ZPackage();
            pkg.Write(centre);
            pkg.Write(radius);
            return pkg.GetArray();
        }

        private static byte[] PointPayload(Vector3 point)
        {
            var pkg = new ZPackage();
            pkg.Write(point);
            return pkg.GetArray();
        }

        // The saved names come from the server, so the grid holds what is really on disk. Asked when the hammer is drawn,
        // which is also when late borrowed pieces are picked up.
        internal static void RefreshNames()
        {
            RefreshBorrowed();
            Ask(AskList, null, default);
        }

        // ── Asking the server ───────────────────────────────────────────────
        // `payload` is a save's snapshot, and empty otherwise.
        private static void Ask(byte what, string name, Vector2s zone, byte[] payload = null)
        {
            var net = ZNet.instance;
            if (net == null) return;
            if (net.IsServer())
            {
                Answer(ZNet.GetUID(), what, name, zone, payload, local: true);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(what);
            pkg.Write(name ?? string.Empty);
            pkg.Write(zone);
            pkg.Write(payload ?? Array.Empty<byte>());
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk, pkg);
        }

        private static void RPC_Ask(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            byte what = pkg.ReadByte();
            string name = pkg.ReadString();
            var zone = pkg.ReadVector2s();
            byte[] payload = pkg.ReadByteArray();
            if (!AdminSyncing.IsAdmin(sender))
            {
                Send(sender, TellMessage, "The Chaos Hammer's tools are an admin's.");
                return;
            }
            Answer(sender, what, name, zone, payload, local: false);
        }

        private static void Answer(long who, byte what, string name, Vector2s zone, byte[] payload, bool local)
        {
            bool overwrite = (what & AskOverwrite) != 0;
            int kind = what & AskKind;
            switch (kind)
            {
                case AskRemove:
                {
                    ZoneModeChunkRemoval.TryRemove(zone, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    return;
                }
                case AskUndo:
                case AskRedo:
                {
                    string undone;
                    if (kind == AskUndo) ZoneModeChunkBlueprint.TryUndo(out undone);
                    else ZoneModeChunkBlueprint.TryRedo(out undone);
                    FiresLogger.LogInfo("[FiresZones] " + undone);
                    Reply(who, local, TellMessage, undone);
                    return;
                }
                case AskSave:
                {
                    ZoneModeChunkBlueprint.TrySave(zone, name, overwrite, payload, out string report);
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
                case AskSaveArea:
                {
                    var args = new ZPackage(payload ?? Array.Empty<byte>());
                    Vector3 centre = args.ReadVector3();
                    int radius = args.ReadInt(), bottom = args.ReadInt(), top = args.ReadInt();
                    byte[] snapshot = args.ReadByteArray();
                    ZoneModeAreaBlueprint.TrySave(centre, radius, bottom, top, name, overwrite, snapshot, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    Names(who, local);
                    return;
                }
                case AskPlaceArea:
                {
                    Vector3 target = new ZPackage(payload ?? Array.Empty<byte>()).ReadVector3();
                    ZoneModeAreaBlueprint.TryPlace(name, target, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    return;
                }
                case AskSaveSkyland:
                {
                    var args = new ZPackage(payload ?? Array.Empty<byte>());
                    Vector3 aim = args.ReadVector3();
                    byte[] snapshot = args.ReadByteArray();
                    ZoneModeSkylandBlueprint.TrySave(aim, name, overwrite, snapshot, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    Names(who, local);
                    return;
                }
                case AskPlaceSkyland:
                {
                    Vector3 target = new ZPackage(payload ?? Array.Empty<byte>()).ReadVector3();
                    ZoneModeSkylandBlueprint.TryPlace(name, target, out string report);
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    return;
                }
                case AskPaint:
                {
                    var args = new ZPackage(payload ?? Array.Empty<byte>());
                    Vector3 centre = args.ReadVector3();
                    int radius = args.ReadInt();
                    if (BiomePaint.TryPaint(name, centre, radius, who, out uint id, out string report))
                        ZoneModeChunkBlueprint.RecordPaint(id, $"the {BiomePaint.Describe(name)} paint");
                    FiresLogger.LogInfo("[FiresZones] " + report);
                    Reply(who, local, TellMessage, report);
                    return;
                }
                case AskList:
                    Names(who, local);
                    return;
                case AskIcon:
                    SendIcon(who, name, local);
                    return;
            }
        }

        // The names on disk, each with the hash of its picture, so a client asks only for pictures it lacks.
        private static void Names(long who, bool local)
        {
            var names = new List<string>();
            foreach (string line in ZoneModeChunkBlueprint.List())
            {
                int colon = line.IndexOf(':');
                names.Add(KeyOf(ChunkKind, colon > 0 ? line.Substring(0, colon) : line));
            }
            foreach (string area in ZoneModeAreaBlueprint.Names()) names.Add(KeyOf(AreaKind, area));
            foreach (string skyland in ZoneModeSkylandBlueprint.Names()) names.Add(KeyOf(SkylandKind, skyland));
            var hashes = new List<uint>(names.Count);
            foreach (string key in names)
            {
                string path = PathOf(key);
                if (KindOf(key) == ChunkKind) ZoneModeChunkBlueprint.EnsureMap(path);
                hashes.Add(ZoneModeBlueprintIcons.TryLoad(path, out _, out byte[] data) ? ZoneModeBlueprintIcons.Hash(data) : 0u);
            }
            if (local)
            {
                Adopt(names, hashes, local: true);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(TellList);
            pkg.Write(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                pkg.Write(names[i]);
                pkg.Write(hashes[i]);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who, RpcTell, pkg);
        }

        private static void SendIcon(long who, string name, bool local)
        {
            string path = PathOf(name);
            if (!ZoneModeBlueprintIcons.TryLoad(path, out var kind, out byte[] data)) return;
            if (local)
            {
                TakeIcon(name, kind, data);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(TellIcon);
            pkg.Write(name);
            pkg.Write((byte)kind);
            pkg.Write(data);
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

        // A message for whoever asked, sent after the answer (a paint's growth finishes a moment later).
        internal static void Notify(long who, string message)
        {
            if (ZNet.instance != null && who == ZNet.GetUID()) Tell(message);
            else Send(who, TellMessage, message);
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
            if (kind == TellIcon)
            {
                string name = pkg.ReadString();
                var pictureKind = (ZoneModeBlueprintIcons.Kind)pkg.ReadByte();
                TakeIcon(name, pictureKind, pkg.ReadByteArray());
                return;
            }
            int count = pkg.ReadInt();
            var names = new List<string>(count);
            var hashes = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                names.Add(pkg.ReadString());
                hashes.Add(pkg.ReadUInt());
            }
            Adopt(names, hashes, local: false);
        }

        private static void Adopt(List<string> names, List<uint> hashes, bool local)
        {
            if (!Same(names))
            {
                s_saved.Clear();
                s_saved.AddRange(names);
                Rebuild();
                RefreshOpenMenu();
            }
            for (int i = 0; i < names.Count; i++)
            {
                s_wantedHash[names[i]] = hashes[i];
                if (hashes[i] == 0u) continue;
                if (s_icons.TryGetValue(names[i], out var have) && have.Hash == hashes[i]) continue;
                if (local) SendIcon(ZNet.GetUID(), names[i], local: true);
                else Ask(AskIcon, names[i], default);
            }
        }

        // ── Pictures on the client ──────────────────────────────────────────
        private static readonly Dictionary<string, (uint Hash, Sprite Sprite)> s_icons = new Dictionary<string, (uint, Sprite)>();
        private static readonly Dictionary<string, uint> s_wantedHash = new Dictionary<string, uint>();

        private static void TakeIcon(string name, ZoneModeBlueprintIcons.Kind kind, byte[] data)
        {
            var sprite = ZoneModeBlueprintIcons.ToSprite(kind, data);
            if (sprite == null) return;
            s_icons[name] = (s_wantedHash.TryGetValue(name, out uint hash) ? hash : ZoneModeBlueprintIcons.Hash(data), sprite);
            foreach (var pair in s_placeByPiece)
                if (pair.Value == name) pair.Key.m_icon = sprite;
            RefreshOpenMenu();
        }

        private static Sprite IconFor(string name) => s_icons.TryGetValue(name, out var have) ? have.Sprite : s_icon;

        private static void RefreshOpenMenu()
        {
            var player = Player.m_localPlayer;
            if (player == null || !ZoneModeChaosHammer.IsHolding(player)) return;
            RefreshPieces.Invoke(player, null);
            BuildMenuTabs.RequestRefresh();
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
