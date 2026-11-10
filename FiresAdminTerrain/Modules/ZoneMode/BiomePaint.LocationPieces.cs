using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1388 (Fire: "it should never double shrines we KNOW what the shrines were and that they are locations so they get removed and replaced properly").
    // A location Expand World places from a blueprint has no prefab to read, so its contents come from the blueprint file: each piece's prefab and its flat distance
    // from the centre Expand World uses. Flat distance doesn't change with the placement's rotation. A saved object belongs to the location when both match, invulnerable or not;
    // an invulnerable object that doesn't match is somebody's build. The one answer for the location reset, thin and the paint's swap.
    internal static partial class BiomePaint
    {
        private const float PieceDistanceTolerance = 0.5f;

        internal sealed class BlueprintPieces
        {
            internal readonly Dictionary<int, List<float>> DistanceByPrefab = new Dictionary<int, List<float>>();
            internal float Reach;
            internal int Count;
            internal bool Holds(int prefab, float distance)
            {
                if (!DistanceByPrefab.TryGetValue(prefab, out var list)) return false;
                foreach (float d in list) if (Mathf.Abs(d - distance) <= PieceDistanceTolerance) return true;
                return false;
            }
        }

        private static List<string> s_blueprintFiles;

        private static string BlueprintFolder()
        {
            var type = AccessTools.TypeByName("ExpandWorldData.Configuration");
            var prop = type != null ? AccessTools.Property(type, "BlueprintLocalFolder") : null;
            try { if (prop?.GetValue(null) is string folder && folder.Length > 0) return folder; }
            catch (Exception ex) { FiresLogger.LogDebug($"[LocationPieces] Expand World's blueprint folder could not be read, using PlanBuild: {ex.Message}"); }
            return Path.Combine(BepInEx.Paths.ConfigPath, "PlanBuild");
        }

        // Expand World's own rule for finding a file: its name, spaces as underscores, exact case.
        private static string FindBlueprintFile(string name)
        {
            if (s_blueprintFiles == null)
            {
                s_blueprintFiles = new List<string>();
                string folder = BlueprintFolder();
                if (Directory.Exists(folder))
                {
                    s_blueprintFiles.AddRange(Directory.GetFiles(folder, "*.blueprint", SearchOption.AllDirectories));
                    s_blueprintFiles.AddRange(Directory.GetFiles(folder, "*.vbuild", SearchOption.AllDirectories));
                }
            }
            string wanted = name.Replace(" ", "_");
            foreach (string file in s_blueprintFiles)
                if (Path.GetFileNameWithoutExtension(file).Replace(" ", "_") == wanted) return file;
            return null;
        }

        internal static BlueprintPieces ReadBlueprint(string name)
        {
            string file = FindBlueprintFile(name);
            if (file == null) return null;
            var names = new List<string>();
            var xs = new List<float>();
            var zs = new List<float>();
            string centerPiece = "piece_bpcenterpoint";
            bool vbuild = file.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase);
            bool inPieces = true;
            var inv = CultureInfo.InvariantCulture;
            foreach (string raw in File.ReadAllLines(file))
            {
                string row = raw.Trim();
                if (row.Length == 0) continue;
                if (row[0] == '#')
                {
                    if (row.StartsWith("#Center:", StringComparison.Ordinal)) centerPiece = row.Substring(8);
                    else if (row.StartsWith("#Pieces", StringComparison.Ordinal)) inPieces = true;
                    else if (row.StartsWith("#SnapPoints", StringComparison.Ordinal) || row.StartsWith("#Terrain", StringComparison.Ordinal)) inPieces = false;
                    continue;
                }
                if (!inPieces) continue;
                var f = row.Split(';');
                int ix = vbuild ? 5 : 2, iz = vbuild ? 7 : 4;
                if (f.Length <= iz || !float.TryParse(f[ix], NumberStyles.Float, inv, out float x) || !float.TryParse(f[iz], NumberStyles.Float, inv, out float z)) continue;
                names.Add(f[0]); xs.Add(x); zs.Add(z);
            }
            if (names.Count == 0) return null;
            // Expand World's centre: the centre piece by exact name, which then doesn't spawn; without one the middle of the bounds, which also take in the origin.
            float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;
            for (int i = 0; i < names.Count; i++) { minX = Mathf.Min(minX, xs[i]); maxX = Mathf.Max(maxX, xs[i]); minZ = Mathf.Min(minZ, zs[i]); maxZ = Mathf.Max(maxZ, zs[i]); }
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            int centerAt = names.IndexOf(centerPiece);
            if (centerAt >= 0) { cx = xs[centerAt]; cz = zs[centerAt]; }
            var pieces = new BlueprintPieces();
            for (int i = 0; i < names.Count; i++)
            {
                if (i == centerAt) continue;
                float d = Mathf.Sqrt((xs[i] - cx) * (xs[i] - cx) + (zs[i] - cz) * (zs[i] - cz));
                int hash = names[i].GetStableHashCode();
                if (!pieces.DistanceByPrefab.TryGetValue(hash, out var list)) pieces.DistanceByPrefab[hash] = list = new List<float>();
                list.Add(d);
                pieces.Reach = Mathf.Max(pieces.Reach, d);
                pieces.Count++;
            }
            FiresLogger.LogDebug($"[LocationPieces] {name}: {pieces.Count} piece(s) from {Path.GetFileName(file)}, reach {pieces.Reach:0.0} m, centre piece {(centerAt >= 0 ? "found" : "not found, the bounds' middle")}");
            return pieces;
        }

        // Does this saved object belong to this placed location?
        internal static bool IsLocationPiece(ZoneSystem zones, ZoneSystem.ZoneLocation location, Vector3 centre, Vector3 at, int prefab)
        {
            var parts = PartsOf(zones, location);
            float dx = at.x - centre.x, dz = at.z - centre.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (parts.Blueprint != null) return parts.Blueprint.Holds(prefab, distance);
            return parts.Prefabs.Contains(prefab) && distance <= Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius);
        }

        internal static bool IsBlueprintLocation(ZoneSystem zones, ZoneSystem.ZoneLocation location) => PartsOf(zones, location).Blueprint != null;

        internal static float LocationReach(ZoneSystem zones, ZoneSystem.ZoneLocation location)
        {
            float radius = Mathf.Max(location.m_exteriorRadius, location.m_interiorRadius);
            var parts = PartsOf(zones, location);
            return parts.Blueprint != null ? Mathf.Max(radius, parts.Blueprint.Reach + PieceDistanceTolerance) : radius;
        }
    }
}
