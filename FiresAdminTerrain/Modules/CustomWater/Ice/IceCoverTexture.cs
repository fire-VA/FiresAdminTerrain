using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// 0.2.1227 (Fire 10-29: "waves foam and tides should be hidden under ice"; contract agreed with [seasons] for FTS's sea):
    /// a global shader texture of FAT's synced ice cover around the camera, so other mods' water shaders can fade under the ice.
    ///   _FiresIceCover      R8, R = ice cover 0..1 (IceField.TryIceAt, 1 = solid sheet); bilinear, clamp, linear, no mips.
    ///   _FiresIceCoverRect  (x0, z0, sizeM, w): x0/z0 = world XZ of texel (0,0)'s corner, sizeM = side in metres, w = 1 valid / 0 off.
    /// Filled row by row inside a small frame budget into a back buffer and uploaded in one step when the camera has moved a
    /// step or every few seconds (ice changes slowly); never a per-frame upload. Client only. Add-only: nothing in FAT reads it.
    /// </summary>
    // 0.2.1360: on vanilla sea ice prefabs, client only (see IceCoverTexture). Exists exactly while the plate does.
    internal sealed class VanillaIcePlate : MonoBehaviour
    {
        private IceCoverTexture.Plate m_plate;
        private void OnEnable() { if (ZNet.instance != null) m_plate = IceCoverTexture.AddPlate(gameObject); }
        private void OnDisable() { IceCoverTexture.RemovePlate(m_plate); m_plate = null; }
    }

    internal static class IceCoverTexture
    {
        private const int Size = 128;
        private const float SideM = 256f, StepM = SideM / Size, RecentreM = 32f, RefreshSeconds = 3f, BudgetMs = 0.5f;
        private static readonly int TexId = Shader.PropertyToID("_FiresIceCover");
        private static readonly int RectId = Shader.PropertyToID("_FiresIceCoverRect");

        private static Texture2D s_tex;
        private static readonly byte[] s_back = new byte[Size * Size];
        private static Vector2 s_origin, s_building;
        private static int s_row = -1, s_iced, s_uploads, s_fills;
        private static float s_nextAt, s_logAt;
        private static double s_fillMs;
        private static bool s_on;
        private const float SafetySeconds = 30f;   // 0.2.1292
        private static int s_iceRev = int.MinValue, s_skipped; private static float s_iceExp = -1f;

        // 0.2.1260 ([seasons] / Fire 10-29, -253 512: "waves showing over" Vedr's ice): the sea shader (FTS) already calms waves, foam and
        // tide under _FiresIceCover, but only FAT's own ice was in it. Vedr's lake ice (Vedr.VedrApi.IsLakeIceAt, Norger.Vedr) is ORed
        // in per texel, soft-bound by reflection (no Vedr = unchanged), and VedrApi.LakeIceRevision (Vedr 0.8.89+) refills the cover
        // the moment Vedr's ice changes; without it the cover follows its own refresh.
        private static int s_vedr, s_vedrRev = int.MinValue;
        private static bool s_vedrBound;
        private static Func<Vector3, bool> s_vedrIce;
        private static Func<int> s_vedrRevGet;

        // 0.2.1360 ([seasons] for Fire 11-03: "so we dont show the water and waves swelling over the ice"; fires_mesh_at -4170 9145 16: 6 vanilla IceShore_1 plates, top y 30.43, sea 30, none in the cover):
        // vanilla sea ice plates register a disc while they exist (VanillaIcePlate, event driven, grid bucketed) and Vedr's floes join by VedrApi.IsSeaIceAt / SeaIceRevision. Both refill the cover at most every RefreshSeconds.
        private static readonly string[] VanillaIcePrefabs = { "IceShore_1", "IceShore", "ice1" };
        private const float PlateCellM = 32f, PlateAtSeaM = 0.5f;
        internal sealed class Plate { public float X, Z, R; }
        private static readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<Plate>> s_plates = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<Plate>>();
        private static int s_plateRev, s_plateRevSeen, s_plateCount, s_plateTexels, s_seaTexels, s_seaRev = int.MinValue;
        private static bool s_plateInfoSaid;
        private static Func<Vector3, bool> s_seaIce;
        private static Func<int> s_seaRevGet;

        private static bool PlatesOn => global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverVanilla == null || global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverVanilla.Value;
        private static long PlateCell(int x, int z) => ((long)x << 32) ^ (uint)z;

        internal static Plate AddPlate(GameObject go)
        {
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            bool any = false; var b = new Bounds();
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) { if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; } }
            if (!any || b.min.y > water + PlateAtSeaM || b.max.y < water - PlateAtSeaM) return null;   // on land: no waves to hide
            var p = new Plate { X = b.center.x, Z = b.center.z, R = Mathf.Max(b.extents.x, b.extents.z) };
            int x0 = Mathf.FloorToInt((p.X - p.R) / PlateCellM), x1 = Mathf.FloorToInt((p.X + p.R) / PlateCellM), z0 = Mathf.FloorToInt((p.Z - p.R) / PlateCellM), z1 = Mathf.FloorToInt((p.Z + p.R) / PlateCellM);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = PlateCell(x, z);
                    if (!s_plates.TryGetValue(k, out var list)) s_plates[k] = list = new System.Collections.Generic.List<Plate>();
                    list.Add(p);
                }
            s_plateCount++; s_plateRev++;
            if (!s_plateInfoSaid) { s_plateInfoSaid = true; FiresLogger.LogInfo($"[IceCover] vanilla sea ice joins _FiresIceCover: first plate '{go.name}' at ({p.X:0},{p.Z:0}) radius {p.R:0.0} m (0.2.1360)"); }
            return p;
        }

        internal static void RemovePlate(Plate p)
        {
            if (p == null) return;
            int x0 = Mathf.FloorToInt((p.X - p.R) / PlateCellM), x1 = Mathf.FloorToInt((p.X + p.R) / PlateCellM), z0 = Mathf.FloorToInt((p.Z - p.R) / PlateCellM), z1 = Mathf.FloorToInt((p.Z + p.R) / PlateCellM);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = PlateCell(x, z);
                    if (s_plates.TryGetValue(k, out var list)) { list.Remove(p); if (list.Count == 0) s_plates.Remove(k); }
                }
            s_plateCount--; s_plateRev++;
        }

        private static bool PlateIce(float x, float z)
        {
            if (s_plateCount == 0 || !PlatesOn) return false;
            if (!s_plates.TryGetValue(PlateCell(Mathf.FloorToInt(x / PlateCellM), Mathf.FloorToInt(z / PlateCellM)), out var list)) return false;
            foreach (var p in list) { float dx = x - p.X, dz = z - p.Z; if (dx * dx + dz * dz <= p.R * p.R) return true; }
            return false;
        }

        private static bool SeaIce(Vector3 p)
        {
            if (s_seaIce == null || !PlatesOn) return false;
            try { return s_seaIce(p); }
            catch (Exception ex) { s_seaIce = null; FiresLogger.LogDebug($"[IceCover] Vedr IsSeaIceAt threw, unbound: {ex.Message}"); return false; }
        }

        private static int SeaRevision()
        {
            if (s_seaRevGet == null) return s_seaRev;
            try { return s_seaRevGet(); } catch { s_seaRevGet = null; return s_seaRev; }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class AddPlateComponent
        {
            private static void Postfix(ZNetScene __instance)
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;   // the cover never runs on a dedicated server
                int added = 0; var missing = new System.Collections.Generic.List<string>();
                foreach (string n in VanillaIcePrefabs)
                {
                    GameObject go = __instance.GetPrefab(n);
                    if (go == null) { missing.Add(n); continue; }
                    if (go.GetComponent<VanillaIcePlate>() == null) { go.AddComponent<VanillaIcePlate>(); added++; }
                }
                FiresLogger.LogDebug($"[IceCover] vanilla sea ice prefabs: {added} of {VanillaIcePrefabs.Length} take a footprint{(missing.Count > 0 ? "; not on this world: " + string.Join(", ", missing) : "")} (0.2.1360)");
            }
        }

        private static void BindVedr()
        {
            if (s_vedrBound) return;
            s_vedrBound = true;
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { try { t = a.GetType("Vedr.VedrApi", false); } catch { } if (t != null) break; }
                if (t == null) { FiresLogger.LogDebug("[IceCover] Vedr not loaded: _FiresIceCover holds FAT ice only"); return; }
                var m = t.GetMethod("IsLakeIceAt", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
                if (m != null && m.ReturnType == typeof(bool)) s_vedrIce = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), m);
                var p = t.GetProperty("LakeIceRevision", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p != null && p.PropertyType == typeof(int)) s_vedrRevGet = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), p.GetGetMethod());
                var f = s_vedrRevGet == null ? t.GetField("LakeIceRevision", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                if (f != null && f.FieldType == typeof(int)) s_vedrRevGet = () => (int)f.GetValue(null);
                var sm = t.GetMethod("IsSeaIceAt", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
                if (sm != null && sm.ReturnType == typeof(bool)) s_seaIce = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), sm);
                var sp = t.GetProperty("SeaIceRevision", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (sp != null && sp.PropertyType == typeof(int)) s_seaRevGet = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), sp.GetGetMethod());
                FiresLogger.LogInfo($"[IceCover] Vedr sea ice (floes) joins _FiresIceCover: IsSeaIceAt {(s_seaIce != null ? "bound" : "not in this Vedr")}, SeaIceRevision {(s_seaRevGet != null ? "bound" : "not in this Vedr")} (0.2.1360; Info since 0.2.1418)");
                FiresLogger.LogInfo($"[IceCover] Vedr lake ice joins _FiresIceCover: IsLakeIceAt {(s_vedrIce != null ? "bound" : "MISSING")}, LakeIceRevision {(s_vedrRevGet != null ? "bound" : "not in this Vedr (timer refresh only)")} (0.2.1260)");
            }
            catch (Exception ex) { s_vedrIce = null; s_vedrRevGet = null; FiresLogger.LogDebug($"[IceCover] Vedr bind threw: {ex.Message}"); }
        }

        private static bool VedrIce(Vector3 p)
        {
            if (s_vedrIce == null) return false;
            try { return s_vedrIce(p); }
            catch (Exception ex) { s_vedrIce = null; FiresLogger.LogDebug($"[IceCover] Vedr IsLakeIceAt threw, unbound: {ex.Message}"); return false; }
        }

        private static int VedrRevision()
        {
            BindVedr();
            if (s_vedrRevGet == null) return s_vedrRev;
            try { return s_vedrRevGet(); } catch { s_vedrRevGet = null; return s_vedrRev; }
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class Driver
        {
            private static void Postfix()
            {
                long t0 = global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Now();   // 0.2.1268
                try { Tick(); } catch (Exception ex) { FiresLogger.LogDebug($"[IceCover] tick threw: {ex.Message}"); }
                global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Add(global::VerdantsAscent.Terrain.Performance.FatZnetTiming.Slot.IceCover, t0);
            }
        }

        private static void Tick()
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) { Off(); return; }
            Vector3 c = cam.transform.position;
            float now = Time.unscaledTime;
            if (s_row < 0)
            {
                bool moved = !s_on || Mathf.Abs(c.x - (s_origin.x + SideM * 0.5f)) > RecentreM || Mathf.Abs(c.z - (s_origin.y + SideM * 0.5f)) > RecentreM;
                bool cameraOnly = moved && s_on && s_row < 0;   // 0.2.1396
                int vrev = VedrRevision();
                if (now >= s_nextAt)   // 0.2.1360: plates load in bursts and floes drift, so these two wait for the refresh
                {
                    if (s_plateRev != s_plateRevSeen) { s_plateRevSeen = s_plateRev; moved = true; cameraOnly = false; }
                    int srev = SeaRevision();
                    if (srev != s_seaRev) { s_seaRev = srev; moved = true; cameraOnly = false; }
                }
                // 0.2.1401 (ptimer4b fly circle: ice cover 12.8 ms/s mean, 70 peak; Vedr's LakeIceRevision rose 62 -> 422 while flying, each rise a
                // whole 16384-texel fill that also threw away the camera-move reuse): with the key on a lake ice change waits for the refresh.
                var vwait = global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverVedrWaits;
                bool vedrWaits = vwait == null || vwait.Value;
                if (vedrWaits != s_vedrWaitsSaid) { s_vedrWaitsSaid = vedrWaits; FiresLogger.LogInfo($"[IceCover] Vedr lake ice changes {(vedrWaits ? "wait for the 3 s refresh" : "refill the whole cover at once")} (0.2.1401)"); }
                if (vrev != s_vedrRev && (!vedrWaits || now >= s_nextAt)) { s_vedrRev = vrev; moved = true; cameraOnly = false; s_whyVedr++; }   // 0.2.1260: Vedr's lake ice changed (froze / thawed)
                else if (vrev != s_vedrRev) s_vedrHeld++;
                // 0.2.1292 ([fgn] round 135322 [ZNetPostfix] split: ice cover the steadiest FAT ZNet cost, median 8.7 ms/s / 107 us a
                // call - the 3 s timer refilled 16384 ice + Vedr lookups whether or not anything changed): key "Ice cover refills only
                // on change" - the cover refills when the camera moved, Vedr's lake ice revision moved, FAT's ice changed (IceField.Revision:
                // breaks; AppliedExposure: freeze / thaw steps), or after 30 s at most; otherwise it stays as it is.
                bool onChange = global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverOnChange == null || global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverOnChange.Value;
                if (onChange)
                {
                    int irev = IceField.Revision; float iexp = IceField.AppliedExposure;
                    if (irev != s_iceRev || Mathf.Abs(iexp - s_iceExp) > 0.01f) { s_iceRev = irev; s_iceExp = iexp; moved = true; cameraOnly = false; }
                    if (!moved && now < s_nextAt + (SafetySeconds - RefreshSeconds)) { s_skipped++; Log(now); return; }
                }
                else if (!moved && now < s_nextAt) { Log(now); return; }
                s_building = new Vector2(Mathf.Floor((c.x - SideM * 0.5f) / StepM) * StepM, Mathf.Floor((c.z - SideM * 0.5f) / StepM) * StepM);
                s_row = 0; s_iced = 0; s_vedr = 0; s_plateTexels = 0; s_seaTexels = 0;
                BeginReuse(cameraOnly, now);   // 0.2.1396: the texels both rects share are kept
                s_hmByZone.Clear();   // 0.2.1402: heightmaps come and go with zones
            }
            var sw = Stopwatch.StartNew();
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            while (s_row < Size && sw.Elapsed.TotalMilliseconds < BudgetMs)
            {
                float z = s_building.y + (s_row + 0.5f) * StepM;
                for (int i = 0; i < Size; i++)
                {
                    float x = s_building.x + (i + 0.5f) * StepM;
                    if (s_reuse && i + s_reuseDx >= 0 && i + s_reuseDx < Size && s_row + s_reuseDz >= 0 && s_row + s_reuseDz < Size) continue;   // 0.2.1396: moved over from the last fill
                    byte v = 0;
                    // 0.2.1402 (lead 11-06, step 1 of the ice cover plan): per-lookup split and a land count, Debug only (no cost otherwise).
                    // Land is only COUNTED here: inland lakes sit above the sea level, so a land skip is proven safe only when
                    // "land texels with ice" stays 0 over a run.
                    bool prof = FiresLogger.DebugEnabled;
                    int vedrBefore = s_vedr, plateBefore = s_plateTexels, seaBefore = s_seaTexels;
                    long t0 = prof ? Stopwatch.GetTimestamp() : 0, t1;
                    if (IceField.TryIceAt(new Vector3(x, water, z), out float cover, out _) && cover > 0f) { v = (byte)Mathf.Clamp(Mathf.RoundToInt(cover * 255f), 0, 255); s_iced++; }
                    if (prof) { t1 = Stopwatch.GetTimestamp(); s_tIce += t1 - t0; t0 = t1; }
                    if (v == 0) { if (VedrIce(new Vector3(x, water, z))) { v = 255; s_iced++; s_vedr++; } if (prof) { t1 = Stopwatch.GetTimestamp(); s_tVedr += t1 - t0; t0 = t1; } }   // 0.2.1260: Vedr's lake ice counts as a full sheet
                    if (v == 0) { if (PlateIce(x, z)) { v = 255; s_iced++; s_plateTexels++; } if (prof) { t1 = Stopwatch.GetTimestamp(); s_tPlate += t1 - t0; t0 = t1; } }   // 0.2.1360
                    if (v == 0) { if (SeaIce(new Vector3(x, water, z))) { v = 255; s_iced++; s_seaTexels++; } if (prof) { t1 = Stopwatch.GetTimestamp(); s_tSea += t1 - t0; t0 = t1; } }   // 0.2.1360
                    if (prof)
                    {
                        s_texels++;
                        if (IsLand(x, z, water))
                        {
                            s_land++;
                            if (v != 0)
                            {
                                s_landIced++;
                                // 0.2.1403 (step 2): which lookup iced the land texel, so a per-lookup land skip is decided from data
                                if (s_vedr != vedrBefore) s_landIcedVedr++;
                                else if (s_plateTexels != plateBefore) s_landIcedPlate++;
                                else if (s_seaTexels != seaBefore) s_landIcedSea++;
                                else s_landIcedField++;
                            }
                        }
                        s_tLand += Stopwatch.GetTimestamp() - t0;
                    }
                    s_back[s_row * Size + i] = v;
                }
                s_row++;
            }
            s_fillMs += sw.Elapsed.TotalMilliseconds;
            if (s_row < Size) return;
            s_row = -1;
            Upload();
            s_nextAt = now + RefreshSeconds;
            Log(now);
        }

        // 0.2.1396 (FAT ZNet line: ice cover median 8.2 ms/s, nearly all fills while the camera moves; a 32 m recentre leaves about 3/4 of the 256 m rect the same):
        // when only the camera moved, the shared texels move over and only the new strip is asked. Any ice change, and at least every SafetySeconds, is a whole fill.
        // 0.2.1402: per-lookup split (Stopwatch ticks) and land count since the last Debug line; land share Info once per world
        private static long s_tIce, s_tVedr, s_tPlate, s_tSea, s_tLand;
        private static int s_landIcedField, s_landIcedVedr, s_landIcedPlate, s_landIcedSea;   // 0.2.1403
        private static int s_texels, s_land, s_landIced, s_worldTexels, s_worldLand, s_worldLandIced;
        private static ZNet s_landSaidFor;
        private const float LandMarginM = 1f;
        private static readonly System.Collections.Generic.Dictionary<Vector2s, Heightmap> s_hmByZone = new System.Collections.Generic.Dictionary<Vector2s, Heightmap>();

        // the game's own heightmap of the zone (one FindHeightmap per zone per fill): ground more than LandMarginM above the sea level
        private static bool IsLand(float x, float z, float water)
        {
            var p = new Vector3(x, 0f, z);
            Vector2s zone = ZoneSystem.GetZone(p);
            if (!s_hmByZone.TryGetValue(zone, out var hm)) { hm = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone)); s_hmByZone[zone] = hm; }
            return hm != null && hm.GetWorldHeight(p, out float h) && h > water + LandMarginM;
        }

        private static double TickMs(long t) => t * 1000.0 / Stopwatch.Frequency;

        private static bool? s_vedrWaitsSaid;   // 0.2.1401
        private static int s_whyVedr, s_vedrHeld;   // 0.2.1401
        private static bool s_reuse;
        private static int s_reuseDx, s_reuseDz, s_reuseFills;
        private static float s_lastWholeFill = -1000f;
        private static readonly byte[] s_moved = new byte[Size * Size];

        private static void BeginReuse(bool cameraOnly, float now)
        {
            var key = global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.IceCoverReuse;
            s_reuseDx = Mathf.RoundToInt((s_building.x - s_origin.x) / StepM);
            s_reuseDz = Mathf.RoundToInt((s_building.y - s_origin.y) / StepM);
            s_reuse = cameraOnly && (key == null || key.Value) && now - s_lastWholeFill < SafetySeconds && Mathf.Abs(s_reuseDx) < Size && Mathf.Abs(s_reuseDz) < Size;
            if (!s_reuse) { s_lastWholeFill = now; return; }
            s_reuseFills++;
            for (int r = 0; r < Size; r++)
                for (int i = 0; i < Size; i++)
                {
                    int oldI = i + s_reuseDx, oldR = r + s_reuseDz;
                    byte v = oldI >= 0 && oldI < Size && oldR >= 0 && oldR < Size ? s_back[oldR * Size + oldI] : (byte)0;
                    s_moved[r * Size + i] = v;
                    if (v != 0) s_iced++;
                }
            System.Buffer.BlockCopy(s_moved, 0, s_back, 0, s_moved.Length);
        }

        private static void Upload()
        {
            if (s_tex == null)
            {
                s_tex = new Texture2D(Size, Size, TextureFormat.R8, false, true) { name = "FiresIceCover", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                FiresLogger.LogInfo($"[IceCover] _FiresIceCover on: {Size}x{Size} R8 over {SideM:0} m around the camera ({StepM:0.0} m per texel) for water shaders (0.2.1227)");
            }
            s_tex.LoadRawTextureData(s_back);
            s_tex.Apply(false, false);
            s_origin = s_building;
            Shader.SetGlobalTexture(TexId, s_tex);
            Shader.SetGlobalVector(RectId, new Vector4(s_origin.x, s_origin.y, SideM, 1f));
            s_on = true; s_uploads++; s_fills++;
        }

        private static void Off()
        {
            if (!s_on) return;
            s_on = false; s_row = -1;
            Shader.SetGlobalVector(RectId, Vector4.zero);
        }

        private static void Log(float now)
        {
            if (now < s_logAt) return;
            s_logAt = now + 30f;
            if (s_uploads == 0) return;
            FiresLogger.LogDebug($"[IceCover] last 30 s: {s_uploads} upload(s), fill {s_fillMs:0.0} ms total over {s_fills} fill(s); last fill {s_iced} of {Size * Size} texel(s) iced, "
                + $"rect ({s_origin.x:0},{s_origin.y:0}) {SideM:0} m (0.2.1227); [WaterIce] cover: {s_vedr} texel(s) from Vedr lake ice ({(s_vedrIce != null ? "Vedr bound" : "no Vedr")}, revision {(s_vedrRevGet != null ? s_vedrRev.ToString() : "n/a")})");
            FiresLogger.LogDebug($"[IceCover] last fill also: {s_reuseFills} fill(s) kept the shared texels, {s_plateTexels} texel(s) from {s_plateCount} vanilla plate(s), {s_seaTexels} from Vedr floes (0.2.1360); whole fills from a Vedr lake ice change {s_whyVedr}, frames a change waited for the refresh {s_vedrHeld} (0.2.1401)");
            if (s_texels > 0)
            {
                FiresLogger.LogDebug($"[IceCover] lookups last 30 s over {s_texels} texel(s): IceField {TickMs(s_tIce):0.0} ms, Vedr lake {TickMs(s_tVedr):0.0} ms, plates {TickMs(s_tPlate):0.0} ms, Vedr floes {TickMs(s_tSea):0.0} ms, land check {TickMs(s_tLand):0.0} ms; land texels {s_land} ({100.0 * s_land / s_texels:0}%), land texels with ice {s_landIced} (IceField {s_landIcedField}, Vedr lake {s_landIcedVedr}, plates {s_landIcedPlate}, Vedr floes {s_landIcedSea}) (0.2.1403)");
                s_worldTexels += s_texels; s_worldLand += s_land; s_worldLandIced += s_landIced;
                if (s_landSaidFor != ZNet.instance && s_worldTexels >= Size * Size)
                {
                    s_landSaidFor = ZNet.instance;
                    FiresLogger.LogInfo($"[IceCover] land share this world: {100.0 * s_worldLand / s_worldTexels:0}% of {s_worldTexels} looked-up texel(s) are land (ground over {LandMarginM:0} m above the sea), {s_worldLandIced} of them had ice (0.2.1402)");
                    s_worldTexels = 0; s_worldLand = 0; s_worldLandIced = 0;
                }
            }
            s_tIce = s_tVedr = s_tPlate = s_tSea = s_tLand = 0; s_texels = s_land = s_landIced = 0;
            s_landIcedField = s_landIcedVedr = s_landIcedPlate = s_landIcedSea = 0;
            s_uploads = 0; s_fills = 0; s_fillMs = 0; s_reuseFills = 0; s_whyVedr = 0; s_vedrHeld = 0;
        }
    }
}
