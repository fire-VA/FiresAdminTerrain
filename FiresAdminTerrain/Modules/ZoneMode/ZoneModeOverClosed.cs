using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Zones
{
    // ── BUILDING OVER CLOSED SEA (FAT 0.2.718; Fire 10-05) ──────────────────
    // "we were meant to be able to build over the edge of unopened chunks so that we could build platforms down to the water
    // etc. These builds would be removed if the unopen chunk were to be opened." Fire's answers: anywhere over a closed zone;
    // the materials of what is taken down go into a chest per builder, with their name, at the nearest open ground.
    //  * Placement (ZoneModeRefusals): a piece over a closed zone is allowed unless it is a terrain tool (a dig or a hoe
    //    stroke still never touches closed ground).
    //  * Record, never a sweep: when vanilla sets a placed piece's creator over a closed zone, the piece's ZDO gets
    //    OverClosedKey = that zone and the builder's name. That tag is the only thing a later take-down acts on.
    //  * Take-down (ZoneModeOpening.Open, server): only ZDOs in the opening zone that carry ITS tag are destroyed. Their full
    //    build cost and anything stored in them is kept per builder in a pending file beside the world (so a restart never
    //    loses it), then put into a "Returned materials" chest (and a sign with the builder's name) once the zone's ground
    //    is loaded, at the nearest dry ground to where the first piece stood.
    internal static class ZoneModeOverClosed
    {
        internal static readonly int OverClosedKey = "fires_overclosed".GetStableHashCode();     // Vector3(zone.x, 1, zone.y)
        internal static readonly int OverClosedByKey = "fires_overclosed_by".GetStableHashCode(); // builder's name
        private const string PendingSuffix = "_returns.dat";
        private const int PendingFormat = 1;
        private const string ChestPrefab = "piece_chest_wood";
        private const string SignPrefab = "sign";
        private const float WaterLevel = 30f;
        private const float DryMargin = 0.3f;
        private const float SearchStep = 2f;
        private const float SearchReach = 40f;
        private const float ChestSpacing = 1.6f;
        private const float RetrySeconds = 5f;
        private const int CarryWidth = 8, CarryHeight = 64;

        private sealed class PendingReturn
        {
            public string Builder;
            public long BuilderId;
            public Vector3 At;
            public Vector2s Zone;
            public byte[] Items;   // an Inventory (CarryWidth x CarryHeight) saved with vanilla's own format
            public int Pieces;
        }

        private static readonly List<PendingReturn> s_pending = new List<PendingReturn>();
        private static string s_loadedFor;
        private static float s_nextTry;

        // ── Placement record ────────────────────────────────────────────────
        // Vanilla sets a placed piece's creator on the placing peer, which owns the new piece; a piece standing over a
        // closed zone is tagged there and then.
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        private static class TagPieceOverClosedZone
        {
            private static void Postfix(Piece __instance, long uid)
            {
                if (!ZoneModeGate.Active || __instance == null || uid == 0L) return;
                var nview = __instance.GetComponent<ZNetView>();
                var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo == null || !nview.IsOwner()) return;
                Vector3 at = __instance.transform.position;
                if (!ZoneModeRefusals.IsClosedAt(at)) return;
                var zone = ZoneSystem.GetZone(at);
                string builder = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
                zdo.Set(OverClosedKey, new Vector3(zone.x, 1f, zone.y));
                zdo.Set(OverClosedByKey, builder);
                FiresLogger.LogDebug($"[FiresZones] built over closed zone ({zone.x},{zone.y}): {Utils.GetPrefabName(__instance.gameObject)} "
                    + $"at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) by '{builder}'; it is taken down (materials to a chest) when that zone opens.");
                ZoneModeRefusals.TellLocalPlayer($"Over closed sea: this is taken down when ({zone.x},{zone.y}) opens; its materials come back in a chest.");
            }
        }

        internal static bool IsTaggedFor(ZDO zdo, Vector2s zone)
        {
            Vector3 tag = zdo.GetVec3(OverClosedKey, Vector3.zero);
            return tag.y > 0.5f && Mathf.RoundToInt(tag.x) == zone.x && Mathf.RoundToInt(tag.z) == zone.y;
        }

        // ── Take-down on open (server) ──────────────────────────────────────
        internal static void TakeDown(IList<Vector2s> opened)
        {
            var zdoMan = ZDOMan.instance;
            var scene = ZNetScene.instance;
            if (zdoMan == null || scene == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            EnsureLoaded();
            int added = 0;
            foreach (var zone in opened)
            {
                var objects = new List<ZDO>();
                zdoMan.FindSectorObjects(zone, new SimulationDistance(0, 0), objects);
                var byBuilder = new Dictionary<string, PendingReturn>();
                var carried = new Dictionary<string, Inventory>();
                int removed = 0;
                foreach (var zdo in objects)
                {
                    if (zdo == null || !IsTaggedFor(zdo, zone)) continue;
                    var prefab = scene.GetPrefab(zdo.GetPrefab());
                    string builder = zdo.GetString(OverClosedByKey, "");
                    if (!byBuilder.TryGetValue(builder, out var ret))
                    {
                        ret = new PendingReturn { Builder = builder, BuilderId = zdo.GetLong(ZDOVars.s_creator), At = zdo.GetPosition(), Zone = zone };
                        byBuilder[builder] = ret;
                        carried[builder] = new Inventory("returned", null, CarryWidth, CarryHeight);
                    }
                    if (prefab != null) Carry(prefab, zdo, carried[builder]);
                    ret.Pieces++;
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    zdoMan.DestroyZDO(zdo);
                    removed++;
                }
                foreach (var pair in byBuilder)
                {
                    var package = new ZPackage();
                    carried[pair.Key].Save(package);
                    pair.Value.Items = package.GetArray();
                    s_pending.Add(pair.Value);
                    added++;
                    FiresLogger.LogInfo($"[FiresZones] opening ({zone.x},{zone.y}): took down {pair.Value.Pieces} piece(s) built over it by "
                        + $"'{pair.Key}' ({carried[pair.Key].GetAllItems().Count} stack(s) of materials kept for a chest at the nearest dry ground).");
                }
                if (removed == 0) continue;
            }
            if (added > 0) Save();
        }

        // A piece's full build cost (recoverable requirements, as a deconstruct refunds) and whatever it stores.
        private static void Carry(GameObject prefab, ZDO zdo, Inventory into)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece != null && piece.m_resources != null)
                foreach (var req in piece.m_resources)
                {
                    if (req == null || req.m_resItem == null || !req.m_recover || req.m_amount <= 0) continue;
                    AddStacks(into, req.m_resItem, req.m_amount);
                }
            var container = prefab.GetComponentInChildren<Container>(true);
            if (container == null) return;
            byte[] items = zdo.GetByteArray(ZDOVars.s_items);
            if (items == null || items.Length == 0) return;
            var stored = new Inventory("stored", null, Math.Max(1, container.m_width), Math.Max(1, container.m_height));
            stored.Load(new ZPackage(items));
            foreach (var item in stored.GetAllItems()) into.AddItem(item.Clone());
        }

        private static void AddStacks(Inventory into, ItemDrop drop, int amount)
        {
            int max = Math.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            while (amount > 0)
            {
                var item = drop.m_itemData.Clone();
                item.m_stack = Math.Min(amount, max);
                item.m_dropPrefab = drop.gameObject;
                if (!into.AddItem(item)) break;
                amount -= item.m_stack;
            }
        }

        // ── Chests, once the zone's ground is there (server, from ZoneModeDiagnostics) ──
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            EnsureLoaded();
            if (s_pending.Count == 0 || Time.realtimeSinceStartup < s_nextTry) return;
            s_nextTry = Time.realtimeSinceStartup + RetrySeconds;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || ZNetScene.instance == null) return;
            var generated = ZoneModeRegistry.GeneratedZones(zoneSystem);
            bool changed = false;
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var ret = s_pending[i];
                if (!ZoneModeRegistry.IsOpen(ret.Zone) || generated == null || !generated.Contains(ret.Zone)) continue;
                if (!TryFindDryGround(zoneSystem, ret.At, out Vector3 spot)) continue;
                int chests = PlaceChests(ret, spot);
                if (chests < 0) continue;
                s_pending.RemoveAt(i);
                changed = true;
                string text = $"{(string.IsNullOrEmpty(ret.Builder) ? "Builds" : ret.Builder + "'s builds")} over ({ret.Zone.x},{ret.Zone.y}) were "
                    + $"taken down when it opened; their materials are in {(chests == 1 ? "a chest" : chests + " chests")} at ({spot.x:0}, {spot.z:0}).";
                FiresLogger.LogInfo($"[FiresZones] {text}");
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            }
            if (changed) Save();
        }

        private static bool TryFindDryGround(ZoneSystem zoneSystem, Vector3 near, out Vector3 spot)
        {
            spot = near;
            if (!zoneSystem.GetGroundHeight(near, out float here)) return false;   // ground not loaded here yet: try later
            for (float r = 0f; r <= SearchReach; r += SearchStep)
            {
                int steps = r <= 0f ? 1 : 8;
                for (int a = 0; a < steps; a++)
                {
                    float ang = a * Mathf.PI * 2f / steps;
                    var p = new Vector3(near.x + Mathf.Cos(ang) * r, near.y, near.z + Mathf.Sin(ang) * r);
                    if (!ZoneModeRegistry.IsOpen(ZoneSystem.GetZone(p))) continue;
                    if (!zoneSystem.GetGroundHeight(p, out float h) || h < WaterLevel + DryMargin) continue;
                    spot = new Vector3(p.x, h, p.z);
                    return true;
                }
            }
            spot = new Vector3(near.x, Mathf.Max(here, WaterLevel), near.z);   // no dry ground near: at the water's surface
            return true;
        }

        // Fills as many chests as the materials need, side by side; each has the builder as its creator, and a sign names them.
        // -1 when a prefab is missing (tried again later).
        private static int PlaceChests(PendingReturn ret, Vector3 spot)
        {
            var chestPrefab = ZNetScene.instance.GetPrefab(ChestPrefab);
            var signPrefab = ZNetScene.instance.GetPrefab(SignPrefab);
            if (chestPrefab == null) return -1;
            var container = chestPrefab.GetComponentInChildren<Container>(true);
            int w = container != null ? container.m_width : 5, h = container != null ? container.m_height : 2;
            var carried = new Inventory("returned", null, CarryWidth, CarryHeight);
            if (ret.Items != null && ret.Items.Length > 0) carried.Load(new ZPackage(ret.Items));
            var items = new List<ItemDrop.ItemData>(carried.GetAllItems());
            int chests = 0, next = 0;
            do
            {
                var at = spot + new Vector3(chests * ChestSpacing, 0.05f, 0f);
                var zdo = NetworkObjectHelper.CreateZdo(chestPrefab, at, Quaternion.identity);
                if (zdo == null) return chests == 0 ? -1 : chests;
                var inv = new Inventory(ChestPrefab, null, w, h);
                while (next < items.Count && inv.CanAddItem(items[next])) inv.AddItem(items[next++]);
                var package = new ZPackage();
                inv.Save(package);
                zdo.Set(ZDOVars.s_items, package.GetArray());
                zdo.Set(ZDOVars.s_addedDefaultItems, true);
                if (ret.BuilderId != 0L) zdo.Set(ZDOVars.s_creator, ret.BuilderId);
                chests++;
            } while (next < items.Count);
            if (signPrefab != null)
            {
                var sign = NetworkObjectHelper.CreateZdo(signPrefab, spot + new Vector3(-ChestSpacing, 0.05f, 0f), Quaternion.identity);
                if (sign != null)
                {
                    sign.Set(ZDOVars.s_text, $"Returned materials: {(string.IsNullOrEmpty(ret.Builder) ? "builder unknown" : ret.Builder)}");
                    if (ret.BuilderId != 0L) sign.Set(ZDOVars.s_creator, ret.BuilderId);
                }
            }
            return chests;
        }

        // ── The pending file (beside the world; a restart keeps every return) ──
        private static string PendingPath()
        {
            string world = ZoneModeRegistry.WorldName;
            return string.IsNullOrEmpty(world) ? null : Voxel.FiresWorldRecords.PathFor(world, PendingSuffix);
        }

        private static void EnsureLoaded()
        {
            string world = ZoneModeRegistry.WorldName;
            if (world == s_loadedFor) return;
            s_loadedFor = world;
            s_pending.Clear();
            string path = PendingPath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var pkg = new ZPackage(File.ReadAllBytes(path));
                if (pkg.ReadInt() != PendingFormat) { FiresLogger.LogWarning($"[FiresZones] {path}: unknown format; kept, not read."); return; }
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                    s_pending.Add(new PendingReturn
                    {
                        Builder = pkg.ReadString(), BuilderId = pkg.ReadLong(), At = pkg.ReadVector3(),
                        Zone = new Vector2s(pkg.ReadInt(), pkg.ReadInt()), Pieces = pkg.ReadInt(), Items = pkg.ReadByteArray(),
                    });
                FiresLogger.LogInfo($"[FiresZones] {count} pending return(s) of materials from builds over opened zones, waiting for their ground.");
            }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] {path} could not be read ({ex.GetType().Name}: {ex.Message}); kept as it is."); }
        }

        private static void Save()
        {
            string path = PendingPath();
            if (path == null) return;
            try
            {
                var pkg = new ZPackage();
                pkg.Write(PendingFormat);
                pkg.Write(s_pending.Count);
                foreach (var r in s_pending)
                {
                    pkg.Write(r.Builder ?? ""); pkg.Write(r.BuilderId); pkg.Write(r.At);
                    pkg.Write((int)r.Zone.x); pkg.Write((int)r.Zone.y); pkg.Write(r.Pieces); pkg.Write(r.Items ?? new byte[0]);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, pkg.GetArray());
                if (File.Exists(path)) File.Replace(temp, path, path + ".previous");
                else File.Move(temp, path);
            }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] pending returns could not be saved ({ex.GetType().Name}: {ex.Message})."); }
        }
    }
}
