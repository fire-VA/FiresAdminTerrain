using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // The ONE biome override authority. A point's biome is, in order: the newest Chaos Hammer paint circle over it (BiomePaint;
    // Restore leaves the world's answer), then any registered override provider (FatApi.AddBiomeOverride, e.g. RPGM's
    // ForceBiome territories), else the world's own answer. WorldGenerator.GetBiome (float and Vector3) answers through it, as
    // GetBiomeSector already does for paint, so everything vanilla keys off those lookups follows the painted biome: the
    // environment and its weather (freezing, snow and ash particles), music, spawns, vegetation, locations and clutter.
    // Never inside a heightmap's corner scope (BiomePaint.Ground keeps each zone's own ground shape), a zone-mode source lookup,
    // or before the world's zone system exists (world generation must read the world's own biomes).
    internal static class BiomeOverride
    {
        private static Func<float, float, int>[] s_providers = new Func<float, float, int>[0];
        private static readonly object s_providerLock = new object();
        [ThreadStatic] private static int t_inProvider;
        private static int s_providerFaults;

        // Sector providers answer a whole BiomeSector (a biome with its alt-biome blend, e.g. a territory forced to "Plains - Heath");
        // they are asked before the biome-only providers, and their sector's biome is the point's biome.
        private static Func<float, float, BiomeSector>[] s_sectorProviders = new Func<float, float, BiomeSector>[0];

        internal static int ProviderCount => s_providers.Length + s_sectorProviders.Length;

        internal static void AddSectorProvider(Func<float, float, BiomeSector> provider)
        {
            lock (s_providerLock)
            {
                var a = new Func<float, float, BiomeSector>[s_sectorProviders.Length + 1];
                Array.Copy(s_sectorProviders, a, s_sectorProviders.Length);
                a[a.Length - 1] = provider;
                s_sectorProviders = a;
            }
        }

        internal static bool RemoveSectorProvider(Func<float, float, BiomeSector> provider)
        {
            lock (s_providerLock)
            {
                int i = Array.IndexOf(s_sectorProviders, provider);
                if (i < 0) return false;
                var a = new Func<float, float, BiomeSector>[s_sectorProviders.Length - 1];
                Array.Copy(s_sectorProviders, 0, a, 0, i);
                Array.Copy(s_sectorProviders, i + 1, a, i, a.Length - i);
                s_sectorProviders = a;
                return true;
            }
        }

        // The sector a provider forces at (x, z): a sector provider's own, else a biome-only provider's plain biome sector (null).
        internal static bool TryProviderSector(float x, float z, out BiomeSector sector, out Heightmap.Biome biome)
        {
            sector = null;
            biome = Heightmap.Biome.None;
            var sectorProviders = s_sectorProviders;
            if (sectorProviders.Length == 0 || t_inProvider > 0) return TryBiomeProviders(x, z, out biome);
            t_inProvider++;
            try
            {
                foreach (var provider in sectorProviders)
                {
                    BiomeSector answer;
                    try { answer = provider(x, z); }
                    catch (Exception ex) { NoteFault(ex); continue; }
                    if (answer != null && answer.Biome != Heightmap.Biome.None) { sector = answer; biome = answer.Biome; return true; }
                }
            }
            finally { t_inProvider--; }
            return TryBiomeProviders(x, z, out biome);
        }

        private static void NoteFault(Exception ex)
        {
            if (System.Threading.Interlocked.Increment(ref s_providerFaults) == 1)
                FiresLogger.LogWarning($"[BiomeOverride] an override provider threw, skipped (logged once): {ex.Message}");
        }

        internal static void AddProvider(Func<float, float, int> provider)
        {
            lock (s_providerLock)
            {
                var a = new Func<float, float, int>[s_providers.Length + 1];
                Array.Copy(s_providers, a, s_providers.Length);
                a[a.Length - 1] = provider;
                s_providers = a;
            }
        }

        internal static bool RemoveProvider(Func<float, float, int> provider)
        {
            lock (s_providerLock)
            {
                int i = Array.IndexOf(s_providers, provider);
                if (i < 0) return false;
                var a = new Func<float, float, int>[s_providers.Length - 1];
                Array.Copy(s_providers, 0, a, 0, i);
                Array.Copy(s_providers, i + 1, a, i, a.Length - i);
                s_providers = a;
                return true;
            }
        }

        // The overriding biome at (x, z), or false when nothing overrides it there.
        internal static bool TryAt(float x, float z, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (BiomePaint.TryPaintedGround(x, z, out var painted)) { biome = painted; return true; }
            return TryProviderSector(x, z, out _, out biome);
        }

        private static bool TryBiomeProviders(float x, float z, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            var providers = s_providers;
            if (providers.Length == 0 || t_inProvider > 0) return false;
            t_inProvider++;
            try
            {
                foreach (var provider in providers)
                {
                    int answer;
                    try { answer = provider(x, z); }
                    catch (Exception ex) { NoteFault(ex); continue; }
                    if (answer > 0) { biome = (Heightmap.Biome)answer; return true; }
                }
                return false;
            }
            finally { t_inProvider--; }
        }

        // 0.2.1024 (Fire 10-24, "Ocean" on the HUD over super flat land in a territory): the Ocean guard keeps real sea out of the
        // overrides, but on a super flat world's LAND the earlier answer is the seed's real terrain under the flat ground, which
        // can be sea. There the override (a territory's ForceBiome) must still apply.
        private static bool SuperFlatLand(float x, float z) => VerdantsAscent.Terrain.Voxel.FatSuperFlat.On && VerdantsAscent.Terrain.Voxel.FatSuperFlat.IsLand(x, z);

        private static bool Applies() => ZoneSystem.instance != null && BiomePaint.OverrideLookupAllowed;

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        private static class WorldGenerator_GetBiome_Override
        {
            // After the generators and providers' own patches, before super flat's Priority.Last (which stands aside in a content area).
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(float wx, float wy, ref Heightmap.Biome __result)
            {
                if ((__result == Heightmap.Biome.Ocean && !SuperFlatLand(wx, wy)) || !Applies()) return;
                if (TryAt(wx, wy, out var biome)) __result = biome;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class WorldGenerator_GetBiomePoint_Override
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector3 __0, ref Heightmap.Biome __result)
            {
                try
                {
                    if ((__result == Heightmap.Biome.Ocean && !SuperFlatLand(__0.x, __0.z)) || !Applies()) return;
                    if (TryAt(__0.x, __0.z, out var biome)) __result = biome;
                
                }
                catch (System.Exception e) { VerdantsAscent.Terrain.Zones.FatBiomeResolver.NoteThrow("BiomeOverride GetBiomePoint", __0, e); throw; }   // 0.2.1201
            }
        }

        // ── ONE answer: the sector follows GetBiome ─────────────────────────────────────────────────────────────────────────
        // Fire's log 10-23: 26x vanilla "GetBiome error Swamp -> Ocean" at (1975,56,705): Player.UpdateBiome asks
        // GetBiomeSector(position) and GetBiome(position) every second and they disagreed. The two lookups run through
        // different patch chains (zone-mode gate, Fires generator, paint, layer / cave biome, super flat, override providers,
        // several at Priority.Last with no order between them), so they could answer differently at the same point.
        // GetBiome is THE answer (this class: paint, then providers, then the world; super flat and the layer patches on
        // top), and as the very last step of GetBiomeSector(position) - the overload the player, the HUD and the environment read, and the one
        // vanilla compares - the sector is made to carry that biome: a sector
        // whose biome differs is replaced by the override provider's own sector for that biome, else a plain sector of it.
        // A sector that already agrees is kept as it is (its alt-biome blend stays). Not inside the world's own lookups
        // (BiomePaint.OverrideLookupAllowed: resolving, a heightmap's corner scope, a zone-mode source lookup) or before the
        // zone system exists. One Debug line (at most every 60 s) names a disagreement it settled.
        [ThreadStatic] private static int t_agreeing;
        private static readonly Dictionary<Heightmap.Biome, BiomeSector> s_agreeSectors = new Dictionary<Heightmap.Biome, BiomeSector>();
        private static float s_nextAgreeNote;
        private static int s_agreed;

        private static void Agree(float x, float z, Vector3? point, ref BiomeSector result)
        {
            if (result == null || t_agreeing > 0 || !Applies()) return;
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            t_agreeing++;
            try
            {
                var biome = point.HasValue ? wg.GetBiome(point.Value) : wg.GetBiome(x, z);
                if (biome == result.Biome || biome == Heightmap.Biome.None) return;
                var was = result.Biome;
                if (TryProviderSector(x, z, out var provided, out var pb) && provided != null && pb == biome) result = provided;
                else
                    lock (s_agreeSectors)
                    {
                        if (!s_agreeSectors.TryGetValue(biome, out var plain)) s_agreeSectors[biome] = plain = new BiomeSector(null, biome);
                        result = plain;
                    }
                s_agreed++;
                if (FiresLogger.DebugEnabled && System.Threading.Thread.CurrentThread == s_mainThread && Time.time >= s_nextAgreeNote)
                {
                    s_nextAgreeNote = Time.time + 60f;
                    FiresLogger.LogDebug($"[BiomeOverride] sector at ({x:0},{z:0}) said {was}, GetBiome says {biome}: the sector now carries {biome} ({s_agreed} settled so far).");
                }
            }
            finally { t_agreeing--; }
        }

        private static System.Threading.Thread s_mainThread;

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class NoteMainThread
        {
            private static void Postfix() => s_mainThread = System.Threading.Thread.CurrentThread;
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
        private static class WorldGenerator_GetBiomeSector_PointAgree
        {
            [HarmonyPriority(-100)]
            private static void Postfix(Vector3 __0, ref BiomeSector __result) => Agree(__0.x, __0.z, __0, ref __result);
        }

        // ── Piece snow ──────────────────────────────────────────────────────
        // Vanilla's piece shader has no snow. A piece standing in a snowy biome (Mountain, DeepNorth: real or overridden) carries
        // _FiresSnowBiome = 1 on its renderers; a piece shader that honours it (TossinShade's FiresPiece) draws snow there at any
        // height. Set when the piece wakes (built or loaded) and again for pieces inside a finished paint stroke.
        internal const string SnowBiomeProperty = "_FiresSnowBiome";
        private static readonly int s_snowBiomeId = Shader.PropertyToID(SnowBiomeProperty);
        private const Heightmap.Biome SnowyBiomes = Heightmap.Biome.Mountain | Heightmap.Biome.DeepNorth;
        // 0.2.1206 ([visual], Norger kit S0: a static initializer here ran on the game's HeightmapBuilder thread -> 'CreateImpl can only be called
        // from the main thread' -> TypeInitializationException, the type dead for the session): created on first (main-thread) use instead.
        private static MaterialPropertyBlock s_block_;
        private static MaterialPropertyBlock s_block { get { if (s_block_ == null) { s_block_ = new MaterialPropertyBlock(); FatMainThreadInit.Note("BiomeOverride"); } return s_block_; } }
        private static readonly List<Renderer> s_renderers = new List<Renderer>();

        internal static bool IsSnowyAt(Vector3 at)
            => WorldGenerator.instance != null && (WorldGenerator.instance.GetBiome(at) & SnowyBiomes) != Heightmap.Biome.None;

        private static bool s_pieceSnowStoodDown;

        internal static void ApplyPieceSnow(Piece piece)
        {
            if (piece == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            // 0.2.1027 ([seasons] via [ghost]): with Core's Dynamic snow on, FTS's driver owns _FiresSnowBiome on pieces
            // (CoverageAt per piece); writing it here too would fight it. Off or no SnowField: today's biome flag.
            if (VerdantsAscent.Terrain.Voxel.VoxelSnowDepth.SnowFieldEnabled)
            {
                if (!s_pieceSnowStoodDown) { s_pieceSnowStoodDown = true; FiresLogger.LogDebug("[BiomeOverride] piece snow: Dynamic snow is on - FTS owns _FiresSnowBiome, FAT stands down"); }
                return;
            }
            float snow = IsSnowyAt(piece.transform.position) ? 1f : 0f;
            s_renderers.Clear();
            piece.GetComponentsInChildren(true, s_renderers);
            foreach (var r in s_renderers)
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                r.GetPropertyBlock(s_block);
                s_block.SetFloat(s_snowBiomeId, snow);
                r.SetPropertyBlock(s_block);
            }
            s_block.Clear();
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        private static class Piece_Awake_Snow
        {
            private static void Postfix(Piece __instance)
            {
                try { ApplyPieceSnow(__instance); }
                catch (Exception ex) { FiresLogger.LogDebug($"[BiomeOverride] piece snow skipped on {__instance?.name}: {ex.Message}"); }
            }
        }

        // Every loaded piece, after the paint list changed (each peer installs the list it receives).
        internal static void RefreshAllPieceSnow()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            foreach (var piece in Piece.s_allPieces) if (piece != null) ApplyPieceSnow(piece);
        }

        // Pieces round a finished paint stroke follow its new ground.
        internal static int RefreshPieceSnow(Vector3 centre, float radius)
        {
            int n = 0;
            foreach (var piece in Piece.s_allPieces)
            {
                if (piece == null) continue;
                var p = piece.transform.position;
                float dx = p.x - centre.x, dz = p.z - centre.z;
                if (dx * dx + dz * dz > radius * radius) continue;
                ApplyPieceSnow(piece);
                n++;
            }
            return n;
        }

        // ── fat_biome_paths: what each biome-keyed path answers where you stand (one Debug line per path) ──
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Command
        {
            private static bool s_registered;

            [HarmonyPostfix]
            private static void Register()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("fat_biome_paths",
                    "[FiresAdminTerrain] logs what every biome-keyed path answers where you stand (world, override, sector, heightmap, environment, freezing, music, piece snow)",
                    args => { foreach (var line in Report()) { FiresLogger.LogDebug(line); args.Context?.AddString(line); } });
            }
        }

        internal static List<string> Report()
        {
            var lines = new List<string>();
            var player = Player.m_localPlayer;
            var wg = WorldGenerator.instance;
            if (player == null || wg == null) { lines.Add("[BiomePaths] no player / world"); return lines; }
            var at = player.transform.position;
            string P(string what, object value) => $"[BiomePaths] {what}: {value}";
            lines.Add(P("at", $"{at.x:0},{at.z:0} (y {at.y:0.0})"));
            lines.Add(P("override", TryAt(at.x, at.z, out var ov) ? ov.ToString() : "none"));
            lines.Add(P("WorldGenerator.GetBiome(x,z)", wg.GetBiome(at.x, at.z)));
            lines.Add(P("WorldGenerator.GetBiome(point)", wg.GetBiome(at)));
            var sector = wg.GetBiomeSector(at);
            lines.Add(P("WorldGenerator.GetBiomeSector", sector != null ? sector.Biome.ToString() : "none"));
            var hmap = Heightmap.FindHeightmap(at);
            lines.Add(P("Heightmap.GetBiome(point)", hmap != null ? hmap.GetBiome(at).ToString() : "no heightmap"));
            lines.Add(P("Heightmap.FindBiome", Heightmap.FindBiome(at)));
            lines.Add(P("Player.GetCurrentBiome", player.GetCurrentBiome()));
            var env = EnvMan.instance;
            if (env != null)
            {
                var current = env.GetCurrentEnvironment();
                lines.Add(P("EnvMan.GetBiome", env.GetBiome()));
                lines.Add(P("environment", current != null ? current.m_name : "none"));
                lines.Add(P("freezing / cold / wet", $"{EnvMan.IsFreezing()} / {EnvMan.IsCold()} / {EnvMan.IsWet()}"));
            }
            lines.Add(P("music", MusicMan.instance != null ? (MusicMan.instance.m_currentMusic?.m_name ?? "none") : "no music manager"));
            lines.Add(P("piece snow here", IsSnowyAt(at) ? "1 (snowy biome)" : "0"));
            lines.Add(P("providers", $"{ProviderCount} registered; paint circles {(BiomePaint.Any ? "yes" : "none")}"));
            return lines;
        }
    }
}

namespace VerdantsAscent
{
    // 0.2.1206: the why-line for FAT's lazily created MaterialPropertyBlocks (once per owner): which thread made it. A block must be
    // made on Unity's main thread; one made elsewhere would throw here instead of killing the owner type for the session.
    internal static class FatMainThreadInit
    {
        private static readonly System.Collections.Generic.HashSet<string> s_said = new System.Collections.Generic.HashSet<string>();
        internal static void Note(string owner)
        {
            lock (s_said) { if (!s_said.Add(owner)) return; }
            int t = System.Threading.Thread.CurrentThread.ManagedThreadId;
            FiresLogger.LogDebug($"[FiresAdminTerrain] {owner}: MaterialPropertyBlock created on first use, thread {t} (lazy since 0.2.1206: a static initializer had run on the game's HeightmapBuilder thread and thrown 'CreateImpl can only be called from the main thread')");
        }
    }
}
