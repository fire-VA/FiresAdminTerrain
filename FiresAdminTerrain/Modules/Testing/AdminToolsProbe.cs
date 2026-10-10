using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Testing
{
    // What admintools_test reads back, the same way on every peer: the tester, the test bot and the server each read
    // their OWN copy of the ground at the asked points and answer, so "the bot sees the same" is a comparison of numbers.
    // A value a peer cannot read (no tile there, no heightmap, no colliders on a dedicated server) is NaN, and NaN is
    // never compared.
    internal enum ProbeKind : byte
    {
        TileHeight,
        TilePaint,
        TileBiome,
        VoxelSurface,
        VanillaHeight,
        Cultivated,
        PaintMask,
        Biome,
        Standing,
        WaterSurface,
        WaterType,
        PourLevel,
        SpringFrozen,
        LandIce,
        FloatingIce,
        FloatingIceHoles,
        LandSnow,
        // Answered by the server only (it holds every ZDO and every location), for picking a test place anywhere:
        PiecesNear,     // build-piece ZDOs within PieceNearMetres of the point
        LocationNear,   // metres to the nearest placed location
        // 0.2.715: Vfall falls whose lip is within FallsNearMetres, as this peer holds them (the server from its zone import,
        // the bot from the chunk anchor), so a fall that did not reach a peer reads 0 there.
        FallsNear,
        // 0.2.733 (server only): the nearest placed location's prefab hash in two 16-bit halves (each exact as a float), so
        // the surface find can name the location that ruled a flat place out.
        LocationHashHigh,
        LocationHashLow,
    }

    internal struct ProbePoint
    {
        internal ProbeKind Kind;
        internal Vector3 At;

        internal ProbePoint(ProbeKind kind, Vector3 at)
        {
            Kind = kind;
            At = at;
        }
    }

    internal sealed class PeerProbe
    {
        internal long Sender;
        internal string Role;
        internal string Name;
        internal float[] Values;
        internal int Errors;
    }

    internal static class AdminToolsProbe
    {
        private const string RpcAsk = "FiresAdminTerrain_AdminToolsProbe";
        private const string RpcAnswer = "FiresAdminTerrain_AdminToolsProbeAnswer";
        private const int Format = 1;
        // A surface is searched this far above and below the asked point; a standing ray starts this far above it.
        private const float SurfaceRange = 12f;
        private const float RayLift = 6f;
        private const float RayLength = 24f;
        private const int ColourBase = 256;
        internal const float AnswerSeconds = 6f;
        // Every player answers, and a dedicated server as well; a host is one of the players, so once every player has
        // answered the wait for a separate server ends after this.
        private const float ListenHostGraceSeconds = 1.5f;

        private static readonly Dictionary<long, PeerProbe> s_answers = new Dictionary<long, PeerProbe>();
        private static ZRoutedRpc s_rpcOn;
        private static int s_ask;
        private static int s_errors;
        private static bool s_counting;
        private static int s_rayMask;

        // From FAT's one ZNet.Awake hook (FatVanillaHooks).
        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_rpcOn, rpc)) return;
            rpc.Register<ZPackage>(RpcAsk, OnAsk);
            rpc.Register<ZPackage>(RpcAnswer, OnAnswer);
            s_rpcOn = rpc;
            if (!s_counting)
            {
                Application.logMessageReceivedThreaded += CountErrors;
                s_counting = true;
            }
        }

        private static void CountErrors(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error) System.Threading.Interlocked.Increment(ref s_errors);
        }

        internal static int LocalErrors => s_errors;

        // Every peer's reading of the points, this one's included; waits AnswerSeconds for the others.
        internal static IEnumerator AskAll(List<ProbePoint> points, Action<List<PeerProbe>> done)
        {
            s_ask++;
            s_answers.Clear();
            var pkg = new ZPackage();
            pkg.Write(Format);
            pkg.Write(s_ask);
            pkg.Write(points.Count);
            foreach (var point in points)
            {
                pkg.Write((int)point.Kind);
                pkg.Write(point.At);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcAsk, pkg);
            int players = ZNet.instance != null ? ZNet.instance.GetPlayerList().Count : 1;
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < AnswerSeconds)
            {
                bool dedicatedToo = s_answers.Count >= players + 1;
                bool playersOnly = s_answers.Count >= players && Time.realtimeSinceStartup - started > ListenHostGraceSeconds;
                if (dedicatedToo || playersOnly) break;
                yield return null;
            }
            done(new List<PeerProbe>(s_answers.Values));
        }

        internal static float[] ReadLocal(List<ProbePoint> points)
        {
            var values = new float[points.Count];
            for (int i = 0; i < points.Count; i++) values[i] = Read(points[i]);
            return values;
        }

        private static void OnAsk(long sender, ZPackage pkg)
        {
            if (pkg.ReadInt() != Format) return;
            int ask = pkg.ReadInt();
            int count = pkg.ReadInt();
            var points = new List<ProbePoint>(count);
            for (int i = 0; i < count; i++) points.Add(new ProbePoint((ProbeKind)pkg.ReadInt(), pkg.ReadVector3()));
            float[] values = ReadLocal(points);
            var answer = new ZPackage();
            answer.Write(Format);
            answer.Write(ask);
            answer.Write(Role());
            answer.Write(Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "");
            answer.Write(s_errors);
            answer.Write(values.Length);
            foreach (float value in values) answer.Write(value);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAnswer, answer);
        }

        private static void OnAnswer(long sender, ZPackage pkg)
        {
            if (pkg.ReadInt() != Format || pkg.ReadInt() != s_ask) return;
            var probe = new PeerProbe { Sender = sender, Role = pkg.ReadString(), Name = pkg.ReadString(), Errors = pkg.ReadInt() };
            probe.Values = new float[pkg.ReadInt()];
            for (int i = 0; i < probe.Values.Length; i++) probe.Values[i] = pkg.ReadSingle();
            s_answers[sender] = probe;
        }

        // The arrival rule's radius (AdminToolsPlaces.PieceClearMetres).
        internal const float PieceNearMetres = 40f;
        internal const float FallsNearMetres = 4f;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // Player-placed ZDOs (they carry a creator) within PieceNearMetres (flat distance): the zone of the point and its eight
        // neighbours. The same rule as the arrival check (AdminToolsPlaces.CheckArrival): R76 counted trees and rocks, which
        // some mod gives a Piece component on the client, as "build pieces" and SKIPped a place this probe had passed.
        private static float PiecesNear(Vector3 at)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return float.NaN;
            var zone = ZoneSystem.GetZone(at);
            var found = new List<ZDO>();
            int count = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    found.Clear();
                    ZDOMan.instance.FindObjects(new Vector2s(zone.x + dx, zone.y + dz), found, new HashSet<ZoneSystem.SectorIndex>());
                    foreach (var zdo in found)
                    {
                        if (zdo == null) continue;
                        var p = zdo.GetPosition();
                        float fx = p.x - at.x, fz = p.z - at.z;
                        if (fx * fx + fz * fz > PieceNearMetres * PieceNearMetres) continue;
                        if (zdo.GetLong(ZDOVars.s_creator) != 0L) count++;
                    }
                }
            return count;
        }

        // Flat metres to the nearest placed location (a dungeon entrance, an altar, a ruin); 1e6 when there is none.
        private static float NearestLocation(Vector3 at)
        {
            if (ZoneSystem.instance == null) return float.NaN;
            return NearestLocation(at, out _);
        }

        private static float NearestLocation(Vector3 at, out ZoneSystem.ZoneLocation nearest)
        {
            nearest = null;
            float best = 1e6f;
            foreach (var location in ZoneSystem.instance.GetLocationList())
            {
                if (!location.m_placed) continue;
                float fx = location.m_position.x - at.x, fz = location.m_position.z - at.z;
                float d = Mathf.Sqrt(fx * fx + fz * fz);
                if (d < best) { best = d; nearest = location.m_location; }
            }
            return best;
        }

        // Half of the nearest placed location's prefab hash (ZoneLocation.Hash = its prefab name's stable hash); NaN when none.
        private static float NearestLocationHashHalf(Vector3 at, bool high)
        {
            if (ZoneSystem.instance == null) return float.NaN;
            NearestLocation(at, out var nearest);
            if (nearest == null || string.IsNullOrEmpty(nearest.m_prefabName)) return float.NaN;
            uint hash = unchecked((uint)nearest.m_prefabName.GetStableHashCode());
            return high ? hash >> 16 : hash & 0xFFFFu;
        }

        // The location prefab a hash from the two halves above names, as this peer's location list knows it.
        internal static string LocationName(float high, float low)
        {
            if (float.IsNaN(high) || float.IsNaN(low)) return "?";
            int hash = unchecked((int)(((uint)high << 16) | (uint)low));
            if (ZoneSystem.instance != null)
                foreach (var location in ZoneSystem.instance.m_locations)
                    if (!string.IsNullOrEmpty(location.m_prefabName) && location.m_prefabName.GetStableHashCode() == hash) return location.m_prefabName;
            return "#" + hash.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Role()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) return "server";
            return Environment.GetCommandLineArgs().Length > 0 && Array.IndexOf(Environment.GetCommandLineArgs(), "-firesbot") >= 0 ? "bot" : "client";
        }

        private static float Read(ProbePoint point)
        {
            try
            {
                var at = point.At;
                switch (point.Kind)
                {
                    case ProbeKind.PiecesNear:
                        return IsServer ? PiecesNear(at) : float.NaN;
                    case ProbeKind.LocationNear:
                        return IsServer ? NearestLocation(at) : float.NaN;
                    case ProbeKind.LocationHashHigh:
                    case ProbeKind.LocationHashLow:
                        return IsServer ? NearestLocationHashHalf(at, point.Kind == ProbeKind.LocationHashHigh) : float.NaN;
                    case ProbeKind.FallsNear:
                        return VerdantsAscent.Water.Tools.ToolFalls.CountNear(at, FallsNearMetres);
                    case ProbeKind.TileHeight:
                        return TileAnchorRegistry.TrySampleHeightAtWorld(at, out float tileY) ? tileY : float.NaN;
                    case ProbeKind.TilePaint:
                        return TileAnchorRegistry.TrySamplePaintAtWorld(at, out Color32 paint) ? PackColour(paint) : float.NaN;
                    case ProbeKind.TileBiome:
                        return TileAnchorRegistry.SampleBiomeAtWorld(at);
                    case ProbeKind.VoxelSurface:
                        return VoxelWorld.TryFindSurfaceY(at.x, at.z, at.y, SurfaceRange, out float surface) ? surface : float.NaN;
                    case ProbeKind.VanillaHeight:
                        return Heightmap.GetHeight(at, out float height) ? height : float.NaN;
                    case ProbeKind.Cultivated:
                        var cultivatedMap = Heightmap.FindHeightmap(at);
                        return cultivatedMap == null ? float.NaN : cultivatedMap.IsCultivated(at) ? 1f : 0f;
                    case ProbeKind.PaintMask:
                        var paintMap = Heightmap.FindHeightmap(at);
                        return paintMap == null ? float.NaN : PackColour(paintMap.GetPaintMask(at));
                    case ProbeKind.Biome:
                        return WorldGenerator.instance == null ? float.NaN : (float)(int)WorldGenerator.instance.GetBiome(at.x, at.z);
                    case ProbeKind.Standing:
                        return Standing(at);
                    case ProbeKind.WaterSurface:
                        float level = CustomWaterManager.GetWaterLevelAt(at);
                        return level > NoWater ? level : float.NaN;
                    case ProbeKind.WaterType:
                        return WaterTypeAt(at);
                    case ProbeKind.PourLevel:
                        return Floating.GetLiquidLevel(at, 1f, LiquidType.Water);
                    case ProbeKind.SpringFrozen:
                        return SpringFrozenNear(at);
                    case ProbeKind.LandIce:
                        var landIce = LandIceController.GetLandIceAt(at);
                        return landIce != null ? landIce.IceSurfaceY : float.NaN;
                    case ProbeKind.FloatingIce:
                        var floatingIce = FloatingIceAt(at);
                        return floatingIce != null ? floatingIce.IceSurfaceY : float.NaN;
                    case ProbeKind.FloatingIceHoles:
                        var holed = FloatingIceAt(at);
                        return holed != null ? holed.HoleCount : float.NaN;
                    case ProbeKind.LandSnow:
                        var snow = LandSnowController.GetLandSnowAt(at);
                        return snow != null ? snow.SnowSurfaceY : float.NaN;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[AdminToolsTest] probe {point.Kind} at {Where(point.At)} threw {ex.GetType().Name}: {ex.Message}");
            }
            return float.NaN;
        }

        internal static string Where(Vector3 point) => $"({point.x:0.0},{point.y:0.0},{point.z:0.0})";

        // The ground a player would stand on: the first collider below, as the character controller finds it.
        // What a body would stand on, on this peer's colliders. On a VoxelWrap dedicated server that is vanilla's heightmap
        // collider: it holds voxel DATA only and builds no voxel colliders (R77: server 132.81 over a 127.85 carve, 134.75 under
        // a 138.75 cube). LastStandingHit names the hit for the case line; the server writes its own to its log.
        internal static string LastStandingHit { get; private set; }

        private static float Standing(Vector3 at)
        {
            LastStandingHit = null;
            if (s_rayMask == 0) s_rayMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain", "piece");
            var from = at + Vector3.up * RayLift;
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, RayLength, s_rayMask)) return float.NaN;
            var go = hit.collider.gameObject;
            var root = hit.collider.attachedRigidbody != null ? hit.collider.attachedRigidbody.gameObject : go.transform.root.gameObject;
            LastStandingHit = $"{go.name} (layer {LayerMask.LayerToName(go.layer)}, under {root.name}) at {hit.point.y:0.00}";
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                FiresLogger.LogInfo($"[AdminToolsProbe] server Standing at ({at.x:0.0},{at.z:0.0}): {LastStandingHit}");
            return hit.point.y;
        }

        // FAT's water reads float.MinValue where there is none ([visual]'s WATER_TOOLS_REFERENCE.md).
        private const float NoWater = float.MinValue / 2f;
        private const float SpringReach = 15f;

        // The custom water's type at a point: the combiner whose surface covers it.
        private static float WaterTypeAt(Vector3 at)
        {
            foreach (var combiner in WaterMeshCombiner.AllCombiners)
                if (combiner != null && combiner.GetWaterSurfaceAt(at) > NoWater) return (int)combiner.CustomLiquidType;
            return float.NaN;
        }

        private static float SpringFrozenNear(Vector3 at)
        {
            float best = SpringReach * SpringReach;
            FiresLiquidRiverController nearest = null;
            foreach (var spring in Object.FindObjectsOfType<FiresLiquidRiverController>())
            {
                float d = (spring.transform.position - at).sqrMagnitude;
                if (d > best) continue;
                best = d;
                nearest = spring;
            }
            return nearest == null ? float.NaN : nearest.m_frozen ? 1f : 0f;
        }

        private static FloatingIceController FloatingIceAt(Vector3 at)
        {
            foreach (var ice in FloatingIceController.AllFloatingIce)
                if (ice != null && ice.ContainsPosition(at)) return ice;
            return null;
        }

        internal static float PackColour(Color32 colour) => colour.r + colour.g * ColourBase + colour.b * ColourBase * ColourBase;

        internal static float PackColour(Color colour) => PackColour((Color32)colour);

        internal static Color32 UnpackColour(float packed)
        {
            int value = (int)packed;
            return new Color32((byte)(value % ColourBase), (byte)(value / ColourBase % ColourBase), (byte)(value / (ColourBase * ColourBase) % ColourBase), 0);
        }
    }
}
