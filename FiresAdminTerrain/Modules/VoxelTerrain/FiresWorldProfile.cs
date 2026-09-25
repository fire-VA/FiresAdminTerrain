using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
        internal const string CloneSourcePrefix = "clone of ";

        internal ProfileGenerator Generator;
        internal ProfileTerrain Terrain;
        internal bool ZoneMode;
        internal bool? SkyIslands, Hydrology, Caves;
        internal string ImageBase = "", BiomeImage = "", VahcBase = "";
        internal string Source = "created";
        internal DateTime CreatedUtc = DateTime.UtcNow;

        private static FiresWorldProfile s_current;

        internal static FiresWorldProfile Current => s_current;

        internal static void Set(FiresWorldProfile profile) => s_current = profile;

        internal static void Clear() => s_current = null;

        internal bool IsClone => Source != null && Source.StartsWith(CloneSourcePrefix, StringComparison.Ordinal);

        internal FiresWorldProfile Copy() => (FiresWorldProfile)MemberwiseClone();

        // Why a world could not be built this way, or null. The Fires generator runs only on voxel terrain, and one zone at
        // a time (its skylands and border rivers) only on stock generation.
        internal string Conflict()
        {
            if (Generator != ProfileGenerator.Vanilla && Terrain == ProfileTerrain.Heightmap)
                return "The Fires generator builds voxel terrain: choose voxel wrap or full voxel.";
            if (Generator == ProfileGenerator.Vanilla && Terrain == ProfileTerrain.VoxelOnly)
                return "Full voxel is built by the Fires generator: with stock Valheim, choose voxel wrap.";
            if (ZoneMode && Generator != ProfileGenerator.Vanilla)
                return "One zone at a time runs on stock Valheim terrain for now, not on the Fires generator.";
            return null;
        }

        // What a world without a profile is built as today, by the older rules and this installation's config. A stock world
        // configured as full voxel builds no voxel at all (the streamer stands down without the Fires generator), so it
        // reads as heightmap with no voxel features: exactly what it has.
        internal static FiresWorldProfile Infer(string worldName)
        {
            var terrainMode = FiresVoxelConfig.TerrainMode?.Value ?? FiresTerrainMode.Off;
            bool fullVoxel = terrainMode == FiresTerrainMode.FullVoxel;
            var genMode = FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto;
            bool fires = fullVoxel && (FiresVoxelConfig.CustomGeneration?.Value ?? true) && genMode != FiresWorldGenMode.Off
                && FiresWorldGenBase.UnprofiledChoosesFires(worldName, genMode, out _, out _, out _);
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
                Source = "inferred",
            };
        }

        // Server: the world's file, read as its generator initialises. Clients take theirs from the handshake.
        internal static void OnWorldInitialize(World world)
        {
            s_current = null;
            if (world == null || world.m_menu) return;
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            s_current = Read(world.m_name);
            if (s_current != null) FiresLogger.LogInfo($"[FiresWorldGen] world '{world.m_name}' profile: {s_current.Describe()}.");
        }

        // The terrain mode this world runs under, given the configured one.
        internal FiresTerrainMode TerrainMode(FiresTerrainMode configured)
        {
            switch (Terrain)
            {
                case ProfileTerrain.VoxelWrap:
                case ProfileTerrain.VoxelOnly:
                    return FiresTerrainMode.FullVoxel;
                default:
                    return configured == FiresTerrainMode.FullVoxel ? FiresTerrainMode.ZoneReplace : configured;
            }
        }

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
               + Flag("sky islands", SkyIslands) + Flag("rivers & lakes", Hydrology) + Flag("caves", Caves)
               + (Generator == ProfileGenerator.Image ? $", image '{ImageBase}'" : "") + $" ({Source})";

        private static string Flag(string name, bool? value) => value.HasValue ? $", {name} {(value.Value ? "on" : "off")}" : "";

        internal static string PathFor(string worldName)
        {
            string safe = string.Concat((worldName ?? "world").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresWorldGen", "firesgen_" + safe + ".profile");
        }

        internal static bool Exists(string worldName) => File.Exists(PathFor(worldName));

        internal bool Write(string worldName)
        {
            try
            {
                string path = PathFor(worldName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new List<string>
                {
                    $"version={Version}",
                    $"generator={Generator.ToString().ToLowerInvariant()}",
                    $"terrain={Terrain.ToString().ToLowerInvariant()}",
                    $"zonemode={(ZoneMode ? "true" : "false")}",
                    $"skyislands={Tri(SkyIslands)}",
                    $"hydrology={Tri(Hydrology)}",
                    $"caves={Tri(Caves)}",
                    $"imagebase={ImageBase}",
                    $"biomeimage={BiomeImage}",
                    $"vahcbase={VahcBase}",
                    $"source={Source}",
                    $"created={CreatedUtc.ToString("O", CultureInfo.InvariantCulture)}",
                });
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
                var profile = new FiresWorldProfile();
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
                        case "hydrology": profile.Hydrology = ParseTri(value); break;
                        case "caves": profile.Caves = ParseTri(value); break;
                        case "imagebase": profile.ImageBase = value; break;
                        case "biomeimage": profile.BiomeImage = value; break;
                        case "vahcbase": profile.VahcBase = value; break;
                        case "source": profile.Source = value; break;
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
            pkg.Write(Version);
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
        }

        internal static FiresWorldProfile Read(ZPackage pkg)
        {
            byte version = pkg.ReadByte();
            if (version != Version) throw new InvalidDataException($"world profile version {version}, this build reads {Version}");
            return new FiresWorldProfile
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
        }

        private static byte TriByte(bool? value) => value.HasValue ? (value.Value ? (byte)1 : (byte)2) : (byte)0;

        private static bool? TriFrom(byte value) => value == 1 ? true : value == 2 ? (bool?)false : null;
    }
}
