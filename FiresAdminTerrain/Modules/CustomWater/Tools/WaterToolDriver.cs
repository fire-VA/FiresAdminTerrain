using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Tools
{
    // The water V-tools on [worldgen]'s stroke framework (FAT 0.2.749; VoxelBrushDrag.Strokes.cs, 0.2.748). VoxelBrushDrag owns the
    // input (the click gate, the crosshair, the radius on scroll, one offset per tool on PgUp/PgDn, the stroke pacing); these
    // drivers say what each water tool shows and does (Fire 10-05):
    //   Vpour  drag a water ribbon at the ground under the crosshair + the offset ("click and drag with an offset set and it
    //          should ride that offset along the carve so you can easily fill in rivers"); over an edge, a fall
    //   Vriver drag a river bed, the offset its depth under the ground as it was before the stroke
    //   Vpool  click: level, carve and fill a pool at the hit + the offset, or adopt the water clicked; LeftAlt fills the dip
    //          under the click; with a pool or pond clicked before, the routed river between them ("pond connector")
    //   Vpond  click: fill the dip under a flat circle at the ground + the offset (PondTool), edges become falls
    // Previews are flat: a water disc for the tools that lay water ("v pool should be a flat disk, and it should be water not
    // terrain as the preview"), a ring for Vriver; drawn by PondTool from DrawExtras (the framework's ghost has no water shader).
    internal static class WaterToolDriver
    {
        private static bool s_registered;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            VToolDrivers.Register("Vpour", new Pour());
            VToolDrivers.Register("Vriver", new River());
            VToolDrivers.Register("Vpool", new Pool());
            VToolDrivers.Register("Vpond", new Pond());
            FiresLogger.LogDebug("[FiresWater] water V-tools on the stroke framework: Vpour, Vriver, Vpool, Vpond.");
        }

        private static float WaterSpeed => FiresVoxelConfig.WaterDrawSpeed != null ? FiresVoxelConfig.WaterDrawSpeed.Value : 4f;

        private static Material Lines => VoxelBrushDrag.SharedLineMat();

        private static Ghost NoGhost => new Ghost { Shape = GhostShape.None };

        // A refusal says why in the middle of the screen as well as in the log.
        private static void Say(string line)
        {
            FiresLogger.LogDebug(line);
            int at = line.IndexOf(" refused", System.StringComparison.Ordinal);
            if (at >= 0 && Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, line.Substring(at + 1), 0, null, false);
        }

        private sealed class Pour : IVToolDriver
        {
            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => false;
            public float DefaultOffset => 0.5f;
            public float MinOffset => -8f;
            public string OffsetLabel => "water height";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s)
                => PondTool.PreviewDisc(null, Lines, s.HitGround, s.HitGround.y + s.Offset, s.Radius, true);
            public void Begin(in StrokeInput s) { PondTool.PourStrokeBegin(); Dab(s); }
            public void Dab(in StrokeInput s)
            {
                WaterShapingTools.LevelOffset = s.Offset;
                PondTool.PourStrokeDab(s.HitGround, s.Radius);
            }
            public void End(in StrokeInput s) => PondTool.PourStrokeBegin();
        }

        private sealed class River : IVToolDriver
        {
            private const float BaseDepth = 2.5f;   // WaterShapingTools.RiverDab's depth at LevelOffset 0

            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => false;
            public float DefaultOffset => BaseDepth;
            public float MinOffset => 0.5f;
            public string OffsetLabel => "depth";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s) => PondTool.PreviewDisc(null, Lines, s.HitGround, s.HitGround.y, s.Radius, false);
            public void Begin(in StrokeInput s) { s_riverSurface.Clear(); Dab(s); }
            public void Dab(in StrokeInput s)
            {
                // The bed keeps its depth from the ground as it was before the stroke, not from the trench the stroke has dug
                // (the ground ray would ride the trench floor and every dab would cut deeper).
                Vector3 at = s.HitGround;
                at.y = StrokeSurface(at, s.Radius);
                WaterShapingTools.LevelOffset = BaseDepth - s.Offset;
                WaterShapingTools.RiverDab(at, s.Radius);
            }
            public void End(in StrokeInput s) => s_riverSurface.Clear();
        }

        private sealed class Pool : IVToolDriver
        {
            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => true;
            public float DefaultOffset => 0f;
            public float MinOffset => -8f;
            public string OffsetLabel => "water level";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s) => PondTool.PreviewDisc(null, Lines, s.HitAny, s.HitAny.y + s.Offset, s.Radius, true);
            public void Begin(in StrokeInput s)
            {
                WaterShapingTools.LevelOffset = s.Offset;
                if (s.Alt) Say(WaterShapingTools.FillBowl(s.HitAny, s.Radius));   // LeftAlt: fill the dip under the click to its rim
                else WaterShapingTools.PoolClick(s.HitAny, s.Radius);             // HitAny: a click on a lake adopts the lake
            }
            public void Dab(in StrokeInput s) { }
            public void End(in StrokeInput s) { }
        }

        private sealed class Pond : IVToolDriver
        {
            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => true;
            public float DefaultOffset => 1f;
            public float MinOffset => -8f;
            public string OffsetLabel => "water height";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s)
            {
                PondTool.Offset = s.Offset;
                PondTool.Preview(null, Lines, s.HitGround, s.HitGround.y + s.Offset, s.Radius, Player.m_localPlayer);
            }
            public void Begin(in StrokeInput s)
            {
                PondTool.Offset = s.Offset;
                Say(PondTool.Fill(s.HitGround, s.HitGround.y + s.Offset, s.Radius));
            }
            public void Dab(in StrokeInput s) { }
            public void End(in StrokeInput s) { }
        }

        // ── Vriver's surface as the stroke found it: each 1 m cell is read once, before a dab edits it ───────────────────────
        private static readonly Dictionary<long, float> s_riverSurface = new Dictionary<long, float>();
        private const float SurfaceReach = 12f;

        private static float StrokeSurface(Vector3 at, float radius)
        {
            int r = Mathf.CeilToInt(radius);
            int cx = Mathf.FloorToInt(at.x), cz = Mathf.FloorToInt(at.z);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (s_riverSurface.ContainsKey(key)) continue;
                    s_riverSurface[key] = VoxelWorld.TryFindSurfaceY(cx + dx + 0.5f, cz + dz + 0.5f, at.y, SurfaceReach, out float y) ? y : at.y;
                }
            return s_riverSurface.TryGetValue(((long)cx << 32) ^ (uint)cz, out float centre) ? centre : at.y;
        }
    }
}
