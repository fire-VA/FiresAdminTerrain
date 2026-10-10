using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // P1 — lives on every spawned VAVoxel anchor. One anchor = one EDITED
    // chunk. The ZDO carries the chunk key + the compressed brush edit blob;
    // this component applies it on spawn (world load / zone stream-in) and
    // re-applies whenever the ZDO data revision changes (another client
    // brushing the same chunk).
    [DisallowMultipleComponent]
    public sealed class VoxelChunkAnchor : MonoBehaviour
    {
        private static readonly Dictionary<Vector3Int, VoxelChunkAnchor> s_byKey =
            new Dictionary<Vector3Int, VoxelChunkAnchor>();

        public static bool TryGet(Vector3Int key, out VoxelChunkAnchor anchor)
            => s_byKey.TryGetValue(key, out anchor) && anchor != null;

        // Chunk dropped (zone unwrap / Y-range rebuild) while this anchor
        // stays loaded: forget the committed revision so the next poll
        // re-applies the brushwork onto the regenerated chunk.
        public static void InvalidateKey(Vector3Int key)
        {
            if (s_byKey.TryGetValue(key, out var a) && a != null)
            {
                a._lastRev = uint.MaxValue;
                a._lastFailedRev = uint.MaxValue;
            }
        }

        private ZNetView _nv;
        private Vector3Int _key;
        // 0.2.865: the record last applied to which live chunk (same bytes on the same chunk are not re-applied).
        private VoxelChunk _lastAppliedChunk;
        private uint _lastAppliedHash;
        private int _lastAppliedLength;
        private static int s_sameRecordSkips;

        private static uint Fnv(byte[] b)
        {
            uint h = 2166136261;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            return h;
        }
        private bool _keyKnown;
        private uint _lastRev = uint.MaxValue;
        private uint _lastFailedRev = uint.MaxValue;
        private bool _capWarned;
        private float _nextPoll;
        private float _bornAt;

        public ZNetView Nview => _nv;

        // 0.2.689 (FatGroundProbe): the ZDO's latest revision and the one this peer has applied.
        internal uint LatestRevision => _nv != null && _nv.IsValid() ? _nv.GetZDO().DataRevision : 0u;
        internal string AppliedRevisionText => _lastRev == uint.MaxValue ? "none" : _lastRev.ToString();

        // 0.2.690 (FatGroundProbe.CollidersReady): this peer has applied the ZDO's latest revision (a key-less fresh
        // anchor has nothing to apply yet).
        internal bool Applied => !_keyKnown || (_nv != null && _nv.IsValid() && _lastRev == _nv.GetZDO().DataRevision);

        // 0.2.693 (FatGroundProbe.HasEditsXZ): a loaded edit anchor in the 32 m column over (x, z), at any height.
        internal static bool AnyInColumn(float x, float z, float radius)
        {
            foreach (var kv in s_byKey)
            {
                if (kv.Value == null) continue;
                float minX = kv.Key.x * 32f, minZ = kv.Key.z * 32f;
                float dx = Mathf.Max(minX - x, 0f, x - (minX + 32f));
                float dz = Mathf.Max(minZ - z, 0f, z - (minZ + 32f));
                if (dx * dx + dz * dz <= radius * radius) return true;
            }
            return false;
        }

        // 0.2.689 (FatGroundProbe.HasEdits): a loaded edit anchor whose 32 m chunk comes within radius of pos.
        internal static bool AnyNear(Vector3 pos, float radius)
        {
            foreach (var kv in s_byKey)
            {
                if (kv.Value == null) continue;
                Vector3 min = new Vector3(kv.Key.x * 32f, kv.Key.y * 32f, kv.Key.z * 32f);
                float dx = Mathf.Max(min.x - pos.x, 0f, pos.x - (min.x + 32f));
                float dy = Mathf.Max(min.y - pos.y, 0f, pos.y - (min.y + 32f));
                float dz = Mathf.Max(min.z - pos.z, 0f, pos.z - (min.z + 32f));
                if (dx * dx + dy * dy + dz * dz <= radius * radius) return true;
            }
            return false;
        }

        private void Awake()
        {
            _nv = GetComponent<ZNetView>();
            _bornAt = Time.time;
        }

        private void Update()
        {
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + 0.5f;
            if (_nv == null || !_nv.IsValid()) return;
            var zdo = _nv.GetZDO();

            if (!_keyKnown)
            {
                int kx = zdo.GetInt(VoxelAnchorPrefab.ZDO_KX, int.MinValue);
                if (kx == int.MinValue)
                {
                    // Hygiene: a keyless anchor >30s old is a zombie (crash
                    // between spawn and first write). Owner cleans it up so it
                    // never rots in the save.
                    if (Time.time - _bornAt > 30f && ZNetScene.instance != null)
                    {
                        if (!_nv.IsOwner()) _nv.ClaimOwnership();
                        FiresLogger.LogWarning("[FiresVoxel] destroying keyless zombie VAVoxel anchor (no data written).");
                        ZNetScene.instance.Destroy(gameObject);
                    }
                    return;   // fresh spawn — writer sets keys shortly
                }
                _key = new Vector3Int(kx, zdo.GetInt(VoxelAnchorPrefab.ZDO_KY), zdo.GetInt(VoxelAnchorPrefab.ZDO_KZ));

                // Hygiene: an anchor written in an older blob format is removed
                // by its owner instead of rotting in the save.
                int ver = zdo.GetInt(VoxelAnchorPrefab.ZDO_VER, 0);
                if (ver != VoxelAnchorPrefab.EditsFormatVersion)
                {
                    if (!_nv.IsOwner()) _nv.ClaimOwnership();
                    FiresLogger.LogWarning($"[FiresVoxel] removing VAVoxel anchor for chunk {_key} saved in format {ver} (current {VoxelAnchorPrefab.EditsFormatVersion}).");
                    if (ZNetScene.instance != null) ZNetScene.instance.Destroy(gameObject);
                    return;
                }

                // Hygiene: duplicate anchor for a key already claimed by a live
                // anchor (simultaneous first-saves from two clients). Keep the
                // established one; the duplicate self-destructs if we can own it.
                if (s_byKey.TryGetValue(_key, out var existing) && existing != null && !ReferenceEquals(existing, this))
                {
                    FiresLogger.LogWarning($"[FiresVoxel] duplicate VAVoxel anchor for chunk {_key} — removing the newcomer.");
                    if (!_nv.IsOwner()) _nv.ClaimOwnership();
                    if (ZNetScene.instance != null) ZNetScene.instance.Destroy(gameObject);
                    return;
                }
                _keyKnown = true;
                s_byKey[_key] = this;
            }

            uint rev = zdo.DataRevision;
            if (rev == _lastRev || rev == _lastFailedRev) return;
            // Commit the revision only on SUCCESS: a restore that failed
            // (chunk cap, null blob, import mismatch) used to be skipped
            // forever — the neighbour's brushwork rendered to the 32m face
            // while this chunk stayed vanilla, a truncation line for the
            // whole session. Failure semantics: PERMANENT for this revision
            // (length mismatch) → skip until the ZDO revision changes;
            // TRANSIENT (chunk cap, null blob) → back off to a 5s retry
            // instead of hammering decompress at 2Hz.
            float applyStart = Time.realtimeSinceStartup;
            var outcome = Apply(zdo);
            // 0.2.689 ([lead] RIDEg pit): when a dedicated server takes in a runtime edit, at Debug (Fire's log rule).
            if (ZNet.instance != null && ZNet.instance.IsDedicated() && FiresLogger.DebugEnabled)
                FiresLogger.LogDebug($"[FiresVoxel] server applied edit: chunk {_key} rev {rev} → {outcome} in {(Time.realtimeSinceStartup - applyStart) * 1000f:0} ms"
                    + $" ({Time.time - _bornAt:0.0} s after its anchor arrived).");
            if (outcome == ApplyOutcome.Success) _lastRev = rev;
            else if (outcome == ApplyOutcome.PermanentForRevision) _lastFailedRev = rev;
            else _nextPoll = Time.time + 5f;
        }

        private enum ApplyOutcome { Success, Transient, PermanentForRevision }

        // Adopt an anchor we just spawned locally for `key` (writer side) —
        // registers the lookup and skips re-applying our own write.
        public void AdoptLocal(Vector3Int key)
        {
            _key = key;
            _keyKnown = true;
            s_byKey[key] = this;
            if (_nv != null && _nv.IsValid()) _lastRev = _nv.GetZDO().DataRevision;
        }

        public void MarkSelfWrite()
        {
            if (_nv != null && _nv.IsValid()) _lastRev = _nv.GetZDO().DataRevision;
        }

        private ApplyOutcome Apply(ZDO zdo)
        {
            try
            {
                // Chunk availability FIRST — decompressing per poll while the
                // chunk cap blocks the restore was 2Hz alloc churn for nothing.
                var chunk = VoxelWorld.GetOrCreateForRestore(_key);
                if (chunk == null)
                {
                    if (!_capWarned)
                    {
                        _capWarned = true;
                        FiresLogger.LogWarning($"[FiresVoxel] chunk cap reached — restore deferred for {_key} (retrying at 5s cadence).");
                    }
                    return ApplyOutcome.Transient;
                }

                var packed = zdo.GetByteArray(VoxelAnchorPrefab.ZDO_EDITS);
                if (packed == null) return ApplyOutcome.Transient;   // writer hasn't landed yet
                // 0.2.865 (Fire 10-09: 60 -> 16 fps dips standing at a camp): the same record (rev 0) was applied to the same
                // chunk 3-4 times (the anchor ZDO arriving again, e.g. after an owner change), and every apply re-mirrored and
                // remeshed up to 7 neighbours. Identical bytes on the same live chunk change nothing: skip them.
                uint packedHash = Fnv(packed);
                if (ReferenceEquals(_lastAppliedChunk, chunk) && _lastAppliedHash == packedHash && _lastAppliedLength == packed.Length)
                {
                    s_sameRecordSkips++;
                    if (FiresLogger.DebugEnabled && (s_sameRecordSkips <= 3 || s_sameRecordSkips % 50 == 0))
                        FiresLogger.LogDebug($"[FiresVoxel] chunk {_key}: the same record arrived again (rev {zdo.DataRevision}), not re-applied ({s_sameRecordSkips} such skip(s) this session)");
                    return ApplyOutcome.Success;
                }
                var raw = Utils.Decompress(packed);
                int mopsBefore = chunk.MopSignature;
                if (!chunk.ImportBrushEdits(raw))
                {
                    FiresLogger.LogDebug($"[FiresVoxel] NOT applied: chunk {_key} rev {zdo.DataRevision} ({packed.Length} bytes) was refused by the import");
                    return ApplyOutcome.PermanentForRevision;
                }
                // Persisted borders can predate the canonical-border fix (or
                // the neighbourhood changed since the save) — re-mirror rings
                // from live owners so restores never resurrect stale seams.
                chunk.MirrorBordersNow(true);
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                // 0.2.813 (SinkTestGen2, Fire 10-06: a see-through band all round the sinkhole shaft at y 64, and a client that
                // read the carved shaft as solid there): the NEIGHBOURS' rings mirror this chunk too. Re-mirrored only from
                // this side, a neighbour whose record had landed first kept a stale copy of this chunk's border: its mesh
                // stopped short of ours (the gap) and its ring answered the voxel queries. Each loaded neighbour re-mirrors
                // now, and remeshes if anything in its ring changed.
                int neighboursFixed = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue;
                            if (!VoxelWorld.TryGetChunk(new Vector3Int(_key.x + dx, _key.y + dy, _key.z + dz), out var near) || near == null) continue;
                            if (near.MirrorBordersNow(true) > 0) { VoxelMeshPipeline.EnqueueRemesh(near); neighboursFixed++; }
                        }
                if (neighboursFixed > 0) FiresLogger.LogDebug($"[FiresVoxel] chunk {_key}'s record re-mirrored {neighboursFixed} neighbour ring(s) (remeshed)");
                if (chunk.HasBrushEdits) VoxelZoneStreamer.MarkZoneBrushed(_key);
                VerdantsAscent.Water.Tools.ToolFalls.ReadFrom(_key, zdo);   // 0.2.715: the chunk's Vfall falls
                // 0.2.715 Vmop: a mask that changed here (a peer's mop or restore, or the saved one arriving after the water
                // was built) rebuilds the hand-drawn water over this chunk.
                if (chunk.MopSignature != mopsBefore)
                {
                    var centre = chunk.WorldOrigin + new Vector3(16f, 16f, 16f);
                    int rebuilt = VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(centre, 24f);
                    FiresLogger.LogDebug($"[FiresWater] chunk {_key}'s mop mask changed: {rebuilt} drawn water bod(ies) rebuilt.");
                }
                // Evidence (Fire 10-05, at Debug): a peer's (or the saved world's) edit applied here.
                FiresLogger.LogDebug($"[FiresVoxel] applied chunk {_key} from the shared record ({packed.Length} bytes packed, rev {zdo.DataRevision}).");
                _lastAppliedChunk = chunk; _lastAppliedHash = packedHash; _lastAppliedLength = packed.Length;
                return ApplyOutcome.Success;
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] anchor apply failed for {_key}: {ex.Message}");
                return ApplyOutcome.Transient;
            }
        }

        private void OnDestroy()
        {
            if (_keyKnown && s_byKey.TryGetValue(_key, out var cur) && ReferenceEquals(cur, this))
            {
                s_byKey.Remove(_key);
                VerdantsAscent.Water.Tools.ToolFalls.Unload(_key);
            }
        }

        private static readonly List<ZDO> s_zoneAnchors = new List<ZDO>();
        private static readonly HashSet<Vector3Int> s_skippedKeys = new HashSet<Vector3Int>();

        // A dedicated server keeps no anchor instances near its players, so its copy of a zone reads the zone's
        // VAVoxel ZDOs directly. Only chunks the zone holds, and only revisions not imported yet.
        internal static int ImportZoneEditsFromZdos(Vector2s zone, List<Vector3Int> zoneKeys,
            Dictionary<Vector3Int, uint> importedRevisions)
        {
            if (ZDOMan.instance == null) return 0;
            int anchorPrefab = VoxelAnchorPrefab.PrefabName.GetStableHashCode();
            s_zoneAnchors.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), s_zoneAnchors);
            int imported = 0;
            foreach (var zdo in s_zoneAnchors)
            {
                if (zdo == null || zdo.GetPrefab() != anchorPrefab) continue;
                if (zdo.GetInt(VoxelAnchorPrefab.ZDO_VER, 0) != VoxelAnchorPrefab.EditsFormatVersion) continue;
                int kx = zdo.GetInt(VoxelAnchorPrefab.ZDO_KX, int.MinValue);
                if (kx == int.MinValue) continue;
                var key = new Vector3Int(kx, zdo.GetInt(VoxelAnchorPrefab.ZDO_KY), zdo.GetInt(VoxelAnchorPrefab.ZDO_KZ));
                if (!zoneKeys.Contains(key))
                {
                    // A player edited a chunk this zone's data build never armed (R77: the server read a surface carve's old
                    // height for 42 s and more, and an island cube's, while other edits landed). The zone holds it from now on,
                    // so the server's copy of the ground matches every client's.
                    zoneKeys.Add(key);
                    if (s_skippedKeys.Add(key))
                        FiresLogger.LogInfo($"[FiresVoxel] server data for zone ({zone.x},{zone.y}) takes the player edit in chunk {key}: "
                                            + $"added to the zone's data chunks (now {zoneKeys.Count}).");
                }
                uint revision = zdo.DataRevision;
                if (importedRevisions.TryGetValue(key, out uint seen) && seen == revision) continue;
                var packed = zdo.GetByteArray(VoxelAnchorPrefab.ZDO_EDITS);
                // 0.2.712 (admintools R2 10-05: island vfill/vcube "still apart 42 s after the edit", and this path logged
                // nothing either way): every outcome of a NEW revision is a Debug line, once per chunk and revision.
                if (packed == null) { NoteServerImport(key, revision, zone, "NOT imported: the anchor has no edit blob yet"); continue; }
                var chunk = VoxelWorld.GetOrCreateForRestore(key);
                if (chunk == null) { NoteServerImport(key, revision, zone, "NOT imported: the chunk cap refused the chunk"); continue; }
                int mopsBefore = chunk.MopSignature;
                if (!chunk.ImportBrushEdits(Utils.Decompress(packed)))
                {
                    NoteServerImport(key, revision, zone, $"NOT imported: the import refused the blob ({packed.Length} bytes packed; kind {chunk.Kind})");
                    continue;
                }
                // The anchor-instance path (Apply) re-mirrors and remeshes; this one did neither, so the chunk's border rings
                // and its NEIGHBOURS' rings kept the pre-edit samples, and a column read across a chunk boundary (an edit
                // that rises past y 64) answered from the stale ring. Re-mirror the chunk and every loaded neighbour.
                int healed = MirrorAround(key);
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                // 0.2.715: the server's copy of the chunk's falls and of the drawn water a mop changed.
                VerdantsAscent.Water.Tools.ToolFalls.ReadFrom(key, zdo);
                if (chunk.MopSignature != mopsBefore)
                    VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(chunk.WorldOrigin + new Vector3(16f, 16f, 16f), 24f);
                importedRevisions[key] = revision;
                imported++;
                NoteServerImport(key, revision, zone, $"imported ({packed.Length} bytes packed), {healed} border sample(s) re-mirrored around it");
            }
            s_zoneAnchors.Clear();
            return imported;
        }

        private static readonly HashSet<long> s_importNoted = new HashSet<long>();

        private static void NoteServerImport(Vector3Int key, uint revision, Vector2s zone, string outcome)
        {
            long id = ((long)key.GetHashCode() << 32) ^ revision;
            if (!s_importNoted.Add(id)) return;
            if (s_importNoted.Count > 4096) s_importNoted.Clear();
            FiresLogger.LogDebug($"[FiresVoxel] server zone ({zone.x},{zone.y}) chunk {key} rev {revision}: {outcome}.");
        }

        private static int MirrorAround(Vector3Int key)
        {
            int healed = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (VoxelWorld.TryGetChunk(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out var c) && c != null && !c.PendingGenerate)
                            healed += c.MirrorBordersNow(true);
            return healed;
        }
    }
}
