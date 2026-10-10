using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // ZNetView-backed anchor that owns one 64x64m tile's persistent state.
    //
    // Lives at Y = ANCHOR_DEPTH (-5000), far below any playable terrain so
    // it's never seen and never collides. m_distant=true means ZoneSystem
    // streams it regardless of the player's Y position relative to the anchor.
    //
    // Lifecycle:
    //   Awake   -> register with TileAnchorRegistry, attempt LoadFromZdo
    //   Update  -> CheckLoad (compare DataRevision, re-decode on remote write)
    //   ApplyOp -> ClaimOwnership, mutate TerrainTile, encode, write ZDO
    //   Destroy -> unregister, release tile from combiner
    //
    // The vanilla TerrainComp.CheckLoad pattern is what we mirror exactly.
    // ZDO data-revision changes drive re-decode; no client-to-client RPC for
    // terrain state. RPC is only for cross-tile ops that need to dispatch to
    // the owner peer of a tile this client doesn't own (Phase 8c).
    //
    // Phase 8a stub: this file compiles in isolation. The combiner-attach
    // hooks (FindOrCreateParentCombiner, NotifyCombinerOfMutation) are no-ops
    // until Phase 8b wires them.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ZNetView))]
    public sealed class TileAnchor : MonoBehaviour
    {
        // Y position for every TileAnchor. Well below any expected gameplay
        // range (Valheim seabed is around -30; sky-builds can go to Y=10000+).
        // ZNetView.m_distant=true makes the actual Y irrelevant for streaming,
        // so picking a deep number just guarantees no accidental visibility.
        public const float ANCHOR_DEPTH = -5000f;

        // ZDO keys (hashed once for fast access).
        private static readonly int K_TILE_V = "va_tile_v".GetStableHashCode();
        private static readonly int K_TILE_DATA = "va_tile_data".GetStableHashCode();
        private static readonly int K_TILE_X = "va_tile_x".GetStableHashCode();
        private static readonly int K_TILE_Z = "va_tile_z".GetStableHashCode();

        // The tile this anchor owns. Set on Awake from the ZDO (existing anchor)
        // or by Spawn (newly created — see Phase 8c).
        public Vector2i TileXZ { get; private set; }

        // The runtime data holder. Null until LoadFromZdo succeeds (e.g. non-owner
        // client waiting for ZDO sync).
        public TerrainTile Tile { get; private set; }

        private ZNetView _nview;
        private uint _lastDataRevision = uint.MaxValue;
        private bool _registered;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            if (_nview == null || !_nview.IsValid())
            {
                FiresLogger.LogWarning($"[TileAnchor] Awake @ {transform.position} with invalid ZNetView; disabling.");
                enabled = false;
                return;
            }

            // Identity from ZDO (existing anchor) or from transform (just spawned;
            // creator code sets TileXZ via PreInitializeIdentity before Awake fires).
            if (TileXZ.x == 0 && TileXZ.y == 0)
            {
                TileXZ = ReadTileIndexFromZdo();
                FiresLogger.LogVerbose($"[TileAnchor] Awake derived TileXZ={TileXZ} from ZDO/position.");
            }
            else
            {
                FiresLogger.LogVerbose($"[TileAnchor] Awake with pre-initialized TileXZ={TileXZ}.");
            }

            TileAnchorRegistry.Register(this);
            _registered = true;

            // Upgrade ZDO Type from the prior Default to Prioritized so the
            // NEXT spawn round services us early. The prefab now defaults to
            // Prioritized but existing saved ZDOs still carry the old Type
            // field — bumping it here at Awake propagates through replication.
            // Owner-only set (non-owners would just race the owner's value).
            if (_nview.IsOwner())
            {
                var z = _nview.GetZDO();
                if (z != null && z.Type != ZDO.ObjectType.Prioritized)
                    z.Type = ZDO.ObjectType.Prioritized;
            }

            bool owner = _nview.IsOwner();

            // Timing instrumentation — pins down "tile took forever to appear"
            // complaints. Records wall-clock + frame number at Awake; the
            // matching log fires inside RebuildMesh on the first successful
            // mesh build so we can see the actual lag between ZNetScene
            // spawning us and the mesh appearing on screen.
            _awakeRealtime = Time.realtimeSinceStartup;
            _awakeFrame = Time.frameCount;
            float zoneAge = ZoneStreamTiming.MillisSinceZoneActive(ZoneStreamTiming.ZoneFromAnchor(TileXZ));
            // Per-tile Awake log is verbose-only — the wave gets
            // summarized by the 🪨 TILES batch banner emitted by
            // VATileRestoreBatch after a quiescence window. See
            // VATileRestoreBatch class header for the batching model.
            FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) Awake | owner={owner} | dataRev={_nview.GetZDO()?.DataRevision} | frame={_awakeFrame} | T+{zoneAge:F0}ms since zone became active");

            // Attempt initial load — succeeds for the owner that just wrote, or
            // for a remote client whose ZDO byte array has already synced.
            // CheckLoad on subsequent Update frames will retry until success.
            TryLoadFromZdo();
        }

        // Captured at Awake; consumed by TileMeshRenderer when the first
        // successful mesh rebuild completes so we can log the full latency.
        private float _awakeRealtime = -1f;
        private int _awakeFrame = -1;
        internal float AwakeRealtime => _awakeRealtime;
        internal int AwakeFrame => _awakeFrame;

        private void Update()
        {
            if (_nview == null || !_nview.IsValid()) return;
            CheckLoad();
            TileAnchorHeal.Tick();
        }

        // For TileAnchorHeal: this anchor's ZDO and its stored tile payload (null until it has synced).
        internal ZDO Zdo => _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;
        internal byte[] StoredPayload => Zdo?.GetByteArray(K_TILE_DATA);

        // TileAnchorHeal, server only: store a merged payload on this (the keeper) anchor as its owner, then read it back.
        // True only when the ZDO now holds exactly these bytes and they decode. CheckLoad re-decodes it on every peer.
        internal bool WriteHealedPayload(byte[] payload, out string failure)
        {
            failure = null;
            var zdo = Zdo;
            if (zdo == null) { failure = "no ZDO"; return false; }
            if (!_nview.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
            if (!_nview.IsOwner()) { failure = "could not take ownership"; return false; }
            zdo.Set(K_TILE_V, (int)TerrainTileCodec.SCHEMA_VERSION);
            zdo.Set(K_TILE_X, TileXZ.x);
            zdo.Set(K_TILE_Z, TileXZ.y);
            zdo.Set(K_TILE_DATA, payload);
            byte[] back = zdo.GetByteArray(K_TILE_DATA);
            if (back == null || back.Length != payload.Length) { failure = "read-back length differs"; return false; }
            for (int i = 0; i < back.Length; i++)
                if (back[i] != payload[i]) { failure = $"read-back byte {i} differs"; return false; }
            try { TerrainTileCodec.Decode(back); }
            catch (Exception ex) { failure = $"read-back does not decode ({ex.Message})"; return false; }
            return true;
        }

        // TileAnchorHeal, server only: remove this duplicate (its data already merged into the keeper) from the world.
        internal bool DestroyAsLoser(out string failure)
        {
            failure = null;
            if (ZNetScene.instance == null || _nview == null || !_nview.IsValid()) { failure = "no ZNetScene or ZNetView"; return false; }
            var zdo = Zdo;
            if (zdo != null && !_nview.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
            if (!_nview.IsOwner()) { failure = "could not take ownership"; return false; }
            ZNetScene.instance.Destroy(gameObject);
            return true;
        }

        private void OnDestroy()
        {
            if (_registered)
            {
                TileAnchorRegistry.Unregister(this);
                _registered = false;
                FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) OnDestroy → unregistered.");
            }
            // 8b: NotifyCombinerOfRelease(Tile);
            Tile = null;
        }

        // Vanilla CheckLoad pattern. If the ZDO's DataRevision has advanced
        // since our last decode, re-decode. This is what catches remote-peer
        // writes — the engine handles ZDO replication, we react to the bump.
        private void CheckLoad()
        {
            var zdo = _nview.GetZDO();
            if (zdo == null) return;
            if (zdo.DataRevision == _lastDataRevision) return;
            FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) CheckLoad triggered | rev {_lastDataRevision} → {zdo.DataRevision}");
            TryLoadFromZdo();
        }

        // Read the ZDO byte array, decode, apply to (or create) the child
        // TerrainTile. Returns true if a decode happened, false if no data yet.
        private bool TryLoadFromZdo()
        {
            var zdo = _nview.GetZDO();
            if (zdo == null)
            {
                FiresLogger.LogWarning($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) TryLoadFromZdo: ZDO is null");
                return false;
            }

            byte[] payload = zdo.GetByteArray(K_TILE_DATA);
            if (payload == null || payload.Length == 0)
            {
                // No data yet — non-owner client may need to wait a tick or
                // two for the byte array to sync. CheckLoad will retry.
                FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) TryLoadFromZdo: no payload yet (owner={_nview.IsOwner()}, rev={zdo.DataRevision})");
                _lastDataRevision = zdo.DataRevision;
                return false;
            }

            TileData decoded;
            try
            {
                decoded = TerrainTileCodec.Decode(payload);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) Decode failed ({payload.Length} bytes): {ex.Message}");
                _lastDataRevision = zdo.DataRevision; // don't retry on corrupt data
                return false;
            }

            if (decoded == null)
            {
                FiresLogger.LogWarning($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) Decode returned null from {payload.Length} bytes");
                return false;
            }

            // Sanity: decoded TileXZ should match our identity. If not, the
            // anchor is wired to the wrong tile somehow — log and abandon.
            if (decoded.TileXZ.x != TileXZ.x || decoded.TileXZ.y != TileXZ.y)
            {
                FiresLogger.LogWarning(
                    $"[TileAnchor] Decoded TileXZ ({decoded.TileXZ.x},{decoded.TileXZ.y}) != anchor identity ({TileXZ.x},{TileXZ.y}); ignoring.");
                _lastDataRevision = zdo.DataRevision;
                return false;
            }

            EnsureTile();
            Tile.Initialize(decoded, zdo.DataRevision);
            _lastDataRevision = zdo.DataRevision;

            int heightMods = 0, paintMods = 0;
            for (int i = 0; i < TerrainTileCodec.VERTEX_COUNT; i++)
            {
                if (decoded.HeightModified[i]) heightMods++;
                if (decoded.PaintModified[i]) paintMods++;
            }
            // Per-tile "loaded" log is verbose-only; the totals
            // get aggregated into the 🪨 TILES batch banner. The
            // RecordTileRestored call below feeds the accumulator
            // — VATileRestoreBatch.Flush emits the banner once the
            // wave settles.
            FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) loaded | payload={payload.Length}B, heightMods={heightMods}, paintMods={paintMods}, disks={decoded.Disks.Count}, rev={zdo.DataRevision}");
            VerdantsAscent.Utilities.VATileRestoreBatch.RecordTileRestored(
                payloadBytes: payload.Length,
                heightMods: heightMods,
                paintMods: paintMods,
                disks: decoded.Disks.Count);

            return true;
        }

        // Apply a mutation locally and persist it. Claims ownership if not the
        // owner — vanilla TerrainComp does the same. The mutate callback runs
        // synchronously against the live Tile.* grids; the callback should NOT
        // capture references that outlive this call.
        //
        // LEGACY signature kept for callers that don't track change counts.
        // Always saves + rebuilds. Prefer the int-returning overload below
        // for hot brush paths so no-op ticks don't thrash mesh+collider
        // rebuilds (which can drop FPS to single digits on a 10Hz drag).
        public void ApplyOpAndSave(Action<TerrainTile> mutate)
        {
            if (mutate == null) return;
            ApplyOpAndSave(t => { mutate(t); return 1; });
        }

        // Preferred overload: callback returns the number of verts/disks the
        // op actually modified. If the result is 0, we skip WriteToZdo AND
        // NotifyMutated — no network revision bump, no mesh rebuild, no
        // collider re-cook, no underside re-sync.
        //
        // Why this exists:
        //   The brush drag handler fires at 10Hz. Each tick iterates every
        //   tile inside the brush bounding-box and calls ApplyOpAndSave.
        //   For tiles whose verts don't actually fall inside the brush
        //   circle (corner cases — pun intended), the inner clear/raise/
        //   paint loop writes 0 verts. The OLD ApplyOpAndSave still saved
        //   the ZDO + bumped revision + triggered a full mesh+collider
        //   rebuild on those untouched tiles. With 4 tiles per drag tick
        //   × 10 ticks/sec × ~25ms rebuild each = ~1000ms/sec of work →
        //   11 FPS. Also caused visible flicker on neighbor tiles whose
        //   meshes were rebuilt for nothing.
        //
        // Returns the wrote count from the callback (0 = no save).
        //
        // 2026-05-26 round-41 — `deferRebuild` flag. When true, the ZDO
        // write still happens (so data persists), but the per-tile mesh
        // rebuild is skipped. Brush ops use this so they can apply the
        // op to all overlapping tiles, stitch the borders, and THEN
        // trigger a single rebuild per tile with fully-stitched data.
        // Without this flag the per-tile rebuild fires with pre-stitch
        // heights → the mesh briefly shows a seam ridge until the
        // stitch's secondary rebuild lands a frame later (and on slow
        // clients, the user sees a permanent-looking seam because the
        // stitch's rebuild doesn't always catch every affected tile).
        // The 2D tile layer is retired: no anchor is created (Spawn) and no tile is written (here), whoever asks.
        internal static readonly bool Retired = true;

        public int ApplyOpAndSave(Func<TerrainTile, int> mutate, bool deferRebuild = false)
        {
            if (mutate == null || Retired) return 0;
            if (_nview == null || !_nview.IsValid())
            {
                FiresLogger.LogWarning($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) ApplyOpAndSave: invalid ZNetView");
                return 0;
            }

            if (!_nview.IsOwner())
            {
                FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) ApplyOpAndSave: claiming ownership");
                _nview.ClaimOwnership();
                if (!_nview.IsOwner())
                {
                    // ClaimOwnership is async at the network layer — vanilla
                    // ApplyOperation pattern uses RPC to dispatch to whoever
                    // owns it. Phase 8d will wire that RPC. For 8c, log and skip.
                    FiresLogger.LogWarning($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) ApplyOpAndSave: ClaimOwnership did not promote us; needs RPC dispatch (Phase 8d).");
                    return 0;
                }
            }

            EnsureTile();
            int wrote = mutate(Tile);
            if (wrote <= 0) return 0;   // no-op skip — see comment above

            WriteToZdo();
            if (!deferRebuild) Tile.NotifyMutated();
            return wrote;
        }

        // Per-anchor "clear data in radius" inner loop used by RegionScrubber.
        //
        // Each [Flags] bit in `scope` corresponds to a per-vertex field set:
        //   * TileHeights → Heights[idx] = 0,  HeightModified[idx] = false
        //   * TilePaint   → Paint[idx]   = default, PaintModified[idx] = false
        //   * TileBiome   → Biome[idx]   = 0,  BiomeModified[idx]    = false
        // Any combination is valid; flags from OTHER scope categories
        // (VanillaBiome, Clutter, …) are ignored here — the scrubber handles
        // those at the world layer.
        //
        // Walks only verts inside the brush circle (clamped to tile bounds),
        // so partial-coverage tiles only clear the part that's actually in
        // the radius. ApplyOpAndSave's no-op-skip means a tile whose
        // bounding box overlaps but whose verts don't fall inside the
        // circle (corner case — pun intended) doesn't bump the ZDO or
        // re-cook the mesh for nothing.
        //
        // ANCHOR LIFETIME: this method NEVER destroys the anchor. Even if
        // every vert ends up unmodified, the anchor stays alive with an
        // empty ZDO (a few bytes of header). That's the intentional
        // "anchor-per-region, never destroy" pattern — eliminates the
        // ghost-overlay class of bugs where vanilla overlay ZDOs survived
        // the anchor and resurrected on repaint.
        //
        // Returns the count of (field × vertex) writes — so a single vert
        // cleared with scope=TileHeights|TileBiome counts as 2.
        public int ClearData(
            Vector3 center,
            float radius,
            VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope scope)
        {
            if (radius <= 0f) return 0;

            // Pre-extract the tile-specific scope bits. If none are set we
            // don't even need ApplyOpAndSave — caller's scope was all
            // vanilla / clutter and they expected us to no-op here.
            bool clearH = (scope & VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.TileHeights) != 0;
            bool clearP = (scope & VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.TilePaint)   != 0;
            bool clearB = (scope & VerdantsAscent.Terrain.Lifecycle.RegionScrubber.Scope.TileBiome)   != 0;
            if (!clearH && !clearP && !clearB) return 0;

            float r2 = radius * radius;

            return ApplyOpAndSave(tile =>
            {
                int wrote = 0;
                int size = TerrainTileCodec.VERTEX_GRID_SIZE; // 65
                float originX = tile.TileXZ.x * TerrainTile.TILE_SIZE;
                float originZ = tile.TileXZ.y * TerrainTile.TILE_SIZE;

                // Vertex (i,j) bbox covering the circle — clamped to the
                // tile's 0..64 inclusive range so border verts get hit.
                int sizeMaxIdx = size - 1;
                int iMin = Mathf.Clamp(Mathf.FloorToInt((center.x - radius - originX) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
                int iMax = Mathf.Clamp(Mathf.CeilToInt((center.x + radius - originX)  / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
                int jMin = Mathf.Clamp(Mathf.FloorToInt((center.z - radius - originZ) / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);
                int jMax = Mathf.Clamp(Mathf.CeilToInt((center.z + radius - originZ)  / TerrainTile.VERTEX_SPACING), 0, sizeMaxIdx);

                for (int j = jMin; j <= jMax; j++)
                for (int i = iMin; i <= iMax; i++)
                {
                    float wx = originX + i * TerrainTile.VERTEX_SPACING;
                    float wz = originZ + j * TerrainTile.VERTEX_SPACING;
                    float dx = wx - center.x;
                    float dz = wz - center.z;
                    if (dx * dx + dz * dz > r2) continue;

                    int idx = j * size + i;

                    if (clearH && tile.HeightModified[idx])
                    {
                        tile.HeightModified[idx] = false;
                        tile.Heights[idx] = 0f;
                        wrote++;
                    }
                    if (clearP && tile.PaintModified[idx])
                    {
                        tile.PaintModified[idx] = false;
                        tile.Paint[idx] = default;
                        wrote++;
                    }
                    if (clearB && tile.BiomeModified != null
                        && idx < tile.BiomeModified.Length && tile.BiomeModified[idx])
                    {
                        tile.BiomeModified[idx] = false;
                        tile.Biome[idx] = 0;
                        wrote++;
                    }
                }

                return wrote;
            });
        }

        // Encode current Tile state and stamp it into the ZDO. Bumps
        // DataRevision, which will fan out to peers via ZDO replication.
        private void WriteToZdo()
        {
            if (Tile == null) return;
            var zdo = _nview.GetZDO();
            if (zdo == null)
            {
                FiresLogger.LogWarning($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) WriteToZdo: ZDO is null");
                return;
            }

            byte[] payload;
            try
            {
                payload = TerrainTileCodec.Encode(Tile.Snapshot());
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) Encode failed: {ex.Message}");
                return;
            }

            zdo.Set(K_TILE_V, (int)TerrainTileCodec.SCHEMA_VERSION);
            zdo.Set(K_TILE_X, TileXZ.x);
            zdo.Set(K_TILE_Z, TileXZ.y);
            zdo.Set(K_TILE_DATA, payload);

            _lastDataRevision = zdo.DataRevision;

            FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) saved | payload={payload.Length}B | new rev={zdo.DataRevision}");
        }

        private void EnsureTile()
        {
            if (Tile != null) return;
            // Tile lives as a child GameObject so it can later host the combiner
            // mesh under its transform. Created lazily on first load/op so we
            // don't allocate for anchors that just stream in without data.
            //
            // CRITICAL: InitializeEmpty allocates Heights/Paint/Modified arrays
            // (4225 each), sets TileXZ to our anchor's tile index, and attaches
            // TileMeshRenderer. Without this call:
            //   - mutate(Tile) hits null Heights → silent NRE OR
            //   - TerrainTile.TileXZ stays (0,0) so ApplyFlatToTile computes
            //     vertex range against the wrong origin → 0 writes OR
            //   - WriteToZdo's Snapshot returns null arrays → codec rejects
            //     with "Heights must be length 4225" OR
            //   - NotifyMutated finds no TileMeshRenderer → warning, no render
            // Caught in the log analysis 2026-05-24 — all 4 symptoms vanish
            // once InitializeEmpty runs here.
            var go = new GameObject($"Tile_{TileXZ.x}_{TileXZ.y}");
            go.transform.SetParent(transform, worldPositionStays: false);
            Tile = go.AddComponent<TerrainTile>();
            Tile.InitializeEmpty(TileXZ);
            // EnsureTile creation log is verbose-only — folded into
            // the 🪨 TILES batch banner.
            FiresLogger.LogVerbose($"[TileAnchor] tile=({TileXZ.x},{TileXZ.y}) EnsureTile created TerrainTile child | Heights.Length={Tile.Heights?.Length ?? -1} hasMeshRenderer={Tile.GetComponent<TileMeshRenderer>() != null}");
        }

        private Vector2i ReadTileIndexFromZdo()
        {
            var zdo = _nview.GetZDO();
            if (zdo == null) return TileAnchorRegistry.WorldToTileIndex(transform.position);

            // Prefer explicit ZDO keys if set (write path always sets these).
            int x = zdo.GetInt(K_TILE_X, int.MinValue);
            int z = zdo.GetInt(K_TILE_Z, int.MinValue);
            if (x != int.MinValue && z != int.MinValue)
                return new Vector2i(x, z);

            // Fall back to the spawn position — useful for anchors created
            // before the codec wrote, or any orphan cases.
            return TileAnchorRegistry.WorldToTileIndex(transform.position);
        }

        // Phase 8c helper: assign identity before Awake fires. Caller spawns
        // the prefab, sets TileXZ via this method, then enables the GO so Awake
        // sees the correct identity. (Cannot pass via constructor — MonoBehaviours.)
        internal void PreInitializeIdentity(Vector2i tileXZ)
        {
            TileXZ = tileXZ;
            transform.position = new Vector3(
                tileXZ.x * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f,
                ANCHOR_DEPTH,
                tileXZ.y * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f);
        }

        // Spawn a new TileAnchor at the given tile index via ZNetScene.Instantiate.
        // Idempotent on tile index — returns the existing anchor if one is already
        // registered at this tile. The new anchor's Awake fires synchronously
        // inside Instantiate, so the returned reference is fully initialized:
        // ZDO created, registered with TileAnchorRegistry, ownership claimed.
        //
        // Returns null if:
        //   * VATile prefab is not registered (TileAnchorPrefab.Ensure failed)
        //   * ZNetScene.Instantiate returned null
        //   * The spawned GameObject is missing the TileAnchor component
        public static TileAnchor Spawn(Vector2i tileXZ)
        {
            // The 2D tile layer is retired (Fire, 2026-09-29: FAT's terrain tools are the voxel tools only; on voxel worlds
            // its leftovers floated as "pixels of previous terrain"). No caller gets an anchor to create or edit; what a save
            // still holds is only there to be purged (TileAnchorHeal, mode Purge / fat_tile_purge).
            if (Retired) return null;

            // De-dupe first: if an anchor is already in the registry, return it.
            var existing = TileAnchorRegistry.GetAt(tileXZ);
            if (existing != null)
            {
                FiresLogger.LogVerbose($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}) already loaded; returning existing.");
                return existing;
            }

            // Only the server creates a tile's anchor. Every tile op reaches every peer (TileBrushRPC), and a de-dupe against
            // this peer's registry alone let the editor, the server and the bot each spawn one on a fresh tile before the
            // others' ZDO arrived: the edits split across them and a reload kept one (R70, OPEN_BUGS #47). A client's edit on
            // a fresh tile lands through the server's anchor, which then replicates here.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                FiresLogger.LogVerbose($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}) has no anchor here yet; the server creates it.");
                return null;
            }

            if (!TileAnchorPrefab.IsRegistered)
            {
                if (!TileAnchorPrefab.Ensure())
                {
                    FiresLogger.LogError($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}): VATile prefab not registered; cannot spawn.");
                    return null;
                }
            }

            if (ZNetScene.instance == null)
            {
                FiresLogger.LogError($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}): ZNetScene.instance is null.");
                return null;
            }

            Vector3 spawnPos = new Vector3(
                tileXZ.x * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f,
                ANCHOR_DEPTH,
                tileXZ.y * TerrainTile.TILE_SIZE + TerrainTile.TILE_SIZE * 0.5f);

            var spawned = UnityEngine.Object.Instantiate(
                TileAnchorPrefab.Prefab, spawnPos, Quaternion.identity);

            if (spawned == null)
            {
                FiresLogger.LogError($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}): Instantiate returned null.");
                return null;
            }

            // The clone is inactive (DontDestroyOnLoad pool). Activate so
            // Awake fires on TileAnchor + ZNetView. Awake registers with
            // ZNetScene's ZDO table and TileAnchorRegistry.
            spawned.SetActive(true);

            var anchor = spawned.GetComponent<TileAnchor>();
            if (anchor == null)
            {
                FiresLogger.LogError($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}): spawned GO has no TileAnchor component; destroying.");
                UnityEngine.Object.Destroy(spawned);
                return null;
            }

            // Awake derived TileXZ from spawnPos; assert it matches what we asked for.
            if (anchor.TileXZ.x != tileXZ.x || anchor.TileXZ.y != tileXZ.y)
            {
                FiresLogger.LogWarning($"[TileAnchor.Spawn] tile mismatch after Awake: requested ({tileXZ.x},{tileXZ.y}), got ({anchor.TileXZ.x},{anchor.TileXZ.y}). Forcing.");
                anchor.PreInitializeIdentity(tileXZ);
                TileAnchorRegistry.Unregister(anchor);
                TileAnchorRegistry.Register(anchor);
            }

            FiresLogger.LogInfo($"[TileAnchor.Spawn] tile=({tileXZ.x},{tileXZ.y}) spawned at {spawnPos}");
            return anchor;
        }
    }
}
