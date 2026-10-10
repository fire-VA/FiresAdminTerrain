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
        // 0.2.718 (Fire 10-05, [worldgen]'s FAT_CLOSE_CHUNKS_DESIGN.md): close chunks on a full voxel world and open them again. The
        // park and the registry are [worldgen]'s (ZoneModePark, ZoneModeOpening); these are the Chaos Hammer's entries.
        private const byte AskClose = 13;       // the looked-at zone
        private const byte AskCloseArea = 14;   // it and the 8 round it
        private const byte AskOpen = 15;        // the looked-at zone
        private const byte AskParkList = 16;    // undo of an open / redo of a close: exactly these zones (payload)
        private const byte AskOpenList = 17;    // undo of a close / redo of an open: exactly these zones (payload)
        private const byte AskOverwrite = 0x80;
        private const byte AskKind = 0x7F;
        private const byte TellMessage = 1;
        private const byte TellList = 2;
        private const byte TellIcon = 3;
        private const byte TellZoneStep = 5;   // a close or an open made for this admin: the zones it really changed (0.2.718)
        private const byte TellStep = 4;   // a placement or paint made for this admin: one step of the shared undo history (0.2.716)

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
        // 0.2.718: runtime copies of the Remove tool (same badge, own names).
        private static GameObject s_closeTool, s_closeAreaTool, s_openTool;
        private const string CloseToolObject = "fires_chunktool_close", CloseAreaToolObject = "fires_chunktool_closearea", OpenToolObject = "fires_chunktool_open";

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
            SizeToolsLikeSeed();   // before any copy is made, so the copies are seed-sized too
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
            EnsureCloseTools();
            foreach (var tool in new[] { s_removeTool, s_closeTool, s_closeAreaTool, s_openTool, s_undoTool, s_redoTool })
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

        // 0.2.768 (Fire 10-06: "the ones that still use the large marker should be a seed the size of the one the chunk
        // generator spits out, not oversized"). The Chunk tools' models (and so their placement ghosts) are scaled to the
        // in-world size of the Chunk Seed the generator drops (FiresChunkSpawner_Plain, same bundle), measured, not guessed:
        // both sizes are the largest side of their meshes' bounds in the prefab's own space. Done once per source prefab,
        // before the copies (Save area, Save skyland, Close/Open, saved blueprints) are made from them.
        private const string SeedPrefab = "FiresChunkSpawner_Plain";
        private static readonly HashSet<GameObject> s_seedSized = new HashSet<GameObject>();

        private static void SizeToolsLikeSeed()
        {
            if (!ZoneModePrefabs.TryGet(SeedPrefab, out var seed) || seed == null) return;
            float seedSize = ModelSize(seed);
            if (seedSize <= 0f) return;
            var sized = new List<string>();
            foreach (var tool in new[] { s_copyTool, s_placeTemplate, s_removeTool, s_undoTool, s_redoTool })
            {
                if (tool == null || !s_seedSized.Add(tool)) continue;
                float size = ModelSize(tool);
                if (size <= 0f || Mathf.Abs(size - seedSize) < 0.001f) continue;
                float factor = seedSize / size;
                tool.transform.localScale *= factor;
                sized.Add($"{tool.name} x{factor:0.###}");
            }
            if (sized.Count > 0)
                FiresLogger.LogDebug($"[FiresZones] Chaos Hammer tools sized like a Chunk Seed ({seedSize:0.###} m): {string.Join(", ", sized)}");
        }

        // The largest side of the model's mesh bounds, in the root's space at the root's own scale (works on inactive prefabs,
        // where Renderer.bounds is empty).
        private static float ModelSize(GameObject root)
        {
            bool any = false;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Matrix4x4 toRoot = Matrix4x4.Scale(root.transform.localScale) * root.transform.worldToLocalMatrix;
            void Take(Mesh mesh, Transform t)
            {
                if (mesh == null) return;
                var b = mesh.bounds;
                Matrix4x4 m = toRoot * t.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
                }
            }
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.GetComponent<Renderer>() != null) Take(mf.sharedMesh, mf.transform);
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Take(smr.sharedMesh, smr.transform);
            if (!any) return 0f;
            var size = max - min;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
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

        // Close chunk / Close area / Open chunk: copies of the Remove tool, which they sit beside in the Chunk tools row.
        private static void EnsureCloseTools()
        {
            if (s_removeTool == null) return;
            if (s_closeTool == null) s_closeTool = CopyOfRemove(CloseToolObject, "Close chunk",
                "Look at a chunk and swing: it closes to sea, and its builds and everything on it are set aside until it opens again.");
            if (s_closeAreaTool == null) s_closeAreaTool = CopyOfRemove(CloseAreaToolObject, "Close area (3x3)",
                "Look at a chunk and swing: it and the eight round it close to sea, their builds set aside until they open again.");
            if (s_openTool == null) s_openTool = CopyOfRemove(OpenToolObject, "Open chunk",
                "Look at a closed chunk and swing: it opens, and what was set aside comes back.");
        }

        private static GameObject CopyOfRemove(string objectName, string name, string description)
        {
            var copy = UnityEngine.Object.Instantiate(s_removeTool);
            copy.name = objectName;
            copy.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(copy);
            var piece = copy.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_name = name;
                piece.m_description = description;
            }
            return copy;
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

        // 0.2.753 (Fire 10-06: "it should highlight the chunk like placement does"): the chunk tool in hand, for the
        // highlight (ZoneModeAreaTool.DrawZone): what it does and whether it reaches the 3x3 round the aimed zone.
        internal static bool TryGetZoneTool(Player player, out string action, out bool area)
        {
            action = null;
            area = false;
            if (player == null || s_table == null || !ReferenceEquals(player.m_buildPieces, s_table)) return false;
            var selected = s_table.GetSelectedPiece();
            if (selected == null) return false;
            var go = selected.gameObject;
            if (s_closeTool != null && ReferenceEquals(go, s_closeTool)) action = "Close";
            else if (s_closeAreaTool != null && ReferenceEquals(go, s_closeAreaTool)) { action = "Close"; area = true; }
            else if (s_openTool != null && ReferenceEquals(go, s_openTool)) action = "Open";
            else if (s_removeTool != null && ReferenceEquals(go, s_removeTool)) action = "Take back";
            return action != null;
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
        private static bool s_selectingClick;   // 0.2.769: set while Player_SetSelectedPiece_ByReference_Patch applies a click
        private static bool s_selectionFixLogged;

        internal static void RememberSelection(PieceTable table)
            => s_selectedBefore = s_table != null && ReferenceEquals(table, s_table) ? table.GetSelectedPiece() : null;

        internal static void RestoreSelection(PieceTable table)
        {
            var wanted = s_selectedBefore;
            s_selectedBefore = null;
            if (wanted == null || s_table == null || !ReferenceEquals(table, s_table)) return;
            if (s_selectingClick) return;   // 0.2.769: a click is being applied right now; putting the old piece back would undo it
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

        // 0.2.764 (Fire 10-06: "clicking into our clutter tab picks the wrong things, like take back this chunk or Vdelete").
        // The build menu hands the clicked Piece to Player.SetSelectedPiece(Piece), and vanilla finds it again by PREFAB NAME
        // in the per-category lists and selects category = piece.m_category at that list's slot. The Chaos Hammer's entries
        // are clones and lent pieces whose names can match another entry, or that sit in a list other than their m_category,
        // so the slot lands on a different tool. On the Chaos Hammer the clicked piece itself is looked up BY REFERENCE and
        // exactly its category and slot are selected (every page: categories, biomes, blueprints, favorites, the filter box).
        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.SetSelectedPiece), new[] { typeof(Piece) })]
        private static class Player_SetSelectedPiece_ByReference_Patch
        {
            private const int GridWidth = 15;   // vanilla's GetPieceIndex: x = j % 15, y = j / 15

            private static int FindCategory(List<List<Piece>> lists, Piece p)
            {
                if (lists == null) return -1;
                for (int c = 0; c < lists.Count; c++) if (lists[c] != null && lists[c].IndexOf(p) >= 0) return c;   // by reference
                return -1;
            }

            // Vanilla sizes the per-category selection slots for its 9 categories; the mods' categories go past that (15 here).
            // GetSelectedIndex clamps to the last slot, SetSelected would write past it, so both arrays reach category c.
            private static void EnsureSlotArrays(PieceTable table, int c)
            {
                if (table.m_selectedPiece == null || table.m_selectedPiece.Length <= c) System.Array.Resize(ref table.m_selectedPiece, c + 1);
                if (table.m_lastSelectedPiece == null || table.m_lastSelectedPiece.Length <= c) System.Array.Resize(ref table.m_lastSelectedPiece, c + 1);
            }

            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(Player __instance, Piece p, ref bool __result)
            {
                try
                {
                    var table = __instance != null ? __instance.m_buildPieces : null;
                    if (p == null || s_table == null || !ReferenceEquals(table, s_table)) return true;
                    var lists = ByCategory(table);
                    if (lists == null) return true;
                    int c = FindCategory(lists, p);
                    if (c < 0) return true;   // not in the lists by reference: vanilla's own lookup

                    // 0.2.769: the CATEGORY VALUE, through the enum overload. 0.2.764 called SetBuildCategory(int), which is
                    // PieceTable.SetCategory(int) = m_categories[index], a position in the category LIST, so category 15
                    // selected some other category and the slot was read from the wrong list (Fire: "still chooses the
                    // wrong things"; the read-back Warning showed 'Lantern...', 'Close area (3x3)', 'Vskin').
                    EnsureSlotArrays(table, c);
                    s_selectingClick = true;
                    try { __instance.SetBuildCategory((Piece.PieceCategory)c); }
                    finally { s_selectingClick = false; }
                    // SetBuildCategory re-runs UpdateAvailablePiecesList, which rebuilds the lists: find the piece again.
                    lists = ByCategory(table);
                    c = FindCategory(lists, p);
                    if (c < 0) return true;
                    if ((int)table.GetSelectedCategory() != c) table.SetCategory((Piece.PieceCategory)c);
                    int j = lists[c].IndexOf(p);
                    var slot = new Vector2Int(j % GridWidth, j / GridWidth);
                    EnsureSlotArrays(table, c);
                    __instance.SetSelectedPiece(slot);
                    if (!ReferenceEquals(table.GetSelectedPiece(), p))
                    {
                        // Last resort: the table's own slot for this category, written directly, and the ghost rebuilt.
                        table.m_selectedPiece[c] = slot;
                        __instance.SetupPlacementGhost();
                    }
                    __result = ReferenceEquals(table.GetSelectedPiece(), p);
                    if (__result) FiresLogger.LogDebug($"[FiresZones] Chaos Hammer: selected '{p.m_name}' (category {c} slot {j})");
                    else FiresLogger.LogWarning($"[FiresZones] Chaos Hammer: '{p.m_name}' is in category {c} slot {j} but the selection reads "
                        + $"'{table.GetSelectedPiece()?.m_name ?? "nothing"}' (selected category {(int)table.GetSelectedCategory()}).");
                    return false;
                }
                catch (System.Exception e) { FiresLogger.LogWarning("[FiresZones] Chaos Hammer selection: " + e.Message); return true; }
            }
        }

        // 0.2.769: fat_hammer_selftest - with the Chaos Hammer in hand, selects EVERY piece of its table through the same call
        // a menu click makes (Player.SetSelectedPiece(Piece)) and reads the selection back. One line per mismatch and a summary;
        // the original selection is put back. The deterministic check for "clicking X selects X".
        internal static void RegisterSelfTest()
        {
            new Terminal.ConsoleCommand("fat_hammer_selftest",
                "- with the Chaos Hammer equipped: select every tool as a menu click does and check each selection reads back as that tool",
                args =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null || s_table == null || !ReferenceEquals(player.m_buildPieces, s_table))
                    { args.Context?.AddString("fat_hammer_selftest: equip the Chaos Hammer first"); return; }
                    var lists = ByCategory(s_table);
                    if (lists == null) { args.Context?.AddString("fat_hammer_selftest: no category lists"); return; }
                    var before = s_table.GetSelectedPiece();
                    var all = new List<Piece>();
                    foreach (var list in lists) if (list != null) foreach (var piece in list) if (piece != null && !all.Contains(piece)) all.Add(piece);
                    int ok = 0; var bad = new List<string>();
                    foreach (var piece in all)
                    {
                        player.SetSelectedPiece(piece);
                        var got = s_table.GetSelectedPiece();
                        if (ReferenceEquals(got, piece)) ok++;
                        else bad.Add($"'{piece.m_name}' -> '{got?.m_name ?? "nothing"}'");
                    }
                    if (before != null) player.SetSelectedPiece(before);
                    string summary = $"fat_hammer_selftest: {ok} of {all.Count} tool(s) select themselves" + (bad.Count > 0 ? $"; WRONG {bad.Count}: {string.Join(", ", bad)}" : "; none wrong");
                    args.Context?.AddString(summary);
                    if (bad.Count > 0) FiresLogger.LogWarning("[FiresZones] " + summary); else FiresLogger.LogInfo("[FiresZones] " + summary);
                });
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
                || Is(piece, s_closeTool) || Is(piece, s_closeAreaTool) || Is(piece, s_openTool)
                || Is(piece, s_undoTool) || Is(piece, s_redoTool) || Is(piece, s_saveAreaTool) || Is(piece, s_saveSkylandTool)
                || BiomePaintEntries.TryGet(piece, out _));

        private static bool Is(Piece piece, GameObject tool) => tool != null && ReferenceEquals(piece.gameObject, tool);

        // ── The swing ───────────────────────────────────────────────────────
        // Called from FAT's one Player.TryPlacePiece hook. Nothing is ever built.
        internal static void OnPlace(Player player, Piece piece)
        {
            if (player == null || piece == null) return;
            ZoneModeChaosHammer.Laugh(player);
            // The hammer's Undo / Redo step the ONE history (Core's, or Server Devcommands'), as numpad 7 / 9 do (0.2.716).
            if (Is(piece, s_undoTool)) { FiresCore.Undo.UndoHistory.Undo(); return; }
            if (Is(piece, s_redoTool)) { FiresCore.Undo.UndoHistory.Redo(); return; }
            if (BiomePaintEntries.TryGet(piece, out var paint))
            {
                if (!ZoneModeAreaTool.TryGetAim(out var ground))
                {
                    Tell("Aim at the ground you mean to paint.");
                    return;
                }
                // 0.2.758 (Fire 10-06: every tool keeps to the layer it is aimed at): aimed at a cave floor or a skyland, the paint
                // turns that layer alone, in its chunks (VoxelLayerPaint: synced, saved and undone with them); the ground's circles
                // stay the server's.
                if (AdminSyncing.IsLocalAdmin() && BiomePaint.TryParseKey(paint.Key, out var brush, out var biome, out _))
                {
                    byte code = brush == BiomePaint.Brush.Restore ? (byte)0 : VoxelLayerPaint.BiomeCode(biome);
                    string what = $"the {BiomePaint.Describe(paint.Key)} paint";
                    if (VoxelLayerPaint.TryPaint(ground, ZoneModeAreaTool.PaintRadius, false, 0, true, code, what))
                    {
                        Tell(brush == BiomePaint.Brush.Restore
                            ? "This layer's own biome comes back here."
                            : $"This layer turns {biome} here{(brush == BiomePaint.Brush.Blend ? " (a blend paints the ground only; here it is the plain biome)" : "")}.");
                        return;
                    }
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
            if (Is(piece, s_closeTool) || Is(piece, s_closeAreaTool))
            {
                CloseDialog(zone, Is(piece, s_closeAreaTool));
                return;
            }
            if (Is(piece, s_openTool))
            {
                Ask(AskOpen, null, zone);
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

        private static void CloseDialog(Vector2s zone, bool area)
        {
            byte kind = area ? AskCloseArea : AskClose;
            if (s_confirming) return;
            if (!UnifiedPopup.IsAvailable())
            {
                Ask(kind, null, zone);
                return;
            }
            s_confirming = true;
            InputBlock.Block(true);
            string what = area ? $"the 3x3 chunks round ({zone.x},{zone.y})" : $"chunk ({zone.x},{zone.y})";
            UnifiedPopup.Push(new YesNoPopup($"Close {what}?",
                "It closes to sea. Its builds and everything on it are set aside, not deleted, and come back when it opens "
                + "(Open chunk, a Chunk Seed, a quest, or numpad 7 to undo).",
                () => AnswerClose(true, kind, zone), () => AnswerClose(false, kind, zone), false));
        }

        private static void AnswerClose(bool close, byte kind, Vector2s zone)
        {
            if (!s_confirming) return;
            s_confirming = false;
            UnifiedPopup.Pop();
            InputBlock.Block(false);
            if (close) Ask(kind, null, zone);
        }

        // Zones in a payload: count, then x, y each.
        private static byte[] ZonesPayload(IList<Vector2s> zones)
        {
            var pkg = new ZPackage();
            pkg.Write(zones.Count);
            foreach (var z in zones) pkg.Write(z);
            return pkg.GetArray();
        }

        private static List<Vector2s> ReadZones(byte[] payload)
        {
            var zones = new List<Vector2s>();
            if (payload == null || payload.Length == 0) return zones;
            var pkg = new ZPackage(payload);
            int n = pkg.ReadInt();
            for (int i = 0; i < n; i++) zones.Add(pkg.ReadVector2s());
            return zones;
        }

        private static string ZonesText(IList<Vector2s> zones)
            => zones.Count == 1 ? $"chunk ({zones[0].x},{zones[0].y})" : $"{zones.Count} chunks round ({zones[zones.Count / 2].x},{zones[zones.Count / 2].y})";

        // Server: close (park) or open exactly these zones for `who`; the report goes back, and a fresh close / open (not an undo or
        // redo of one) becomes that admin's undo step with the zones it really changed. A close runs over ~2-3 s with players connected
        // ([worldgen]: ownership to the server, clients stop writing, park, clients drop the ids): TryParkOpenZones answers at once
        // (refused, or "Closing …"), and its `done` brings the zones really parked and the final report.
        private static void CloseOrOpen(long who, bool local, bool close, IList<Vector2s> zones, bool fresh)
        {
            if (close)
            {
                ZoneModePark.TryParkOpenZones(zones, who, (parked, final) => Finish(who, local, true, parked, final, fresh), out string first);
                FiresLogger.LogInfo("[FiresZones] " + first);
                Reply(who, local, TellMessage, first);
                return;
            }
            var opened = ZoneModeOpening.Open(zones, ZoneOpenReason.Admin, who, "Chaos Hammer " + (fresh ? "Open chunk" : "undo / redo")) ?? new List<Vector2s>();
            Finish(who, local, false, opened, opened.Count == 0 ? $"{ZonesText(zones)}: nothing to open (already open?)"
                : $"Opened {ZonesText(opened)}; what was set aside comes back as the chunk loads.", fresh);
        }

        private static void Finish(long who, bool local, bool close, List<Vector2s> changed, string report, bool fresh)
        {
            FiresLogger.LogInfo("[FiresZones] " + report);
            Reply(who, local, TellMessage, report);
            if (!fresh || changed == null || changed.Count == 0) return;
            if (local) { RecordZoneStep(close, changed); return; }
            var pkg = new ZPackage();
            pkg.Write(TellZoneStep);
            pkg.Write(close);
            pkg.Write(ZonesPayload(changed));
            ZRoutedRpc.instance.InvokeRoutedRPC(who, RpcTell, pkg);
        }

        // Client: one step of the shared undo history (numpad 7 / 9). Undo opens what the close closed, or re-closes what the open
        // opened; redo does it again; the server answers each with its own report.
        private static void RecordZoneStep(bool closed, List<Vector2s> zones)
        {
            string what = (closed ? "closing " : "opening ") + ZonesText(zones);
            byte[] payload = ZonesPayload(zones);
            FatUndo.Record(what,
                () => { Ask(closed ? AskOpenList : AskParkList, null, default, payload); return $"Undoing {what}…"; },
                () => { Ask(closed ? AskParkList : AskOpenList, null, default, payload); return $"Redoing {what}…"; });
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
                case AskClose:
                case AskCloseArea:
                {
                    var zones = new List<Vector2s> { zone };
                    if (kind == AskCloseArea)
                    {
                        zones.Clear();
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++) zones.Add(new Vector2s(zone.x + dx, zone.y + dy));
                    }
                    CloseOrOpen(who, local, true, zones, fresh: true);
                    return;
                }
                case AskOpen:
                    CloseOrOpen(who, local, false, new List<Vector2s> { zone }, fresh: true);
                    return;
                case AskParkList:
                case AskOpenList:
                    CloseOrOpen(who, local, kind == AskParkList, ReadZones(payload), fresh: false);
                    return;
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
                    if (ZoneModeChunkBlueprint.TryPlace(zone, name, out string report)) Step(who, local, $"the chunk '{name}' placed at ({zone.x},{zone.y})");
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
                    if (ZoneModeAreaBlueprint.TryPlace(name, target, out string report)) Step(who, local, $"the area '{name}' placed");
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
                    if (ZoneModeSkylandBlueprint.TryPlace(name, target, out string report)) Step(who, local, $"the skyland '{name}' placed");
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
                    {
                        ZoneModeChunkBlueprint.RecordPaint(id, $"the {BiomePaint.Describe(name)} paint");
                        Step(who, local, $"the {BiomePaint.Describe(name)} paint");
                    }
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

        // The server made an undoable edit for `who`: it becomes a step of that admin's shared undo history.
        private static void Step(long who, bool local, string what)
        {
            if (local) { ZoneModeChunkKeys.RecordServerStep(what); return; }
            Send(who, TellStep, what);
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
            if (kind == TellStep)
            {
                ZoneModeChunkKeys.RecordServerStep(pkg.ReadString());
                return;
            }
            if (kind == TellZoneStep)
            {
                bool closed = pkg.ReadBool();
                RecordZoneStep(closed, ReadZones(pkg.ReadByteArray()));
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
