using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1097 (Fire: "placed water should not freeze as a whole body and lava/water should be able to mix ... to allow for ice to
    // 'melt off' at biome edges and return to water would be ideal"). THE water state, per 1 m column, for placed AND generated
    // water: ice 0..1, lava 0..1, crust 0..1 (cooled lava where lava meets water). It replaces 0.2.1095's whole-body / whole-chunk
    // switching (SeasonalWaterFreeze body states, the combiner BiomeLava flag, VoxelChunkView.ApplyLavaLook).
    //   source columns: ice where the column's biome freezes (VanillaIce.BiomeFreezes: Deep North always - the painted biome, the
    //     island's biome at height - other biomes in Vedr's winter), lava where it is Ashlands;
    //   soft edges: a column outside a source gets 1 - smoothstep(0, band, distance to the nearest source column) ("Water state
    //     band (m)", default 6), so a pond crossing a Deep North edge is ice, slush, then open water;
    //   crust = smoothstep(0.05, 0.25, lava) below _FiresLavaHot: the cooled band between glowing lava and water (walkable, no burn).
    // Derived, never saved. Cached per 32 m tile and 16 m height band; dropped when the biome paint, the season rule or the band
    // changes (Tick, every 2 s, main thread). The look is Core's Custom/FiresInlandWater (Tools\FAT1097_WaterState_ShaderContract.md):
    // a 34 x 34 RGBA32 state texture per drawn sheet chunk on its MPB (R ice, G lava, B crust), plus the globals _FiresIceSolid /
    // _FiresLavaHot, which the physics uses too (ice colliders, crust colliders, no liquid level, FatLava heat).
    internal static class WaterStateField
    {
        internal struct State { public float Ice, Lava, Crust; }

        internal static ConfigEntry<float> BandM, IceSolidCfg, LavaHotCfg;
        internal static float IceSolid => IceSolidCfg != null ? IceSolidCfg.Value : 0.6f;
        internal static float LavaHot => LavaHotCfg != null ? LavaHotCfg.Value : 0.5f;
        private static float Band => BandM != null ? Mathf.Max(0.5f, BandM.Value) : 6f;

        private const int Tile = 32, HeightBand = 16, MaxMargin = 16;
        // 0.2.1168: per column (ice, lava, crust, ground y, flags) - ground + flags feed the timed ice (IceField).
        private const int Stride = 5, FlagDeepNorth = 1, FlagRiver = 2;
        private static readonly ConcurrentDictionary<long, float[]> s_tiles = new ConcurrentDictionary<long, float[]>();
        private static readonly object s_lock = new object();
        private static int s_version = 1, s_paint = int.MinValue, s_season = int.MinValue;
        private static float s_band = -1f, s_nextTick, s_tileLogAt;
        private static bool s_saidRemoved;

        internal static int Version => s_version;

        internal static void BindConfig(ConfigFile cfg, string section)
        {
            BandM = cfg.Bind(section, "Water state band (m)", 6f, new ConfigDescription(
                "How far ice and lava fade into open water at a biome edge (Deep North ice, Ashlands lava). The water in between is slush or cooled crust.",
                new AcceptableValueRange<float>(0.5f, 16f)));
            IceSolidCfg = cfg.Bind(section, "Ice solid threshold", 0.6f, new ConfigDescription(
                "Ice amount at which frozen water becomes walkable, slippery ice (below it: slush you swim in).", new AcceptableValueRange<float>(0.05f, 1f)));
            LavaHotCfg = cfg.Bind(section, "Lava hot threshold", 0.5f, new ConfigDescription(
                "Lava amount at which it glows and burns (below it: cooled crust you can walk on).", new AcceptableValueRange<float>(0.05f, 1f)));
            SeamlessSheetsCfg = cfg.Bind(section, "Seamless placed water sheets", true,
                "Placed water and its ice lie on the water surface everywhere, with no step where two sheet pieces meet (also on slopes). Off = as before.");
        }
        internal static ConfigEntry<bool> SeamlessSheetsCfg;   // 0.2.1198
        internal static bool SeamlessSheets => SeamlessSheetsCfg != null && SeamlessSheetsCfg.Value;

        // Main thread, from SeasonalWaterFreeze.Tick: drops the tiles when the paint, the season rule or the band changes, and
        // remeshes what depends on them.
        internal static void Tick()
        {
            s_mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;   // 0.2.1177: the roof test reads voxel chunks (main thread only)
            FiresWaves.Tick();        // 0.2.1175: wave multiplier (server computes, peers receive), shader globals
            IceField.Tick();          // 0.2.1168: the timed ice exposure (own 2 s gate)
            RefreshStale();           // 0.2.1168: refill state textures left stale by an ice step (budgeted, no sheet rebuild)
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + 2f;
            Shader.SetGlobalFloat("_FiresIceSolid", IceSolid);
            Shader.SetGlobalFloat("_FiresLavaHot", LavaHot);
            if (!s_saidRemoved)
            {
                s_saidRemoved = true;
                FiresLogger.LogDebug("[WaterState] removed the 1095 per-body switching: placed and generated water take ice / lava / crust per 1 m column from WaterStateField");
            }
            int paint = VerdantsAscent.Terrain.Zones.BiomePaintVersion.Current, season = VanillaIce.SeasonSignature;
            float band = Band;
            if (paint == s_paint && season == s_season && Mathf.Approximately(band, s_band)) return;
            bool first = s_paint == int.MinValue;
            s_paint = paint; s_season = season; s_band = band;
            s_tiles.Clear();
            s_version++;
            if (first) return;
            int n = VerdantsAscent.Terrain.Voxel.FatSeasons.RemeshWaterForBiomeChange();
            if (ClutterSystem.instance != null) ClutterSystem.instance.ClearAll();   // life re-validates against the new state (no stale floaters)
            PlacedWaterSheets.MarkAllDirty();
            PlacedWaterSolids.MarkAllDirty("paint / season / band changed");
            FiresLogger.LogDebug($"[WaterState] paint / season / band changed (paint {paint}, season 0x{season:X}, band {band:0.#} m): state v{s_version}, {n} generated water chunk(s) remeshed, placed sheets + ice/crust colliders rebuilt");
        }

        private static long Key(int tx, int tz, int band) => (((long)tx & 0xFFFFF) << 40) | (((long)tz & 0xFFFFF) << 20) | ((long)band & 0xFFFFF);

        internal static State Sample(float x, float z, float y)
        {
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            int tx = Mathf.FloorToInt(ix / (float)Tile), tz = Mathf.FloorToInt(iz / (float)Tile), band = Mathf.FloorToInt(y / HeightBand);
            long key = Key(tx, tz, band);
            if (!s_tiles.TryGetValue(key, out var t))
                lock (s_lock)
                    if (!s_tiles.TryGetValue(key, out t)) { t = Build(tx, tz, band); s_tiles[key] = t; }
            int i = (ix - tx * Tile) + Tile * (iz - tz * Tile);
            float ice = t[i * Stride];
            if (ice > 0f && Sheltered(x, z, y, t[i * Stride + 3])) ice = 0f;   // 0.2.1169: caves never freeze (Fire)
            // 0.2.1168: outside Deep North the biome-rule ice is scaled by the timed ice (IceField): shore first, thick enough to walk on
            // from 0.10 m, fires keep holes open.
            if (ice > 0f && !s_dnSaid && ((int)t[i * Stride + 4] & FlagDeepNorth) != 0) { s_dnSaid = true; FiresLogger.LogDebug($"[WaterIce] Deep North water: ice forced (always frozen, no timed scaling) at ({x:0.0},{z:0.0}) y {y:0.0}, ice {ice:0.00} (0.2.1177)"); }
            if (ice > 0f && ((int)t[i * Stride + 4] & FlagDeepNorth) == 0)
            {
                IceField.DeriveAt(x, z, y - t[i * Stride + 3], ((int)t[i * Stride + 4] & FlagRiver) != 0, out float cover, out float thick);   // 0.2.1170: + broken holes
                ice *= IceField.IceAmount(cover, thick, IceSolid);
                if (ice > 0f && VerdantsAscent.Terrain.Voxel.FatFireBullseye.HasHeat && VerdantsAscent.Terrain.Voxel.FatFireBullseye.InMeltCore(new Vector3(x, y, z))) ice = 0f;
            }
            return new State { Ice = ice, Lava = t[i * Stride + 1], Crust = t[i * Stride + 2] };
        }

        // 0.2.1168: the timed ice at a column for FatApi.TryIceAt (cover 0..1 including the biome-edge fade, thickness in metres).
        // 0.2.1317: per-thread counters read by IceField.TryIceAt's slow-call line (Debug); timing only.
        [System.ThreadStatic] internal static long IceDetailLockWaitTicks, IceDetailBuildTicks;
        [System.ThreadStatic] internal static int IceDetailTileBuilds;

        internal static void IceDetail(float x, float z, float y, out float cover, out float thick)
        {
            cover = 0f; thick = 0f;
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            int tx = Mathf.FloorToInt(ix / (float)Tile), tz = Mathf.FloorToInt(iz / (float)Tile), band = Mathf.FloorToInt(y / HeightBand);
            long key = Key(tx, tz, band);
            if (!s_tiles.TryGetValue(key, out var t))
            {
                long tl = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1317: TryIceAt phase timing (lock wait + tile build)
                lock (s_lock)
                {
                    long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                    IceDetailLockWaitTicks += tb - tl;
                    if (!s_tiles.TryGetValue(key, out t)) { t = Build(tx, tz, band); s_tiles[key] = t; IceDetailTileBuilds++; IceDetailBuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tb; }
                }
            }
            int i = (ix - tx * Tile) + Tile * (iz - tz * Tile);
            float raw = t[i * Stride];
            if (raw <= 0f || Sheltered(x, z, y, t[i * Stride + 3])) return;   // 0.2.1169: caves never freeze
            int fl = (int)t[i * Stride + 4];
            if ((fl & FlagDeepNorth) != 0) { cover = raw; thick = raw >= IceSolid ? 1f : 0f; return; }
            IceField.DeriveAt(x, z, y - t[i * Stride + 3], (fl & FlagRiver) != 0, out float c, out float th);
            if (VerdantsAscent.Terrain.Voxel.FatFireBullseye.HasHeat && VerdantsAscent.Terrain.Voxel.FatFireBullseye.InMeltCore(new Vector3(x, y, z))) { c = 0f; th = 0f; }
            cover = raw * c; thick = th;
        }

        // 0.2.1169 (Fire: "placed water in caves should just never freeze since its sheltered from the elements"): a water surface
        // more than 1.5 m under the generated ground is in a cave - the same rule as the cave camera (FiresCaveCamera.EyeUnderground,
        // FiresCaveCamera.cs:45), using the generated ground already cached per column (FiresCaveCamera.TryGenGround in Build).
        // No raycast: Deep North included, it never ices there.
        internal static int ShelteredSkipped;
        private static bool s_shelterSaid;
        // 0.2.1177 (Fire: "the recent icing over logic broke some meshes along the shores" / "they are stepped and disconnected and no
        // longer ice in the deep north"): "below the generated ground" alone also caught every pond DUG into the ground (the surface
        // sits under the pre-dig generated height but has open sky above), so columns of one pond flipped between iced and not -
        // stepped, disconnected ice shells at the shore, and dug Deep North ponds lost their ice. A cave now needs a real ROOF:
        // solid voxel 1.5-20 m above the surface. Unloaded chunks answer "not sheltered" and are not cached. Cached per column.
        private static readonly ConcurrentDictionary<long, bool> s_roof = new ConcurrentDictionary<long, bool>();
        private static int s_roofVersion = -1, s_openSkipped, s_roofLogAtFrame;
        private static int s_mainThread = -1;
        internal static int MainThread => s_mainThread;   // 0.2.1203: IceApiTiming frame totals
        private static bool s_dnSaid;
        private static bool Sheltered(float x, float z, float y, float g)
        {
            if (y >= g - 1.5f) return false;
            if (s_roofVersion != s_version) { s_roof.Clear(); s_roofVersion = s_version; }
            long k = Key(Mathf.FloorToInt(x), Mathf.FloorToInt(z), Mathf.FloorToInt(y * 2f));
            if (!s_roof.TryGetValue(k, out bool roofed))
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThread) return false;   // worker: no chunk reads; not cached
                if (!TryRoof(x, z, y, out roofed)) return false;
                s_roof[k] = roofed;
            }
            if (!roofed) { s_openSkipped++; return false; }
            ShelteredSkipped++;
            if (!s_shelterSaid)
            {
                s_shelterSaid = true;
                FiresLogger.LogDebug($"[WaterIce] sheltered water never freezes: placed water at ({x:0.0},{z:0.0}) skipped (reason: cave, surface y {y:0.0} under a solid roof, generated ground {g:0.0}) (0.2.1177)");
            }
            return true;
        }

        // Solid voxel above the surface = a roof. false = a chunk on the way is not loaded (unknown; caller treats it as open).
        private static bool TryRoof(float x, float z, float y, out bool roofed)
        {
            roofed = false;
            for (float dy = 1.5f; dy <= 20f; dy += 1f)
            {
                float wy = y + dy;
                var key = new Vector3Int(FloorDiv(Mathf.FloorToInt(x), VerdantsAscent.Terrain.Voxel.VoxelChunk.CELLS),
                    FloorDiv(Mathf.FloorToInt(wy), VerdantsAscent.Terrain.Voxel.VoxelChunk.CELLS),
                    FloorDiv(Mathf.FloorToInt(z), VerdantsAscent.Terrain.Voxel.VoxelChunk.CELLS));
                if (!VerdantsAscent.Terrain.Voxel.VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return false;
                sbyte d = chunk.SampleDensityWorld(new Vector3(x, wy, z));
                if (d == sbyte.MinValue) return false;
                if (d > 0) { roofed = true; return true; }
            }
            return true;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

        // Debug proof, from the 30 s [WaterIce] line: dug/open ponds below the generated ground that now keep their ice.
        internal static int TakeOpenSkipped() { int n = s_openSkipped; s_openSkipped = 0; return n; }

        internal static int TileCount => s_tiles.Count;

        // 0.2.1168 (IceField step): the derived ice changed everywhere. Textures are refilled from the cached tiles (no sheet rebuild);
        // ice colliders rebuild; generated water remeshes at most once a minute (its liquid / no-liquid columns follow the ice).
        // 0.2.1170: IceField now applies a step at most once a minute (the gate moved there), so this always remeshes: the colliders,
        // the no-liquid columns and the look change together.
        internal static void IceChanged()
        {
            s_version++;
            PlacedWaterSolids.MarkAllDirty("ice step (IceField exposure)");
            {
                // 0.2.1173 (the lead: the per-step remesh is unmeasured and Fire watches perf): time the main-thread part of the step
                // (the chunk enqueue + the far-water rebuild run here, synchronously); the chunk meshes themselves land later inside the
                // pipeline's frame budget and show in "[FiresVoxelPipe] apply parts" / "frame budget".
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int n = VerdantsAscent.Terrain.Voxel.FatSeasons.RemeshWaterForBiomeChange();
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                FiresLogger.LogDebug($"[WaterIce] ice step: state v{s_version}, {n} generated water chunk(s) queued for remesh (main-thread step {ms:0.00} ms: enqueue + far-water rebuild; the chunk applies land under the pipeline budget), ice colliders rebuilt, {s_entries.Count} state texture(s) to refill");
            }
        }

        // 0.2.1170: a broken hole (or its refreeze) changed the ice in a few columns: refill the state textures, rebuild the placed
        // ice collider tile and remesh only the generated water chunks around it (no world-wide remesh).
        // 0.2.1179: the frost (base or polished cells) changed: the state textures refill (budgeted, RefreshStale); no remesh, no colliders.
        internal static void FrostChanged() { s_frostVersion++; }   // own counter: the roof cache / steam notes stay
        private static int s_frostVersion;

        internal static void HoleChanged(float x, float z, float y)
        {
            s_version++;
            PlacedWaterSolids.MarkDirtyAt(x, z, "ice hole broke / refroze");
            VerdantsAscent.Terrain.Voxel.IceChunkRemesh.Around(x, y, z);
        }

        private static void RefreshStale()
        {
            if (s_entries.Count == 0) return;
            int done = 0;
            foreach (var e in s_entries.Values)
            {
                if ((e.Version == s_version && e.FrostV == s_frostVersion) || e.R == null) continue;
                Fill(e, e.Origin, e.Y);
                if (++done >= 8) break;   // ~1.2k samples each: a few per frame
            }
        }

        internal static bool IceSolidAt(float x, float z, float y) => Sample(x, z, y).Ice >= IceSolid;
        internal static bool LavaHotAt(float x, float z, float y) => Sample(x, z, y).Lava >= LavaHot;
        internal static bool CrustAt(float x, float z, float y) { var s = Sample(x, z, y); return s.Crust >= 0.5f && s.Lava < LavaHot; }
        // Open water: swimmable and alive (fish, lilies, wet ground) - no slush, crust or lava on it.
        internal static bool PureWaterAt(float x, float z, float y) { var s = Sample(x, z, y); return s.Ice < 0.2f && s.Lava < 0.05f && s.Crust < 0.05f; }
        // Not liquid: solid ice, walkable crust, or hot lava (no liquid level, no swimming).
        // 0.2.1313: where a sheet chunk's column grid exists (the one the drawn state texture and the ice collider are built from), the
        // ice answer is that grid's BILINEAR value at the point (the value the shader samples and the collider clips at), so you swim
        // exactly where the drawn sheet is not solid ice and walk exactly where it is. No grid yet = the per-column sample as before.
        internal static bool NotLiquidAt(float x, float z, float y) => NotLiquidAt(x, z, y, GridSrc.Gen);
        internal static bool NotLiquidAt(float x, float z, float y, GridSrc src)
        {
            var s = Sample(x, z, y);
            if (s.Lava >= LavaHot || (s.Crust >= 0.5f && s.Lava < LavaHot)) return true;
            if (TryGridIce(src, x, z, out float gi)) return gi >= IceSolid;
            return s.Ice >= IceSolid;
        }

        // ---- 0.2.1313 ONE SOURCE (Fire: "the visual and collision should match"; RenderDoc 10-31 frame28800 ev 5850 / 5876: two
        // chunks of one pond, one state texture all 0, the other all 1, because each texture was filled at ONE height per chunk - the
        // middle vertex (generated) or the mid surface (placed) - while the colliders and the no-liquid test read each column at its own
        // level). Every sheet chunk now has ONE 34 x 34 column grid (columns -1..32, the state texture's texels), each column sampled
        // at ITS OWN water level (a dry texel takes its nearest wet neighbour's level). The texture, the ice collider (marching squares
        // on the grid's bilinear corner values at _FiresIceSolid) and NotLiquidAt all read that grid.
        internal enum GridSrc { Gen = 0, Placed = 1 }
        internal sealed class Grid
        {
            public readonly float[] Ice = new float[Px * Px], Lava = new float[Px * Px], Crust = new float[Px * Px];
            public float OX, OZ, LoY, HiY; public int Version, FrostV, Bands; public uint LevelSum; public bool AnyIce;
        }
        private static readonly ConcurrentDictionary<long, Grid>[] s_grids = { new ConcurrentDictionary<long, Grid>(), new ConcurrentDictionary<long, Grid>() };
        private static long GridKey(float ox, float oz) => ((long)Mathf.FloorToInt(ox / 32f) << 32) ^ (uint)Mathf.FloorToInt(oz / 32f);
        internal const int GridPx = Px;

        private static uint LevelSum(float[] levels)
        {
            uint h = 2166136261u;
            if (levels != null) for (int k = 0; k < levels.Length; k++) h = (h ^ (uint)Mathf.RoundToInt(levels[k] * 100f)) * 16777619u;
            return h;
        }

        // levels: Px x Px (columns -1..32 of the chunk at ox, oz), > 0 = that column's water level; fallbackY for a texel with no wet
        // column within 1 (it is drawn only as the bank skirt). Main thread (Sample's roof test). Re-used while nothing changed.
        internal static Grid BuildGrid(GridSrc src, float ox, float oz, float[] levels, float fallbackY)
        {
            uint sum = LevelSum(levels);
            long key = GridKey(ox + 1f, oz + 1f);
            var dict = s_grids[(int)src];
            if (dict.TryGetValue(key, out var old) && old.Version == s_version && old.FrostV == s_frostVersion && old.LevelSum == sum
                && old.OX == ox && old.OZ == oz) return old;
            var g = new Grid { OX = ox, OZ = oz, Version = s_version, FrostV = s_frostVersion, LevelSum = sum, LoY = float.MaxValue, HiY = float.MinValue };
            for (int k = 0; k < Px * Px; k++)
            {
                float l = levels != null && k < levels.Length ? levels[k] : 0f;
                if (l <= 0f)
                {
                    // nearest wet neighbour (8-neighbourhood), else the chunk's fallback height
                    int i = k % Px, j = k / Px; float best = 0f;
                    for (int dj = -1; dj <= 1 && best <= 0f; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = i + di, jj = j + dj;
                            if (ii < 0 || jj < 0 || ii >= Px || jj >= Px || levels == null) continue;
                            float nl = levels[ii + Px * jj];
                            if (nl > 0f) { best = nl; break; }
                        }
                    l = best > 0f ? best : fallbackY;
                }
                else { if (l < g.LoY) g.LoY = l; if (l > g.HiY) g.HiY = l; }
                var s = Sample(ox + (k % Px), oz + (k / Px), l);
                g.Ice[k] = s.Ice; g.Lava[k] = s.Lava; g.Crust[k] = s.Crust;
                if (s.Ice > 0f) g.AnyIce = true;
            }
            if (g.LoY <= g.HiY) g.Bands = Mathf.FloorToInt(g.HiY / HeightBand) - Mathf.FloorToInt(g.LoY / HeightBand) + 1;
            dict[key] = g;
            return g;
        }

        // The grid's bilinear ice at a world point (texel centres = column centres), as the shader samples the state texture.
        internal static bool TryGridIce(GridSrc src, float x, float z, out float ice)
        {
            ice = 0f;
            if (!s_grids[(int)src].TryGetValue(GridKey(x, z), out var g)) return false;
            float u = x - g.OX - 0.5f, v = z - g.OZ - 0.5f;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, Px - 2), j0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, Px - 2);
            float fu = Mathf.Clamp01(u - i0), fv = Mathf.Clamp01(v - j0);
            float a = Mathf.Lerp(g.Ice[i0 + Px * j0], g.Ice[i0 + 1 + Px * j0], fu);
            float b = Mathf.Lerp(g.Ice[i0 + Px * (j0 + 1)], g.Ice[i0 + 1 + Px * (j0 + 1)], fu);
            ice = Mathf.Lerp(a, b, fv);
            return true;
        }

        // The grid of the 32 m chunk column whose origin is (ox, oz), if one was built.
        internal static bool TryGetGrid(GridSrc src, float ox, float oz, out Grid g) => s_grids[(int)src].TryGetValue(GridKey(ox + 0.5f, oz + 0.5f), out g);

        // The bilinear value at a cell CORNER (local corner cx, cz in 0..32 of the chunk) = the mean of the 4 texels around it.
        internal static float CornerIce(Grid g, int cx, int cz)
            => 0.25f * (g.Ice[cx + Px * cz] + g.Ice[cx + 1 + Px * cz] + g.Ice[cx + Px * (cz + 1)] + g.Ice[cx + 1 + Px * (cz + 1)]);

        // 0.2.1313 proof, Debug: one line per chunk build (rate-limited per chunk), the drawn solid cells (cell-centre bilinear >= solid,
        // what the shader draws as solid) vs the collider cells (cells the clipped collider covers at its centre), mismatches, and the
        // fill heights / bands the grid used.
        private static readonly ConcurrentDictionary<long, float> s_mismatchLogAt = new ConcurrentDictionary<long, float>();
        internal static int MismatchTotal;
        internal static void NoteCells(string what, Grid g, int visual, int collider, int visualOnly, int colliderOnly, string firstMismatch)
        {
            if (visualOnly + colliderOnly > 0) System.Threading.Interlocked.Add(ref MismatchTotal, visualOnly + colliderOnly);
            if (!FiresLogger.DebugEnabled || (!g.AnyIce && visual + collider == 0)) return;
            long k = GridKey(g.OX + 1f, g.OZ + 1f) ^ ((long)what.GetHashCode() << 20);
            float now = Time.unscaledTime;
            if (s_mismatchLogAt.TryGetValue(k, out float at) && now < at) return;
            s_mismatchLogAt[k] = now + 30f;
            FiresLogger.LogDebug($"[WaterIce] ice cells chunk ({g.OX + 1f:0},{g.OZ + 1f:0}) {what}: visual solid {visual} (cell-centre bilinear >= {IceSolid:0.00}), collider solid {collider}, mismatch {visualOnly + colliderOnly} (visual-only {visualOnly}, collider-only {colliderOnly}){firstMismatch}; fill levels {(g.LoY <= g.HiY ? $"{g.LoY:0.00}..{g.HiY:0.00}, {g.Bands} band(s)" : "none (fallback height)")}; one grid feeds texture + collider + no-liquid (0.2.1313)");
        }

        private static bool s_oneSourceSaid;
        internal static void SayOneSource(string what)
        {
            if (s_oneSourceSaid) return;
            s_oneSourceSaid = true;
            FiresLogger.LogInfo($"[WaterIce] one source: the ice look (state texture), the ice collider and the no-liquid test now read ONE per-column grid sampled at each column's own water level (first: {what} chunk) (0.2.1313)");
        }

        // One tile: 32 x 32 columns x (ice, lava, crust), from the biomes of the tile plus its band margin.
        private static float[] Build(int tx, int tz, int band)
        {
            float bandM = Band;
            int m = Mathf.Clamp(Mathf.CeilToInt(bandM), 1, MaxMargin), n = Tile + 2 * m;
            float y = band * HeightBand + HeightBand * 0.5f;
            var freeze = new bool[n * n];
            var lavaSrc = new bool[n * n];
            var deepNorth = new bool[n * n];
            var wg = WorldGenerator.instance;
            var biomeFreezes = new Dictionary<Heightmap.Biome, bool>();
            int x0 = tx * Tile - m, z0 = tz * Tile - m;
            bool anyF = false, anyL = false;
            if (wg != null)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        var b = wg.GetBiome(new Vector3(x0 + i + 0.5f, y, z0 + j + 0.5f));
                        if (!biomeFreezes.TryGetValue(b, out bool f)) biomeFreezes[b] = f = VanillaIce.BiomeCanIce(b);   // 0.2.1168: timed ice (IceField) decides the amount
                        freeze[i + n * j] = f; anyF |= f;
                        deepNorth[i + n * j] = b == Heightmap.Biome.DeepNorth;
                        bool l = b == Heightmap.Biome.AshLands;
                        lavaSrc[i + n * j] = l; anyL |= l;
                    }
            var t = new float[Tile * Tile * Stride];
            int ice = 0, slush = 0, lava = 0, crust = 0, water = 0;
            float hot = LavaHot, solid = IceSolid;
            for (int j = 0; j < Tile; j++)
                for (int i = 0; i < Tile; i++)
                {
                    float iceA = anyF ? Amount(freeze, n, i + m, j + m, m, bandM) : 0f;
                    float lavaA = anyL ? Amount(lavaSrc, n, i + m, j + m, m, bandM) : 0f;
                    iceA *= 1f - lavaA;   // lava wins where both reach
                    float crustA = lavaA >= hot ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.25f, lavaA));
                    int k = (i + Tile * j) * Stride;
                    t[k] = iceA; t[k + 1] = lavaA; t[k + 2] = crustA;
                    // 0.2.1168: ground under the column + flags for the timed ice (only where ice can form at all)
                    if (iceA > 0f)
                    {
                        float wx = x0 + m + i + 0.5f, wz = z0 + m + j + 0.5f;
                        float g;
                        if (!VerdantsAscent.Terrain.Voxel.FiresCaveCamera.TryGenGround(wx, wz, out g)) g = wg != null ? wg.GetHeight(wx, wz) : y;
                        int fl = deepNorth[(i + m) + n * (j + m)] ? FlagDeepNorth : 0;
                        if (fl == 0 && ((i | j) & 1) == 0)
                        {
                            try { if (VerdantsAscent.Terrain.Voxel.VoxelRiverNetwork.TrySurfaceAt(wx, wz, out _)) fl |= FlagRiver; } catch { }
                        }
                        else if (fl == 0) fl = (int)t[((i & ~1) + Tile * (j & ~1)) * Stride + 4] & FlagRiver;   // the 2 m cell's river flag
                        t[k + 3] = g; t[k + 4] = fl;
                    }
                    if (lavaA >= hot) lava++;
                    else if (crustA >= 0.5f) crust++;
                    else if (iceA >= solid) ice++;
                    else if (iceA >= 0.2f) slush++;
                    else water++;
                }
            if ((anyF || anyL) && Time.realtimeSinceStartup >= s_tileLogAt)
            {
                s_tileLogAt = Time.realtimeSinceStartup + 2f;
                FiresLogger.LogDebug($"[WaterState] tile ({tx},{tz}) y {band * HeightBand}: ice {ice}, slush {slush}, lava {lava}, crust {crust}, water {water} column(s) (band {bandM:0.#} m, state v{s_version})");
            }
            return t;
        }

        // 1 on a source column; else 1 - smoothstep(0, band, distance to the nearest source column within the margin).
        private static float Amount(bool[] src, int n, int ci, int cj, int m, float band)
        {
            if (src[ci + n * cj]) return 1f;
            float best = float.MaxValue;
            for (int dj = -m; dj <= m; dj++)
                for (int di = -m; di <= m; di++)
                {
                    if (!src[(ci + di) + n * (cj + dj)]) continue;
                    float d = di * di + dj * dj;
                    if (d < best) best = d;
                }
            if (best == float.MaxValue) return 0f;
            float dist = Mathf.Sqrt(best) - 0.5f;
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(dist / band));
        }

        // ---- the per-chunk state texture on a sheet renderer's MPB ----
        internal static readonly int TexId = Shader.PropertyToID("_FiresWaterStateTex"), RectId = Shader.PropertyToID("_FiresWaterStateRect");
        private const int Px = 34;
        private sealed class Entry { public Texture2D Tex; public int Version, FrostV; public Vector3 Origin; public float Y; public Renderer R; public float[] Levels; public GridSrc Src; public uint LevelSum; }   // 0.2.1313: + the column levels
        private static readonly Dictionary<int, Entry> s_entries = new Dictionary<int, Entry>();
        private static readonly Color32[] s_px = new Color32[Px * Px];
        private static MaterialPropertyBlock s_mpb;
        private static float s_bindLogAt, s_edgeLogAt;
        private static readonly HashSet<string> s_pathSaid = new HashSet<string>();
        private static int s_bound;

        // True when the sheet material's shader reads the state (Core's FiresInlandWater 1097+): FAT then draws ice / lava / crust
        // through the water sheet; with an older shader the generated ice keeps its own IceShell renderer.
        // 0.2.1100 (Fire: frozen pond reads as clear moving water; his log had ice columns but never a "bound ... on=1" line): Core's
        // FiresInlandWater v6 declares _FiresWaterStateTex / _FiresWaterStateRect as CGPROGRAM uniforms only, not in its Properties
        // block, so Material.HasProperty was always false and every sheet got on = 0. The contract shader is recognised by name; an
        // MPB value an older shader doesn't read is simply ignored, so binding it is harmless there.
        internal static bool ShaderReads(Material m)
            => m != null && m.shader != null && (m.HasProperty(TexId) || m.shader.name == "Custom/FiresInlandWater");
        private static bool s_notBoundSaid;
        private static float s_iceMaxLast = -1f;

        // Binds (or refreshes) a sheet chunk's state texture: chunk origin (world, 32 m chunk), the water's height for the biome.
        internal static void Bind(Renderer r, Vector3 chunkOrigin, float y, string what) => Bind(r, chunkOrigin, y, what, null);

        // 0.2.1313: levels = the chunk's Px x Px column water levels (> 0 wet; VoxelChunk.SAMPLES layout, columns -1..32): the texture is
        // filled from the ONE column grid (BuildGrid) the collider and the no-liquid test read.
        internal static void Bind(Renderer r, Vector3 chunkOrigin, float y, string what, float[] levels)
        {
            if (r == null) return;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_mpb);
            if (!ShaderReads(r.sharedMaterial))
            {
                s_mpb.SetVector(RectId, Vector4.zero);   // on = 0: the shader (if it declares it at all) draws plain water
                r.SetPropertyBlock(s_mpb);
                if (!s_notBoundSaid)
                {
                    s_notBoundSaid = true;
                    var sm = r.sharedMaterial;
                    FiresLogger.LogInfo($"[WaterState] NOT bound: sheet material '{(sm != null ? sm.name : "none")}' shader '{(sm != null && sm.shader != null ? sm.shader.name : "none")}' lacks _FiresWaterStateTex (needs Core 0.2.860+ FiresInlandWater)");
                }
                return;
            }
            int id = r.GetInstanceID();
            if (!s_entries.TryGetValue(id, out var e))
            {
                if (s_entries.Count > 4096) Purge();
                e = new Entry { Tex = new Texture2D(Px, Px, TextureFormat.RGBA32, false, true) { name = "FiresWaterState", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear }, Version = -1, R = r };
                s_entries[id] = e;
            }
            uint lsum = levels != null ? LevelSum(levels) : 0u;
            if (levels != null)
            {
                if (e.Levels == null) e.Levels = new float[Px * Px];
                System.Array.Copy(levels, e.Levels, Mathf.Min(levels.Length, Px * Px));
                e.Src = what == "placed" ? GridSrc.Placed : GridSrc.Gen;
            }
            else e.Levels = null;
            if (e.Version != s_version || e.FrostV != s_frostVersion || e.Origin != chunkOrigin || !Mathf.Approximately(e.Y, y) || e.LevelSum != lsum)
            {
                if (levels != null && e.Src == GridSrc.Placed && e.LevelSum != lsum) PlacedWaterSolids.MarkDirtyAt(chunkOrigin.x + 1f, chunkOrigin.z + 1f, "sheet column levels changed");   // 0.2.1313: the collider re-cuts from the new grid
                e.LevelSum = lsum;
                Fill(e, chunkOrigin, y);
                if (Time.realtimeSinceStartup >= s_bindLogAt)
                {
                    s_bindLogAt = Time.realtimeSinceStartup + 10f;
                    FiresLogger.LogDebug($"[WaterState] bound {what} chunk ({chunkOrigin.x:0},{chunkOrigin.z:0}) y {y:0.0} tex {e.Tex.GetInstanceID()} on=1, ice max {s_iceMaxLast:0.00}, wave amp x(1-ice) and ice opacity by Core per contract ({s_bound} fill(s), {s_entries.Count} texture(s), state v{s_version})");
                }
            }
            if (s_pathSaid.Add(what)) FiresLogger.LogDebug($"[WaterIce] bind path '{what}': rect.w = 2 (frost in A), chunk ({chunkOrigin.x:0},{chunkOrigin.z:0}) y {y:0.0} (0.2.1183)");
            s_mpb.SetTexture(TexId, e.Tex);
            s_mpb.SetVector(RectId, new Vector4(chunkOrigin.x - 1f, chunkOrigin.z - 1f, 1f / Px, 2f));   // 0.2.1180: w = 2 = "this FAT writes frost in A" (the Core ice drop-in gates frost on w >= 1.5; older FAT = 1)
            r.SetPropertyBlock(s_mpb);
        }

        // 0.2.1168: the texture fill, shared by Bind and the ice-step refresh (the MPB already points at e.Tex).
        private static void Fill(Entry e, Vector3 chunkOrigin, float y)
        {
            {
                float iceMax = 0f;
                Grid g = e.Levels != null ? BuildGrid(e.Src, chunkOrigin.x - 1f, chunkOrigin.z - 1f, e.Levels, y) : null;   // 0.2.1313 one source
                if (g != null) SayOneSource(e.Src == GridSrc.Placed ? "placed" : "generated");
                for (int j = 0; j < Px; j++)
                    for (int i = 0; i < Px; i++)
                    {
                        var s = g != null ? new State { Ice = g.Ice[i + Px * j], Lava = g.Lava[i + Px * j], Crust = g.Crust[i + Px * j] }
                                          : Sample(chunkOrigin.x - 1f + i, chunkOrigin.z - 1f + j, y);
                        if (s.Ice > iceMax) iceMax = s.Ice;
                        // 0.2.1179 A = frost (contract: 0 = legacy / no ice; on ice 128 + 127 x frost = 0.5 polished clear .. 1 full frost haze)
                        byte a = s.Ice > 0.004f ? (byte)(128 + Mathf.RoundToInt(127f * IceFrost.FrostSmoothAt(chunkOrigin.x - 1f + i, chunkOrigin.z - 1f + j))) : (byte)0;   // 0.2.1183: tent-smoothed (no hard polish edge)
                        s_px[i + Px * j] = new Color32((byte)(s.Ice * 255f + 0.5f), (byte)(s.Lava * 255f + 0.5f), (byte)(s.Crust * 255f + 0.5f), a);
                    }
                s_iceMaxLast = iceMax;
                // 0.2.1183 proof: each chunk's border texels (the ones its neighbour also samples) with its height band, so a round shows
                // whether two neighbouring chunks of one pond disagree (a different band / ice / frost at the shared edge = a hard cut).
                if (FiresLogger.DebugEnabled && Time.realtimeSinceStartup >= s_edgeLogAt && iceMax > 0f)
                {
                    s_edgeLogAt = Time.realtimeSinceStartup + 5f;
                    float iw = 0f, ie = 0f, fw = 0f, fe = 0f; int n = 0;
                    for (int j = 1; j < Px - 1; j++)
                    {
                        var w = s_px[1 + Px * j]; var ea = s_px[Px - 2 + Px * j];
                        iw += w.r; ie += ea.r; fw += w.a; fe += ea.a; n++;
                    }
                    FiresLogger.LogDebug($"[WaterIce] state edge: chunk ({chunkOrigin.x:0},{chunkOrigin.z:0}) y {y:0.0} band {Mathf.FloorToInt(y / HeightBand)}: west border ice {iw / n / 255f:0.00} A {fw / n / 255f:0.00}, east border ice {ie / n / 255f:0.00} A {fe / n / 255f:0.00}; margin sampled from world columns (neighbours), frost tent-smoothed (0.2.1183)");
                }
                e.Tex.SetPixels32(s_px);
                e.Tex.Apply(false, false);
                e.Version = s_version; e.FrostV = s_frostVersion; e.Origin = chunkOrigin; e.Y = y;
                s_bound++;
                // 0.2.1321 (Fire fell under the ice at 02:33:19 exactly as "solid ice sub-cells none (first build)" tiles were
                // built: the texture drew solid ice here while the collider tile waited its turn in PlacedWaterSolids' 0.25 s queue):
                // a placed fill that shows solid ice the tile's collider has not got yet builds that collider NOW, this frame.
                if (g != null && e.Src == GridSrc.Placed) PlacedWaterSolids.BuildWithVisual(chunkOrigin.x, chunkOrigin.z, g);
            }
        }

        private static void Purge()
        {
            var dead = new List<int>();
            foreach (var kv in s_entries) if (kv.Value.R == null) dead.Add(kv.Key);
            foreach (int k in dead) { Object.Destroy(s_entries[k].Tex); s_entries.Remove(k); }
        }
    }
}
