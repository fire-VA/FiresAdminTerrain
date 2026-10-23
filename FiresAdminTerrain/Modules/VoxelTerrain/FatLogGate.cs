using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Some sessions logged 160k lines; FAT's top repeaters were evidence lines that fire per chunk / per zone.
    // One line per tag per minute at most; the next allowed line says how many of that kind were held back in between.
    internal static class FatLogGate
    {
        private const float Every = 60f;
        private static readonly Dictionary<string, (float next, int held)> s_gates = new Dictionary<string, (float, int)>();

        // True when a line of this kind may be written now; held = how many were held back since the last one.
        internal static bool Allow(string tag, out int held)
        {
            float now = Time.unscaledTime;
            s_gates.TryGetValue(tag, out var g);
            if (now < g.next) { s_gates[tag] = (g.next, g.held + 1); held = 0; return false; }
            held = g.held;
            s_gates[tag] = (now + Every, 0);
            return true;
        }

        internal static string Held(int held) => held > 0 ? $" (+{held} more like this in the last minute)" : "";
    }
}
