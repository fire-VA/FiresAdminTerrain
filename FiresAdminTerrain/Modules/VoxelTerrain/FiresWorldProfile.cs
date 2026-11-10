using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VerdantsAscent.Terrain.Voxel
{
    internal enum ProfileGenerator : byte { Vanilla = 0, Fires = 1, Image = 2 }

    internal enum ProfileTerrain : byte { Heightmap = 0, VoxelWrap = 1, VoxelOnly = 2 }

    // A world's own generation settings, decided when it is created or converted and kept beside its records as
    // FiresWorldGen\firesgen_<world>.profile. The server reads it before its generator initialises and sends it to
    // every client ahead of PeerInfo, so a world is built the same way everywhere, whatever each machine's config
    // says. A world without one keeps the older rules: the recorded yes/no answer, a baked cache, the Fires name prefix.
    internal sealed class FiresWorldProfile
    {
        internal const byte Version = 1;
        // Version 2 = version 1 plus one byte at the end: island water off. Written only for a world that switched it off,
        // so every other world keeps version 1 on the wire and older builds still read it.
        private const byte VersionIslandWater = 2;
        // Version 3 = version 2's layout with the island-water byte ALWAYS written, plus the world's size (four
        // floats: radius, edge, stretch, biome stretch). Written only for a world whose recorded size is not vanilla, so a
        // 10 km world keeps version 1 or 2 on the wire byte for byte. The writer picks the lowest version that carries what
        // the world needs; the reader takes 1..3.
        private const byte VersionWorldSize = 3;
        // Version 4 (the admin "Super flat" build world) = version 3's layout (size always written) plus one
        // byte: super flat. Written only for a super flat world; every other world keeps 1..3 on the wire byte for byte.
        private const byte VersionSuperFlat = 4;
        // 0.2.1102: version 5 = version 4 plus one byte: the skyland site rule (SkySites2). Written only for a super flat world made
        // on 0.2.1102+, so every existing world stays 1..4 on the wire byte for byte.
        private const byte VersionSkySites = 5;
        // 0.2.1137: version 6 = version 5 plus SkySites3 (the pond-aware / mini site rule) and the list of EXISTING-world islands
        // the server switched to it (islands with no generated zone when decided; FatLocationReplan). Written only when either is set.
        private const byte VersionSkySites3 = 6;
        internal const string CloneSourcePrefix = "clone of ";

        internal ProfileGenerator Generator;
        internal ProfileTerrain Terrain;
        internal bool ZoneMode;
        internal bool? SkyIslands, Hydrology, Caves;
        internal bool? SkyIslandWater;   // null/true = islands get their ponds, rivers and falls; false = born dry
        internal string ImageBase = "", BiomeImage = "", VahcBase = "";
        internal string Source = "created";
        internal DateTime CreatedUtc = DateTime.UtcNow;
        // The world's size (FatWorldSize): null = vanilla. Recorded once, from Expand World Size, when the world is
        // created or first initialised by a server with EWS making a non-vanilla world; never changed afterwards.
        internal float? WorldRadius, WorldEdge, WorldStretch, BiomeStretch;
        // An admin build / test / blueprint world. Flat Meadows ground at y = SuperFlatHeight, 25% of the vanilla
        // size, no locations, no vegetation or clutter, no spawning of any kind. Chosen once on the New World screen; never changes.
        internal bool SuperFlat;
        // 0.2.1102: new worlds only. True = every skyland gets a location site (a narrow island takes the palette's smallest site
        // with its ease-back clamped to the island's heart). Existing worlds keep the 0.2.1096 rule so built islands never change.
        internal bool SkySites2;
        // 0.2.1137: new worlds: every skyland uses the 1137 site rule (pond islands sited on dry ground, a mini site when nothing
        // else fits). Existing worlds: only the islands in SiteIslands3 (their Variety ids), decided once by the server.
        internal bool SkySites3;
        // 0.2.1327: the world this file belongs to (World.m_uid); null = written before 0.2.1327. Add-only key "worldid": an older
        // FAT's reader skips keys it does not know (Read's switch has no default).
        internal long? WorldUid;
        internal readonly HashSet<int> SiteIslands3 = new HashSet<int>();
        internal const float SuperFlatHeight = 50f;
        internal const float SuperFlatRadius = 2500f, SuperFlatEdge = 2625f;

        /// <summary>THE super flat profile - the New World toggle and the unattended -fat-new-superflat path both build it here.</summary>
        internal static FiresWorldProfile NewSuperFlat(string source) => NewSuperFlat(source, ProfileTerrain.Heightmap, false, false);

        // Super flat takes the terrain choice; sky islands + caves are optional toggles: Heightmap keeps the
        // stock generator (patched flat by FatSuperFlat); Voxel wrap / Full voxel use the Fires generator, whose field is flat on a super
        // flat world (FiresWorldGenBase's branch). Rivers and lakes stay off. Sky islands and caves only exist in voxel terrain.
        internal static FiresWorldProfile NewSuperFlat(string source, ProfileTerrain terrain, bool skyIslands, bool caves)
            => NewSuperFlat(source, terrain, skyIslands, caves, SuperFlatRadius, SuperFlatEdge);

        internal static FiresWorldProfile NewSuperFlat(string source, ProfileTerrain terrain, bool skyIslands, bool caves, float radius, float edge)
        {
            bool voxel = terrain != ProfileTerrain.Heightmap;
            var flat = new FiresWorldProfile
            {
                Generator = voxel ? ProfileGenerator.Fires : ProfileGenerator.Vanilla,
                Terrain = terrain,
                ZoneMode = false,
                SkyIslands = voxel && skyIslands,
                Hydrology = false,
                Caves = voxel && caves,
                SuperFlat = true,
                SkySites2 = true,   // 0.2.1102: a new world
                SkySites3 = true,   // 0.2.1137: a new world
                Source = source,
            };
            flat.RecordSize(new FatWorldSize.Size { Radius = radius, Edge = edge, Stretch = 1f, BiomeStretch = 1f });
            return flat;
        }

        // For unattended runs: `-fat-new-superflat <world>` on the SERVER. A world of that name with no profile
        // and no save data is created super flat - the same profile the New World toggle writes - instead of being inferred from config.
        internal const string NewSuperFlatArgument = "-fat-new-superflat";
        internal static bool LaunchAsksSuperFlat(string worldName)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, NewSuperFlatArgument);
            return i >= 0 && i + 1 < args.Length && string.Equals(args[i + 1], worldName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The current world is super flat (server: its file; client: the server's handshake profile).</summary>
        // The world being generated (set as its generator initialises, server and client).
        internal static string WorldName { get; private set; }

        internal static bool CurrentIsSuperFlat => s_current != null && s_current.SuperFlat;

        internal bool HasRecordedSize => WorldRadius.HasValue && WorldEdge.HasValue && WorldStretch.HasValue && BiomeStretch.HasValue;

        internal FatWorldSize.Size RecordedSize => new FatWorldSize.Size
        {
            Radius = WorldRadius ?? FatWorldSize.VanillaRadius,
            Edge = WorldEdge ?? FatWorldSize.VanillaEdge,
            Stretch = WorldStretch ?? 1f,
            BiomeStretch = BiomeStretch ?? 1f,
        };

        internal void RecordSize(FatWorldSize.Size size)
        {
            WorldRadius = size.Radius;
            WorldEdge = size.Edge;
            WorldStretch = size.Stretch;
            BiomeStretch = size.BiomeStretch;
        }

        private bool SizeIsNonVanilla => HasRecordedSize && !RecordedSize.IsVanilla;

        private static FiresWorldProfile s_current;
        private bool _fromFile;

        // Read from the world's own file and chosen for it (New World, a clone, a test's setup), not inferred from a
        // machine's config: such a profile says whether the world is one zone at a time, and the -fireszones launch flag
        // never overrules it (the launch flag once turned a whole-world test world into a zone-mode world).
        internal bool Chosen => _fromFile && Source != InferredSource && Source != FromRecordsSource;

        internal static FiresWorldProfile Current => s_current;

        // Every change of the current profile re-reads the world's size: a client's comes from the server's
        // handshake profile here, never from its own Expand World Size config.
        internal static void Set(FiresWorldProfile profile)
        {
            s_current = profile;
            FatWorldSize.Refresh();
        }

        internal static void Clear()
        {
            s_current = null;
            FatWorldSize.Refresh();
        }

        internal bool IsClone => Source != null && Source.StartsWith(CloneSourcePrefix, StringComparison.Ordinal);

        internal FiresWorldProfile Copy() => (FiresWorldProfile)MemberwiseClone();

        // Why a world could not be built this way, or null. The Fires generator runs only on voxel terrain. One zone at a
        // time runs on either generator: the Fires generator's own samplers ask the zone gate first (ZoneModeGate.
        // SamplerSeesClosed), so its far view, map, water and voxel ground keep closed zones as sea.
        //
        // VANILLA + FULL VOXEL IS LEGAL, and is how an existing world is converted: the vanilla heightmap sets the
        // ground and the voxel field is built from it, then the river carve, caves and sky islands run on that data.
        // Nothing needed adding for it - FiresWorldGenBase returns early for a Vanilla profile so the Fires height
        // generator never runs, while WrapsAllTerrain still covers every near zone and GeneratesCaves /
        // BuildsSkyIslands / CarvesRiverBeds gate on that coverage, not on the generator. Refusing the pair here was
        // the only thing standing in the way, and it forced a conversion to invent new terrain from the seed instead
        // of keeping the world's own shape.
        internal string Conflict()
        {
            if (Generator != ProfileGenerator.Vanilla && Terrain == ProfileTerrain.Heightmap)
                return "The Fires generator builds voxel terrain: choose voxel wrap or full voxel.";
            return null;
        }

        internal const string InferredSource = "inferred";
        internal const string FromRecordsSource = "from the world's own records";

        // What a world without a profile is built as, by the older rules and a config. A stock world configured as full
        // voxel builds no voxel at all (the streamer stands down without the Fires generator), so it reads as heightmap
        // with no voxel features: exactly what it has.
        //
        // `configMayChooseTheGenerator` is the whole difference between a world being created and a world that already
        // exists. For a NEW world the config is the only thing that can speak, and "make my worlds voxel" is exactly
        // what it is for. For an EXISTING world the land is already in the save, so only the world's own records may
        // name its generator - a config that says FullVoxel must never turn a stock world's ground into Fires ground
        // underneath the buildings already standing on it. Every other field still comes from the config, because the
        // machine reading it is the server: the profile is pinned and sent to every client, which is what makes the
        // server authoritative instead of each client's own settings.
        internal static FiresWorldProfile Infer(string worldName, bool configMayChooseTheGenerator)
        {
            bool fullVoxel = FiresVoxelConfig.LegacyConfigFullVoxel;   // 0.2.1336: the old cfg's value, pinned into this world's profile here, once
            var genMode = FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto;
            bool chosen = configMayChooseTheGenerator
                ? FiresWorldGenBase.UnprofiledChoosesFires(worldName, genMode, out _, out _, out _)
                : FiresWorldGenBase.RecordsChooseFires(worldName, out _, out _, out _);
            // The master switches stay a veto in both cases: with the generator switched off at this machine nothing
            // can turn it on, and Generator must fall back to Vanilla or the profile would conflict with itself.
            bool fires = fullVoxel && (FiresVoxelConfig.CustomGeneration?.Value ?? true) && genMode != FiresWorldGenMode.Off
                && chosen;
            bool voxelOnly = (FiresVoxelConfig.WorldMode?.Value ?? VoxelWorldMode.VoxelWrap) == VoxelWorldMode.VoxelOnly;
            bool noVoxel = fullVoxel && voxelOnly && !fires;
            string image = FiresVoxelConfig.WorldGenImageBase?.Value ?? "";
            return new FiresWorldProfile
            {
                Generator = !fires ? ProfileGenerator.Vanilla : image.Length > 0 ? ProfileGenerator.Image : ProfileGenerator.Fires,
                Terrain = !fullVoxel || noVoxel ? ProfileTerrain.Heightmap : voxelOnly ? ProfileTerrain.VoxelOnly : ProfileTerrain.VoxelWrap,
                SkyIslands = noVoxel ? false : (bool?)null,
                Hydrology = noVoxel ? false : (bool?)null,
                Caves = noVoxel ? false : (bool?)null,
                ImageBase = image,
                BiomeImage = FiresVoxelConfig.WorldGenBiomeImage?.Value ?? "",
                VahcBase = FiresVoxelConfig.WorldGenVahcBase?.Value ?? "",
                Source = InferredSource,
            };
        }

        /// <summary>A world that already has save data. Its generator comes from its own records and nothing else.</summary>
        internal static FiresWorldProfile FromWorldRecords(string worldName)
        {
            var profile = Infer(worldName, configMayChooseTheGenerator: false);
            profile.Source = FromRecordsSource;
            return profile;
        }

        // Server: the world's file, read as its generator initialises. Clients take theirs from the handshake.
        //
        // This ALWAYS ends with a profile, and that is the point. A null one is what let every machine answer from its
        // own config - the client's included, on a world it had only joined - so a world with no file of its own gets
        // one derived here and written beside it, and from then on the world speaks for itself everywhere.
        internal static void OnWorldInitialize(World world)
        {
            s_current = null;
            WorldName = world != null && !world.m_menu ? world.m_name : null;   // 0.2.1036: both peers know it here (FatHaloArc.RingOnly)
            FatWorldSize.Refresh();   // never the previous world's size; a client's arrives with the handshake (Set)
            if (world == null || world.m_menu) return;
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            bool neverSaved = Zones.ZoneModeRegistry.IsNeverSaved(world);
            s_current = Read(world.m_name);
            // 0.2.1327 (Fire 10-31, audit: a new world picked up a deleted same-name world's .profile): the profile names its world's
            // uid. A different uid = another world's leftover; no uid (written before 0.2.1327) is that world's own only once it has
            // been saved - beside a never-saved world it is exactly the leftover case. Either way the leftover is ignored and the
            // world is decided below as if it had none (that path writes the world's own profile over it).
            string ignored = null;
            if (s_current != null && s_current.WorldUid.HasValue && s_current.WorldUid.Value != world.m_uid)
            {
                FiresLogger.LogDebug($"[FiresWorldGen] '{world.m_name}'.profile names world uid {s_current.WorldUid.Value}, this world is "
                    + $"uid {world.m_uid}: a deleted same-name world's leftover, ignored.");
                s_current = null;
                ignored = "stale .profile ignored (uid mismatch)";
            }
            else if (s_current != null && !s_current.WorldUid.HasValue && neverSaved)
            {
                FiresLogger.LogDebug($"[FiresWorldGen] '{world.m_name}'.profile has no world uid and the world has never been saved: "
                    + "a deleted same-name world's leftover, ignored.");
                s_current = null;
                ignored = "stale .profile ignored (new world)";
            }
            if (s_current != null)
            {
                bool stamp = !s_current.WorldUid.HasValue;   // 0.2.1327: an older FAT's profile of a saved world is its own: stamp the uid in
                if (stamp)
                {
                    s_current.WorldUid = world.m_uid;
                    FiresLogger.LogDebug($"[FiresWorldGen] '{world.m_name}'.profile has no world uid but the world has save data: "
                        + $"accepted as its own, uid {world.m_uid} stamped in.");
                }
                // A profile written before FAT knew world sizes takes Expand World Size's (once, if EWS makes a
                // non-vanilla world); one that recorded a size keeps it and names a differing EWS config.
                // New World writes the profile before the first save, so "existing" means the world has save data.
                if (FatWorldSize.RecordFromExpandWorldSize(s_current, world.m_name, existingWorld: !neverSaved) | stamp)
                    s_current.Write(world.m_name);
                FatWorldSize.Refresh();
                LogMode(world.m_name, s_current, s_current.ZoneMode, stamp ? "old .profile stamped" : "own .profile (uid ok)");   // 0.2.1327
                return;
            }

            // 0.2.1030 HaloFlat ([lead]: zero setup): a new world named HaloFlat is created super flat, full voxel, sized past
            // the ring (FatHaloArc builds the ring key server-side on its first start).
            s_current = neverSaved && FatHaloArc.IsHaloFlatWorld(world.m_name)
                ? NewSuperFlat("created (HaloFlat: the full-size halo ring test world)", ProfileTerrain.VoxelOnly, false, false, FatHaloArc.HaloFlatWorldRadius, FatHaloArc.HaloFlatWorldEdge)
                : neverSaved && LaunchAsksSuperFlat(world.m_name) ? NewSuperFlat("created (" + NewSuperFlatArgument + ")")
                : neverSaved ? Infer(world.m_name, configMayChooseTheGenerator: true) : FromWorldRecords(world.m_name);
            s_current.WorldUid = world.m_uid;   // 0.2.1327
            FatWorldSize.RecordFromExpandWorldSize(s_current, world.m_name, existingWorld: !neverSaved);
            FatWorldSize.Refresh();
            FiresLogger.LogDebug(neverSaved
                ? $"[FiresWorldGen] world '{world.m_name}' has no profile and no save data, so it is being created: this "
                    + $"machine's config decides it, once - {s_current.Describe()}."
                : $"[FiresWorldGen] world '{world.m_name}' has no profile but DOES have save data, so its generator comes "
                    + $"from its own records and not from any config - {s_current.Describe()}.");
            if (s_current.Write(world.m_name))
                FiresLogger.LogDebug($"[FiresWorldGen] pinned that profile beside '{world.m_name}'. Every client is sent it, "
                    + "so the world is built the same way everywhere from now on.");
            // 0.2.1327: the -fireszones flag makes a never-saved, unchosen world one zone at a time (ZoneModeRegistry, later this load).
            bool flagZones = neverSaved && !FatHaloArc.IsHaloFlatWorld(world.m_name) && Zones.ZoneModeRegistry.LaunchFlagDecides(s_current);
            string source = flagZones ? "-fireszones flag" : neverSaved ? "config defaults" : "config defaults (generator from the world's own records)";
            LogMode(world.m_name, s_current, s_current.ZoneMode || flagZones, ignored != null ? ignored + ", then " + source : source);
        }

        // 0.2.1327 (Fire 10-31): THE one Info line per world load: what the world is and where that came from (server and client).
        internal static void LogMode(string worldName, FiresWorldProfile p, bool zoneMode, string source)
            => FiresLogger.LogInfo($"[FiresWorldGen] world '{worldName}' mode: generator {p.Generator}, terrain {p.Terrain}, zone mode "
                + $"{(zoneMode ? "on" : "off")}, super flat {(p.SuperFlat ? "on" : "off")} — source: {source}. ({p.Describe()})");

        // 0.2.1336: the world's choice: Heightmap = zones swap only on edit; VoxelWrap / VoxelOnly = zones swap automatically.
        internal FiresTerrainMode TerrainMode()
            => Terrain == ProfileTerrain.Heightmap ? FiresTerrainMode.ZoneReplace : FiresTerrainMode.FullVoxel;

        // Full voxel is built by the Fires generator (the streamer stands down without it), so a stock-generated world
        // saved as full voxel runs as voxel wrap instead: its voxel ground, skylands and border walls still build.
        internal VoxelWorldMode WorldKind(VoxelWorldMode configured)
        {
            switch (Terrain)
            {
                case ProfileTerrain.VoxelOnly: return Generator == ProfileGenerator.Vanilla ? VoxelWorldMode.VoxelWrap : VoxelWorldMode.VoxelOnly;
                case ProfileTerrain.VoxelWrap: return VoxelWorldMode.VoxelWrap;
                default: return configured == VoxelWorldMode.VoxelOnly ? VoxelWorldMode.VoxelWrap : configured;
            }
        }

        internal string Describe()
            => $"{Generator} generation, {Terrain} terrain" + (ZoneMode ? ", one zone at a time" : "")
               + Flag("sky islands", SkyIslands) + Flag("water on sky islands", SkyIslandWater) + Flag("rivers & lakes", Hydrology) + Flag("caves", Caves)
               + (Generator == ProfileGenerator.Image ? $", image '{ImageBase}'" : "")
               + (SizeIsNonVanilla ? $", world {RecordedSize}" : "") + (SuperFlat ? ", SUPER FLAT (admin build world)" : "") + $" ({Source})";

        private static string Flag(string name, bool? value) => value.HasValue ? $", {name} {(value.Value ? "on" : "off")}" : "";

        internal static string PathFor(string worldName)
        {
            return VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(worldName, ".profile");
        }

        internal static bool Exists(string worldName) => File.Exists(PathFor(worldName));

        internal bool Write(string worldName)
        {
            try
            {
                string path = PathFor(worldName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>
                {
                    $"version={Version}",
                    $"generator={Generator.ToString().ToLowerInvariant()}",
                    $"terrain={Terrain.ToString().ToLowerInvariant()}",
                    $"zonemode={(ZoneMode ? "true" : "false")}",
                    $"skyislands={Tri(SkyIslands)}",
                    $"skyislandwater={Tri(SkyIslandWater)}",
                    $"hydrology={Tri(Hydrology)}",
                    $"caves={Tri(Caves)}",
                    $"imagebase={ImageBase}",
                    $"biomeimage={BiomeImage}",
                    $"vahcbase={VahcBase}",
                    $"source={Source}",
                    $"created={CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}",
                };
                if (WorldUid.HasValue) lines.Add($"worldid={WorldUid.Value.ToString(CultureInfo.InvariantCulture)}");   // 0.2.1327
                if (SuperFlat) lines.Add("superflat=true");   // Only on a super flat world (every other file stays as it was)
                if (SkySites3) lines.Add("skysites=3");   // 0.2.1137: new worlds only (implies 2)
                else if (SkySites2) lines.Add("skysites=2");   // 0.2.1102: new worlds only
                if (SiteIslands3.Count > 0) lines.Add("skysites3islands=" + string.Join(",", SiteIslands3.OrderBy(v => v)));   // 0.2.1137
                // The world's size, only when it is not vanilla (a 10 km world's file stays as it was).
                if (SizeIsNonVanilla)
                {
                    lines.Add($"worldradius={WorldRadius.Value.ToString("R", CultureInfo.InvariantCulture)}");
                    lines.Add($"worldedge={WorldEdge.Value.ToString("R", CultureInfo.InvariantCulture)}");
                    lines.Add($"worldstretch={WorldStretch.Value.ToString("R", CultureInfo.InvariantCulture)}");
                    lines.Add($"biomestretch={BiomeStretch.Value.ToString("R", CultureInfo.InvariantCulture)}");
                }
                FiresCore.IO.AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] could not write the profile for '{worldName}': {ex.Message}");
                return false;
            }
        }

        internal static FiresWorldProfile Read(string worldName)
        {
            try
            {
                string path = PathFor(worldName);
                if (!File.Exists(path)) return null;
                var profile = new FiresWorldProfile { _fromFile = true };
                foreach (var raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = raw.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "generator": profile.Generator = ParseEnum(value, ProfileGenerator.Vanilla); break;
                        case "terrain": profile.Terrain = ParseEnum(value, ProfileTerrain.Heightmap); break;
                        case "zonemode": profile.ZoneMode = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "skyislands": profile.SkyIslands = ParseTri(value); break;
                        case "skyislandwater": profile.SkyIslandWater = ParseTri(value); break;
                        case "hydrology": profile.Hydrology = ParseTri(value); break;
                        case "caves": profile.Caves = ParseTri(value); break;
                        case "imagebase": profile.ImageBase = value; break;
                        case "biomeimage": profile.BiomeImage = value; break;
                        case "vahcbase": profile.VahcBase = value; break;
                        case "source": profile.Source = value; break;
                        case "worldid":   // 0.2.1327
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid)) profile.WorldUid = uid;
                            break;
                        case "worldradius": profile.WorldRadius = ParseSize(value); break;
                        case "worldedge": profile.WorldEdge = ParseSize(value); break;
                        case "worldstretch": profile.WorldStretch = ParseSize(value); break;
                        case "biomestretch": profile.BiomeStretch = ParseSize(value); break;
                        case "superflat": profile.SuperFlat = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "skysites": profile.SkySites2 = value.Trim() == "2" || value.Trim() == "3"; profile.SkySites3 = value.Trim() == "3"; break;
                        case "skysites3islands":
                            foreach (var id in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                                if (int.TryParse(id.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) profile.SiteIslands3.Add(v);
                            break;
                        case "created":
                            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                                profile.CreatedUtc = created;
                            break;
                    }
                }
                return profile;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] could not read the profile for '{worldName}': {ex.Message}; the older rules apply.");
                return null;
            }
        }

        private static string Tri(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "default";

        // A missing, unreadable or non-positive size reads as no size (vanilla), never as a guess.
        private static float? ParseSize(string value)
            => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && parsed > 0f && !float.IsInfinity(parsed)
                ? parsed : (float?)null;

        private static bool? ParseTri(string value)
        {
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
            => Enum.TryParse(value, true, out T parsed) ? parsed : fallback;

        // ── Handshake ───────────────────────────────────────────────────────
        internal void Write(ZPackage pkg)
        {
            bool dryIslands = SkyIslandWater == false;
            bool sized = SizeIsNonVanilla;
            byte version = SuperFlat && HasRecordedSize && (SkySites3 || SiteIslands3.Count > 0) ? VersionSkySites3 : SuperFlat && HasRecordedSize && SkySites2 ? VersionSkySites : SuperFlat && HasRecordedSize ? VersionSuperFlat : sized ? VersionWorldSize : dryIslands ? VersionIslandWater : Version;
            pkg.Write(version);
            pkg.Write((byte)Generator);
            pkg.Write((byte)Terrain);
            pkg.Write(ZoneMode);
            pkg.Write(TriByte(SkyIslands));
            pkg.Write(TriByte(Hydrology));
            pkg.Write(TriByte(Caves));
            pkg.Write(ImageBase ?? "");
            pkg.Write(BiomeImage ?? "");
            pkg.Write(VahcBase ?? "");
            pkg.Write(Source ?? "");
            if (version >= VersionIslandWater) pkg.Write(TriByte(SkyIslandWater));
            if (version >= VersionWorldSize)
            {
                pkg.Write(WorldRadius.Value);
                pkg.Write(WorldEdge.Value);
                pkg.Write(WorldStretch.Value);
                pkg.Write(BiomeStretch.Value);
            }
            if (version >= VersionSuperFlat) pkg.Write(SuperFlat);
            if (version >= VersionSkySites) pkg.Write(SkySites2);
            if (version >= VersionSkySites3)
            {
                pkg.Write(SkySites3);
                pkg.Write(SiteIslands3.Count);
                foreach (int v in SiteIslands3.OrderBy(v => v)) pkg.Write(v);
            }
        }

        internal static FiresWorldProfile Read(ZPackage pkg)
        {
            byte version = pkg.ReadByte();
            if (version < Version || version > VersionSkySites3)
                throw new InvalidDataException($"world profile version {version}, this build reads {Version}-{VersionSkySites3}");
            var profile = new FiresWorldProfile
            {
                Generator = (ProfileGenerator)pkg.ReadByte(),
                Terrain = (ProfileTerrain)pkg.ReadByte(),
                ZoneMode = pkg.ReadBool(),
                SkyIslands = TriFrom(pkg.ReadByte()),
                Hydrology = TriFrom(pkg.ReadByte()),
                Caves = TriFrom(pkg.ReadByte()),
                ImageBase = pkg.ReadString(),
                BiomeImage = pkg.ReadString(),
                VahcBase = pkg.ReadString(),
                Source = pkg.ReadString(),
            };
            if (version >= VersionIslandWater) profile.SkyIslandWater = TriFrom(pkg.ReadByte());
            if (version >= VersionWorldSize)
            {
                profile.WorldRadius = pkg.ReadSingle();
                profile.WorldEdge = pkg.ReadSingle();
                profile.WorldStretch = pkg.ReadSingle();
                profile.BiomeStretch = pkg.ReadSingle();
            }
            if (version >= VersionSuperFlat) profile.SuperFlat = pkg.ReadBool();
            if (version >= VersionSkySites) profile.SkySites2 = pkg.ReadBool();
            if (version >= VersionSkySites3)
            {
                profile.SkySites3 = pkg.ReadBool();
                int n = pkg.ReadInt();
                for (int i = 0; i < n; i++) profile.SiteIslands3.Add(pkg.ReadInt());
            }
            return profile;
        }

        private static byte TriByte(bool? value) => value.HasValue ? (value.Value ? (byte)1 : (byte)2) : (byte)0;

        private static bool? TriFrom(byte value) => value == 1 ? true : value == 2 ? (bool?)false : null;
    }
}
