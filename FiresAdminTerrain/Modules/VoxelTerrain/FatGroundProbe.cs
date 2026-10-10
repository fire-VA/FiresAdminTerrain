using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── What does the SERVER stand on here? (0.2.689, [lead] + [fgn] 10-01, RIDEg) ──────────────────────────────────────
    // ride_test's Lox was spawned in a carved pit and walked out while the server owned it. The VoxelWrap dedi builds
    // headless voxel colliders ("Server builds voxel collision" = true), so the question is which of three it was: the
    // runtime carve not yet in the server's collider, a climbable smoothed wall, or vanilla's heightmap collider still on.
    // This asks the server, from any client, and prints its answer:
    //   console  va_ground_probe <x> <z> [y]        (admin)   → one line, "[FiresGroundProbe] server ground at …"
    //   code     FatGroundProbe.Request(x, z, y, Action<string> reply)   (public, stable; [fgn] calls it from ride_test)
    //   RPC      "FiresAdminTerrain_GroundProbe" (client → server): ZPackage { int id, float x, float z, float y }
    //            "FiresAdminTerrain_GroundProbeReply" (server → asker): ZPackage { int id, string line }
    // The line, fields separated by " | ":
    //   ray: the top 4 hits of a down ray from y + RayAbove on the character ground layers, each "y Y on 'name' (layer,
    //        Collider type, voxel|heightmap|other)"; voxel: VoxelWorld density surface y in [y + RayAbove, y − RayBelow];
    //   heightmap: vanilla's height there and whether its MeshCollider is enabled; edits: the VAVoxel anchor of the
    //   chunk at the voxel surface, its latest ZDO revision and the one this server applied (equal = the carve is in).
    // Read-only. Without a y the server's heightmap height + RayAbove is the top of the ray.
    public static class FatGroundProbe
    {
        public const string RpcName = "FiresAdminTerrain_GroundProbe", ReplyRpcName = "FiresAdminTerrain_GroundProbeReply";
        private const string Tag = "[FiresGroundProbe]", Command = "va_ground_probe";
        private const float RayAbove = 12f, RayBelow = 60f;
        private const int ListHits = 4;
        private static readonly string[] GroundLayers = { "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle" };
        private static int s_mask;
        private static ZRoutedRpc s_registeredOn;
        private static int s_nextId = 1;
        private static readonly Dictionary<int, Action<string>> s_pending = new Dictionary<int, Action<string>>();
        private static bool s_commandRegistered;

        // Called with the water RPCs (ZNet start and player spawn); safe to repeat, re-registers on a new ZRoutedRpc.
        internal static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredOn)) return;
            rpc.Register<ZPackage>(RpcName, OnRequest);
            rpc.Register<ZPackage>(ReplyRpcName, OnReply);
            s_registeredOn = rpc;
            s_pending.Clear();
        }

        internal static void RegisterCommand()
        {
            if (s_commandRegistered) return;
            s_commandRegistered = true;
            new Terminal.ConsoleCommand(Command,
                "<x> <z> [y] - ask the SERVER what it stands on there: its ray hits (voxel / heightmap colliders), the voxel "
                + "density surface, vanilla's heightmap and whether its collider is on, and whether the chunk's carve is applied (admin, read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Length < 3 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args[2], NumberStyles.Float, inv, out float z))
                    { args.Context.AddString($"{Tag} usage: {Command} <x> <z> [y]"); return; }
                    float y = args.Length > 3 && float.TryParse(args[3], NumberStyles.Float, inv, out float yy) ? yy : float.NaN;
                    var ctx = args.Context;
                    Request(x, z, y, line => { FiresLogger.LogInfo(line); try { ctx?.AddString(line); } catch { } });
                },
                isCheat: true);
        }

        // Public, stable: the reply arrives on the main thread (from the server, or at once on a server or host).
        public static void Request(float x, float z, float y, Action<string> reply)
        {
            if (reply == null) return;
            if (ZNet.instance == null) { reply($"{Tag} no world"); return; }
            if (ZNet.instance.IsServer()) { reply(Answer(x, z, y)); return; }
            EnsureRegistered();
            if (ZRoutedRpc.instance == null) { reply($"{Tag} no RPC"); return; }
            int id = s_nextId++;
            s_pending[id] = reply;
            var pkg = new ZPackage();
            pkg.Write(id); pkg.Write(x); pkg.Write(z); pkg.Write(float.IsNaN(y) ? float.MinValue : y);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcName, pkg);
        }

        private static void OnRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            int id = pkg.ReadInt();
            float x = pkg.ReadSingle(), z = pkg.ReadSingle(), y = pkg.ReadSingle();
            string line;
            try { line = Answer(x, z, y <= float.MinValue ? float.NaN : y); }
            catch (Exception ex) { line = $"{Tag} the server's probe threw: {ex.Message}"; }
            var reply = new ZPackage();
            reply.Write(id); reply.Write(line);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpcName, reply);
        }

        private static void OnReply(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            int id = pkg.ReadInt();
            string line = pkg.ReadString();
            if (s_pending.TryGetValue(id, out var cb)) { s_pending.Remove(id); cb(line); }
        }

        // The answer, computed where it runs (the server for a remote asker).
        internal static string Answer(float x, float z, float y)
        {
            var inv = CultureInfo.InvariantCulture;
            if (s_mask == 0) s_mask = LayerMask.GetMask(GroundLayers);
            var p = new Vector3(x, 0f, z);
            var hm = Heightmap.FindHeightmap(p);
            bool hmHeight = Heightmap.GetHeight(p, out float hmY);
            float top = (float.IsNaN(y) ? (hmHeight ? hmY : WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 30f) : y) + RayAbove;

            var hits = Physics.RaycastAll(new Vector3(x, top, z), Vector3.down, RayAbove + RayBelow, s_mask, QueryTriggerInteraction.Ignore)
                .OrderByDescending(h => h.point.y).Take(ListHits).ToList();
            string ray = hits.Count == 0 ? "no hit" : string.Join("; ", hits.Select(h =>
            {
                var c = h.collider;
                string kind = c.GetComponentInParent<VoxelChunkView>() != null ? "voxel"
                    : c.GetComponentInParent<Heightmap>() != null ? "heightmap" : "other";
                return string.Format(inv, "y {0:0.00} on '{1}' ({2}, {3}, {4})", h.point.y, c.name, LayerMask.LayerToName(c.gameObject.layer), c.GetType().Name, kind);
            }));

            string voxel = VoxelWorld.TrySampleSurfaceY(x, z, top, top - RayAbove - RayBelow, out float vy, out bool wall)
                ? string.Format(inv, "y {0:0.00}", vy) : wall ? "solid at the top of the window" : "no loaded column";

            string heightmap;
            if (hm == null) heightmap = hmHeight ? string.Format(inv, "y {0:0.00}, no heightmap object here", hmY) : "none loaded";
            else
            {
                var col = hm.GetComponent<MeshCollider>();
                heightmap = string.Format(inv, "y {0:0.00}, collider {1}", hmY,
                    col == null ? "none" : col.enabled ? "ON" : "off");
            }

            string edits = "no voxel surface to key";
            if (!float.IsNaN(vy) && vy > float.MinValue)
            {
                var key = new Vector3Int(Mathf.FloorToInt(x / 32f), Mathf.FloorToInt((vy - 0.5f) / 32f), Mathf.FloorToInt(z / 32f));
                edits = VoxelChunkAnchor.TryGet(key, out var anchor) && anchor != null
                    ? $"chunk {key} anchor rev {anchor.LatestRevision} latest, {anchor.AppliedRevisionText} applied here"
                    : $"chunk {key} has no edit anchor loaded here";
            }

            string where = ZNet.instance != null && ZNet.instance.IsDedicated() ? "dedicated server" : ZNet.instance != null && ZNet.instance.IsServer() ? "host" : "client";
            return string.Format(inv, "{0} {1} ground at ({2:0.0}, {3:0.0}) from y {4:0.0}: ray {5} | voxel {6} | heightmap {7} | edits {8}",
                Tag, where, x, z, top, ray, voxel, heightmap, edits);
        }

        // ── HasEdits: public, stable ([fgn] option 2, a hand-off near carved ground) ───────────────────────────────────
        // True when a loaded VAVoxel edit anchor's chunk (32 m cube) comes within radius of pos (XZ and Y). Registry
        // lookup only: no physics, no zone build.
        public static bool HasEdits(Vector3 pos, float radius) => VoxelChunkAnchor.AnyNear(pos, Mathf.Max(0f, radius));

        // 0.2.694 ([lead]: "count untouched as columns whose voxel surface equals the generated height"): ground at (x, z)
        // standing more than EditedTolerance off WorldGenerator's height there is edited (a carve, a fill, a level, a pool).
        // The tolerance is the fill threshold judged on 09-29 (VoxelWrap ground sits a little off the generator anywhere).
        public const float EditedTolerance = 0.3f;

        public static bool IsGroundEdited(float x, float z, float groundY)
            => WorldGenerator.instance != null && Mathf.Abs(groundY - WorldGenerator.instance.GetHeight(x, z)) > EditedTolerance;

        // 0.2.693: the same in plan view: an edit anchor's 32 m column within radius of (x, z), whatever its height.
        public static bool HasEditsXZ(float x, float z, float radius) => VoxelChunkAnchor.AnyInColumn(x, z, Mathf.Max(0f, radius));

        // ── VoxelSurfaceY: public, stable (0.2.793; [visual]'s TestSite and any test that measures ground) ──────────────
        // The TOPMOST walkable voxel surface (a solid→air crossing: solid below, air above) in the column at (x, z) within
        // [nearY - reach, nearY + reach], read from the live voxel density on THIS peer: no physics, so building pieces,
        // creatures and stale colliders are never read. Highest crossing wins, so a cave pocket, an overhang's underside or
        // a lip below the top never does (VoxelWorld.TryFindSurfaceY picks the crossing NEAREST its hint in either
        // direction, which can be one of those). Keep the window round the layer you mean: a skyland or a cave roof outside
        // it is ignored. False when the column is not loaded here or holds no surface in the window.
        public static bool VoxelSurfaceY(float x, float z, float nearY, float reach, out float y)
            => VoxelWorld.TrySampleSurfaceY(x, z, nearY + Mathf.Abs(reach), nearY - Mathf.Abs(reach), out y, out _);

        // The same with the window round the generated ground (±GroundWindow m), for a caller with no height to start from.
        public static bool VoxelSurfaceY(float x, float z, out float y)
        {
            float ground = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 30f;
            return VoxelSurfaceY(x, z, ground, GroundWindow, out y);
        }

        private const float GroundWindow = 40f;

        // fat_surface_probe <x> <z> [nearY] [reach=30]: every surface crossing in the column on this peer, and what
        // TryFindSurfaceY (nearest to nearY) and VoxelSurfaceY (topmost) answer there. Read-only.
        internal static void RegisterSurfaceProbe()
        {
            new Terminal.ConsoleCommand("fat_surface_probe",
                "<x> <z> [nearY] [reach=30] - every voxel surface crossing in the column here, and what TryFindSurfaceY / VoxelSurfaceY read (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Length < 3 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x) || !float.TryParse(args[2], NumberStyles.Float, inv, out float z))
                    { args.Context?.AddString("fat_surface_probe <x> <z> [nearY] [reach=30]"); return; }
                    float near = args.Length > 3 && float.TryParse(args[3], NumberStyles.Float, inv, out float n) ? n
                        : (WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 30f);
                    float reach = args.Length > 4 && float.TryParse(args[4], NumberStyles.Float, inv, out float r) ? r : 30f;
                    string crossings = VoxelWorld.DescribeColumn(x, z, near - reach, near + reach);
                    bool nearOk = VoxelWorld.TryFindSurfaceY(x, z, near, reach, out float nearest);
                    bool topOk = VoxelSurfaceY(x, z, near, reach, out float top);
                    string line = string.Format(inv, "[FatSurfaceProbe] ({0:0.0},{1:0.0}) window {2:0.0}..{3:0.0}: crossings {4}; TryFindSurfaceY(near {5:0.0}) {6}; VoxelSurfaceY (topmost) {7}",
                        x, z, near - reach, near + reach, crossings, near, nearOk ? nearest.ToString("0.00", inv) : "none", topOk ? top.ToString("0.00", inv) : "none");
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                },
                isCheat: true);
        }

        // ── CollidersReady: public, stable (0.2.690, [fgn]: hold a server spawn / tame on edited ground until it is) ────
        // False while any voxel chunk within radius of pos (a 32 m cube grid) still has its edit anchor's latest revision
        // unapplied here, or has a surface whose collider bake is in flight or not yet assigned. True otherwise, including
        // where no voxel chunk is loaded (nothing of ours to wait for). Registry and view lookups only, no physics.
        public static bool CollidersReady(Vector3 pos, float radius)
        {
            radius = Mathf.Max(0f, radius);
            int x0 = Mathf.FloorToInt((pos.x - radius) / 32f), x1 = Mathf.FloorToInt((pos.x + radius) / 32f);
            int y0 = Mathf.FloorToInt((pos.y - radius) / 32f), y1 = Mathf.FloorToInt((pos.y + radius) / 32f);
            int z0 = Mathf.FloorToInt((pos.z - radius) / 32f), z1 = Mathf.FloorToInt((pos.z + radius) / 32f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (VoxelChunkAnchor.TryGet(key, out var anchor) && anchor != null && !anchor.Applied) return false;
                        if (VoxelWorld.TryGetView(key, out var view) && view != null && view.VertexCount > 0
                            && (view.BakeInFlight || !view.ColliderReady)) return false;
                    }
            return true;
        }
    }
}
