using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1336: the ice controllers' grid header from a ZDO. The code only ever makes width = ceil(extent / cell) + extra, so a stored
    // size must agree with its stored bounds (a little slack for float rounding), and the bounds must fit the largest grid allowed.
    internal static class IceGridCheck
    {
        internal const int MaxCells = 4096 * 4096;   // 64 MB of floats: a 1 km square at 0.25 m
        private const int Slack = 2;
        private static bool s_infoSaid;

        internal static bool Read(byte[] data, float cell, int extra, string what, out int width, out int height, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            width = height = 0; minX = minZ = maxX = maxZ = 0f;
            if (data == null || data.Length < 24) return false;   // no header: the caller's normal path, not a bad block
            width = BitConverter.ToInt32(data, 0);
            height = BitConverter.ToInt32(data, 4);
            minX = BitConverter.ToSingle(data, 8);
            minZ = BitConverter.ToSingle(data, 12);
            maxX = BitConverter.ToSingle(data, 16);
            maxZ = BitConverter.ToSingle(data, 20);
            bool ok = Fits(minX, minZ, maxX, maxZ, cell, extra, what)
                && Mathf.Abs(width - (Mathf.CeilToInt((maxX - minX) / cell) + extra)) <= Slack
                && Mathf.Abs(height - (Mathf.CeilToInt((maxZ - minZ) / cell) + extra)) <= Slack
                && width > 0 && height > 0 && width * (long)height <= MaxCells;
            if (!ok) { FatNetGuard.Refuse(what + " grid", $"{width} x {height}, bounds {minX}..{maxX}, {minZ}..{maxZ}; that block is skipped"); return false; }
            if (!s_infoSaid) { s_infoSaid = true; FiresLogger.LogInfo("[IceGridCheck] ice grid sizes from ZDOs are range-checked before allocating (0.2.1336)"); }
            FiresLogger.LogDebug($"[IceGridCheck] {what} grid {width} x {height} accepted");
            return true;
        }

        internal static bool Fits(float minX, float minZ, float maxX, float maxZ, float cell, int extra, string what)
        {
            float dx = maxX - minX, dz = maxZ - minZ;
            bool ok = !float.IsNaN(dx) && !float.IsNaN(dz) && !float.IsInfinity(dx) && !float.IsInfinity(dz) && dx >= 0f && dz >= 0f
                && (dx / cell + extra + 1d) * (dz / cell + extra + 1d) <= MaxCells;
            if (!ok) FatNetGuard.Refuse(what, $"bounds {minX}..{maxX}, {minZ}..{maxZ}; that block is skipped");
            return ok;
        }
    }
}
