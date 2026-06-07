using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    /// <summary>
    /// Manages snow application to pieces within territories.
    /// 
    /// This system applies snow visual effects to building pieces that are exposed
    /// (not covered by a roof). It mimics how the Seasons mod handles winter snow
    /// on pieces, using the WearNTear.HaveRoof() check to determine coverage.
    /// 
    /// Key features:
    /// - Only applies snow to exposed pieces (checks roof coverage)
    /// - Uses MaterialPropertyBlock for non-invasive color tinting
    /// - Tracks pieces per-territory for proper cleanup
    /// - Periodic scanning around player to handle newly placed pieces
    /// </summary>
    public static class TerritorySnowManager
    {
        // Track which pieces have snow per territory
        private static readonly Dictionary<int, HashSet<int>> _territoryPieces = new();

        // Track all snowed pieces for global cleanup
        private static readonly HashSet<int> _allSnowedPieces = new();

        // Cache WearNTear references to avoid repeated lookups
        private static readonly Dictionary<int, WearNTear> _pieceCache = new();

        // Reusable lists for scanning
        private static readonly List<WearNTear> _tempPieces = new();
        private static readonly Collider[] _overlapResults = new Collider[256];

        // Timing for periodic updates
        private static float _lastScanTime;
        private const float ScanInterval = 2f;

        // Layer mask for piece detection
        private static int _pieceLayerMask = -1;

        private static int PieceLayerMask
        {
            get
            {
                if (_pieceLayerMask == -1)
                    _pieceLayerMask = LayerMask.GetMask("piece", "piece_nonsolid");
                return _pieceLayerMask;
            }
        }

        /// <summary>
        /// Applies snow to all exposed pieces within a circular territory.
        /// </summary>
        public static int ApplySnowToTerritory(int territoryId, Vector3 center, float radius, float blendAmount = 0.5f)
        {
            int count = 0;

            // Find all pieces in radius
            int numColliders = Physics.OverlapSphereNonAlloc(center, radius, _overlapResults, PieceLayerMask);

            for (int i = 0; i < numColliders; i++)
            {
                var collider = _overlapResults[i];
                if (collider == null) continue;

                var wnt = collider.GetComponentInParent<WearNTear>();
                if (wnt == null) continue;

                if (TryApplySnowToPiece(wnt, territoryId, blendAmount))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Applies snow to all exposed pieces within a rectangular territory.
        /// </summary>
        public static int ApplySnowToRectangle(int territoryId, Vector3 min, Vector3 max, float blendAmount = 0.5f)
        {
            // Use sphere overlap with the rectangle's bounding radius
            Vector3 center = (min + max) * 0.5f;
            float radius = Vector3.Distance(min, max) * 0.5f;

            int count = 0;
            int numColliders = Physics.OverlapSphereNonAlloc(center, radius, _overlapResults, PieceLayerMask);

            for (int i = 0; i < numColliders; i++)
            {
                var collider = _overlapResults[i];
                if (collider == null) continue;

                // Check if actually inside rectangle
                Vector3 pos = collider.transform.position;
                if (pos.x < min.x || pos.x > max.x || pos.z < min.z || pos.z > max.z)
                    continue;

                var wnt = collider.GetComponentInParent<WearNTear>();
                if (wnt == null) continue;

                if (TryApplySnowToPiece(wnt, territoryId, blendAmount))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Tries to apply snow to a single piece if it's exposed.
        /// </summary>
        private static bool TryApplySnowToPiece(WearNTear wnt, int territoryId, float blendAmount)
        {
            if (wnt == null) return false;

            int instanceId = wnt.GetInstanceID();

            // Already snowed?
            if (_allSnowedPieces.Contains(instanceId))
                return false;

            // Check if piece has roof coverage - if covered, no snow
            // Use the public static RoofCheck method since HaveRoof() is private
            if (WearNTear.RoofCheck(wnt.transform.position, out _))
                return false;

            // Apply snow to all renderers on this piece
            bool applied = MaterialVariantSystem.ApplySnowToGameObject(wnt.gameObject, blendAmount);

            if (applied)
            {
                _allSnowedPieces.Add(instanceId);

                if (!_territoryPieces.TryGetValue(territoryId, out var pieces))
                {
                    pieces = new HashSet<int>();
                    _territoryPieces[territoryId] = pieces;
                }
                pieces.Add(instanceId);

                _pieceCache[instanceId] = wnt;
            }

            return applied;
        }

        /// <summary>
        /// Removes snow from all pieces in a territory.
        /// </summary>
        public static void RemoveSnowFromTerritory(int territoryId)
        {
            if (!_territoryPieces.TryGetValue(territoryId, out var pieces))
                return;

            foreach (int instanceId in pieces)
            {
                if (_pieceCache.TryGetValue(instanceId, out var wnt) && wnt != null)
                {
                    MaterialVariantSystem.RemoveSnowFromGameObject(wnt.gameObject);
                }

                _allSnowedPieces.Remove(instanceId);
                _pieceCache.Remove(instanceId);
            }

            _territoryPieces.Remove(territoryId);
        }

        /// <summary>
        /// Removes snow from all territories and clears tracking.
        /// </summary>
        public static void ClearAllSnow()
        {
            foreach (var kvp in _pieceCache)
            {
                if (kvp.Value != null)
                {
                    MaterialVariantSystem.RemoveSnowFromGameObject(kvp.Value.gameObject);
                }
            }

            _territoryPieces.Clear();
            _allSnowedPieces.Clear();
            _pieceCache.Clear();
            MaterialVariantSystem.ClearCache();
        }

        /// <summary>
        /// Updates snow for pieces around the player.
        /// This handles newly placed pieces and dynamic territory checks.
        /// </summary>
        /// <param name="playerPos">Player's current position</param>
        /// <param name="scanRadius">Radius to scan around player</param>
        /// <param name="checkTerritory">Function that returns territory ID if position is in snow territory, -1 otherwise</param>
        /// <param name="blendAmount">Snow blend amount (0-1)</param>
        public static void UpdateSnowAroundPlayer(
            Vector3 playerPos,
            float scanRadius,
            Func<Vector3, int> checkTerritory,
            float blendAmount = 0.5f)
        {
            // Rate limit scanning
            if (Time.time - _lastScanTime < ScanInterval)
                return;
            _lastScanTime = Time.time;

            // Find all pieces in radius
            int numColliders = Physics.OverlapSphereNonAlloc(playerPos, scanRadius, _overlapResults, PieceLayerMask);

            for (int i = 0; i < numColliders; i++)
            {
                var collider = _overlapResults[i];
                if (collider == null) continue;

                var wnt = collider.GetComponentInParent<WearNTear>();
                if (wnt == null) continue;

                int instanceId = wnt.GetInstanceID();

                // Check if in snow territory
                int territoryId = checkTerritory(wnt.transform.position);

                if (territoryId >= 0)
                {
                    // In snow territory - apply snow if not already
                    if (!_allSnowedPieces.Contains(instanceId))
                    {
                        TryApplySnowToPiece(wnt, territoryId, blendAmount);
                    }
                }
                else
                {
                    // Not in snow territory - remove snow if applied
                    if (_allSnowedPieces.Contains(instanceId))
                    {
                        MaterialVariantSystem.RemoveSnowFromGameObject(wnt.gameObject);
                        _allSnowedPieces.Remove(instanceId);
                        _pieceCache.Remove(instanceId);

                        // Remove from territory tracking
                        foreach (var kvp in _territoryPieces)
                        {
                            kvp.Value.Remove(instanceId);
                        }
                    }
                }
            }

            // Clean up destroyed pieces from cache
            CleanupDestroyedPieces();
        }

        /// <summary>
        /// Removes entries for destroyed pieces from the cache.
        /// </summary>
        private static void CleanupDestroyedPieces()
        {
            _tempPieces.Clear();

            foreach (var kvp in _pieceCache)
            {
                if (kvp.Value == null)
                {
                    _allSnowedPieces.Remove(kvp.Key);
                    foreach (var territory in _territoryPieces.Values)
                    {
                        territory.Remove(kvp.Key);
                    }
                }
            }

            // Remove null entries
            var keysToRemove = new List<int>();
            foreach (var kvp in _pieceCache)
            {
                if (kvp.Value == null)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
            {
                _pieceCache.Remove(key);
            }
        }

        /// <summary>
        /// Checks if a specific piece has snow applied.
        /// </summary>
        public static bool HasSnow(WearNTear piece)
        {
            if (piece == null) return false;
            return _allSnowedPieces.Contains(piece.GetInstanceID());
        }

        /// <summary>
        /// Gets statistics about snow coverage.
        /// </summary>
        public static (int territories, int totalPieces) GetStats()
        {
            return (_territoryPieces.Count, _allSnowedPieces.Count);
        }
    }
}
