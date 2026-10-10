using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // LOCATION PADS — vanilla levels a location's ground with baked
    // TerrainModifier children that Poke the heightmap flat on spawn. On our
    // voxel worlds that path is fragile: the flatten is radius-tuned for
    // vanilla's GENTLE terrain, and our relief (droplet cuts, swamp basins,
    // crest lifts) is far rougher, so structures sit proud on one side and
    // sink on the other — the "floating locations". We OWN the surface, so we
    // bake the pad ourselves: a flat core at the location's clear radius, a
    // smooth apron out to the feather edge, keyed to the anchor Y the location
    // was placed at. The voxel wrap (GatherColumns) and the placement height
    // query (GetGroundData) both read this, so the terrain the location stands
    // on and the height every later spawn reads AGREE — no floating, no digging
    // a quarry, banks feathered by the same doctrine as our water aprons.
    //
    // MODE looks ahead to the cave/underwater pass: a SurfaceLevel pad flattens
    // the top surface; a SphereCavity leaves the surface alone and (future)
    // hollows a bubble in the voxel density so a cavern location sits inside
    // solid rock without a crater; a SeabedLevel flattens the floor while the
    // water sheet stays overhead. Only SurfaceLevel is wired now.
    internal enum LocationPadMode { SurfaceLevel, SphereCavity, SeabedLevel }

    internal static class FiresLocationPads
    {
        internal struct Pad
        {
            public float Cx, Cz, Y;      // centre + level (world metres)
            public float RFlat, ROuter;  // flat core radius, feather-edge radius
            public LocationPadMode Mode;
        }

        // Indexed by 64m zone: a pad is filed under every zone its ROuter disc
        // touches, so a column only scans its own zone's short list. Main-thread
        // only — Register (SpawnLocation postfix), Sample (GatherColumns /
        // GetGroundData) and Clear all run on the Unity thread; workers consume
        // the already-baked _genGround, never this table.
        private static readonly Dictionary<Vector2i, List<Pad>> s_byZone
            = new Dictionary<Vector2i, List<Pad>>();
        // De-dupe: a location re-spawns (relog, proxy rebuild) through the same
        // postfix; key by rounded centre so the table never grows unbounded.
        private static readonly HashSet<long> s_seen = new HashSet<long>();

        private const float ZONE = 64f;

        internal static bool HasAny => s_byZone.Count > 0;

        internal static void Clear() { s_byZone.Clear(); s_seen.Clear(); }

        // radius = the location's exterior (clear) radius. The structure fills
        // it, so the whole core is flat; the apron feathers out beyond it.
        internal static void Register(Vector3 center, float radius, LocationPadMode mode)
        {
            if (radius <= 0f) return;
            long key = ((long)Mathf.RoundToInt(center.x) * 73856093)
                     ^ ((long)Mathf.RoundToInt(center.z) * 19349663);
            if (!s_seen.Add(key)) return;

            float apron = Mathf.Clamp(radius * 0.5f, 6f, 20f);
            var pad = new Pad
            {
                Cx = center.x,
                Cz = center.z,
                Y = center.y,
                RFlat = radius,
                ROuter = radius + apron,
                Mode = mode
            };
            int z0x = Mathf.FloorToInt((center.x - pad.ROuter) / ZONE);
            int z1x = Mathf.FloorToInt((center.x + pad.ROuter) / ZONE);
            int z0z = Mathf.FloorToInt((center.z - pad.ROuter) / ZONE);
            int z1z = Mathf.FloorToInt((center.z + pad.ROuter) / ZONE);
            for (int zz = z0z; zz <= z1z; zz++)
                for (int zx = z0x; zx <= z1x; zx++)
                {
                    var zk = new Vector2i(zx, zz);
                    if (!s_byZone.TryGetValue(zk, out var list))
                        s_byZone[zk] = list = new List<Pad>(2);
                    list.Add(pad);
                }
        }

        // Blend the natural ground toward any covering pad. Among overlapping
        // pads the one that pulls this column HARDEST toward flat (smallest
        // feather t) wins, so unions read as one clean plateau. Returns false
        // where no SurfaceLevel/SeabedLevel pad reaches — the caller keeps its
        // natural height untouched.
        private const float MaxPadRise = 40f;

        internal static bool TrySample(float wx, float wz, float natural, out float y)
        {
            y = natural;
            if (s_byZone.Count == 0) return false;
            var zk = new Vector2i(Mathf.FloorToInt(wx / ZONE), Mathf.FloorToInt(wz / ZONE));
            if (!s_byZone.TryGetValue(zk, out var list)) return false;

            bool hit = false; float bestT = 2f;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p.Mode == LocationPadMode.SphereCavity) continue; // surface untouched
                // 0.2.740: a pad far above the ground here (a skyland location's, registered at the island's height before
                // 0.2.740 skipped them) never lifts the ground; a real pad sits within a few metres of its natural ground.
                if (p.Y - natural > MaxPadRise) continue;
                float dx = wx - p.Cx, dz = wz - p.Cz;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d >= p.ROuter) continue;
                float t = d <= p.RFlat
                    ? 0f
                    : Mathf.SmoothStep(0f, 1f, (d - p.RFlat) / (p.ROuter - p.RFlat));
                if (t < bestT)
                {
                    bestT = t;
                    y = Mathf.Lerp(p.Y, natural, t);
                    hit = true;
                }
            }
            return hit;
        }
    }
}
