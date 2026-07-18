using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // P1 — lives on every spawned VAVoxel anchor. One anchor = one EDITED
    // chunk. The ZDO carries the chunk key + the compressed density blob;
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
        private bool _keyKnown;
        private uint _lastRev = uint.MaxValue;
        private uint _lastFailedRev = uint.MaxValue;
        private bool _capWarned;
        private float _nextPoll;
        private float _bornAt;

        public ZNetView Nview => _nv;

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
                        Debug.LogWarning("[FiresVoxel] destroying keyless zombie VAVoxel anchor (no data written).");
                        ZNetScene.instance.Destroy(gameObject);
                    }
                    return;   // fresh spawn — writer sets keys shortly
                }
                _key = new Vector3Int(kx, zdo.GetInt(VoxelAnchorPrefab.ZDO_KY), zdo.GetInt(VoxelAnchorPrefab.ZDO_KZ));

                // Hygiene: duplicate anchor for a key already claimed by a live
                // anchor (simultaneous first-saves from two clients). Keep the
                // established one; the duplicate self-destructs if we can own it.
                if (s_byKey.TryGetValue(_key, out var existing) && existing != null && !ReferenceEquals(existing, this))
                {
                    Debug.LogWarning($"[FiresVoxel] duplicate VAVoxel anchor for chunk {_key} — removing the newcomer.");
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
            var outcome = Apply(zdo);
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
                        Debug.LogWarning($"[FiresVoxel] chunk cap reached — restore deferred for {_key} (retrying at 5s cadence).");
                    }
                    return ApplyOutcome.Transient;
                }

                var packed = zdo.GetByteArray(VoxelAnchorPrefab.ZDO_DENSITY);
                if (packed == null) return ApplyOutcome.Transient;   // writer hasn't landed yet
                var raw = Utils.Decompress(packed);
                if (!chunk.ImportDensity(raw)) return ApplyOutcome.PermanentForRevision;
                // Persisted borders can predate the canonical-border fix (or
                // the neighbourhood changed since the save) — re-mirror rings
                // from live owners so restores never resurrect stale seams.
                chunk.MirrorBordersNow(true);
                VoxelMeshPipeline.EnqueueRemesh(chunk);
                if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                    Debug.Log($"[FiresVoxel] anchor restored chunk {_key} ({packed.Length} bytes packed, rev {zdo.DataRevision}).");
                return ApplyOutcome.Success;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresVoxel] anchor apply failed for {_key}: {ex.Message}");
                return ApplyOutcome.Transient;
            }
        }

        private void OnDestroy()
        {
            if (_keyKnown && s_byKey.TryGetValue(_key, out var cur) && ReferenceEquals(cur, this))
                s_byKey.Remove(_key);
        }
    }
}
