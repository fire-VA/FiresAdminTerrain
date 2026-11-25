using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1466 ([worldgen], lead GO "PLAN A" 11-17 after Fire's hand test of 1465: "if you think these two waters are the same youre insane";
    // Tools\FAT_WATER_VANILLA_MATERIAL_PLAN_20261117.md). The 1462-1465 "vanilla-match" retune of FAT's own shader is GONE: it copied vanilla's two
    // BOTTOM colours onto FAT's surface colours and forced opacity 1 and tight ripples (the plan's section 1). Now the key draws FAT's inland
    // water with vanilla's OWN water material: a copy of the zone prefab's Custom/Water material taken at ZoneSystem.Awake (before any mod
    // re-shaders it), its shader and values untouched, the same water as the sea. It is a FAT provider on FAT's own water look host
    // (WaterLookHost, priority 100: EBM 300 / Vedr 200 still win while the hand-off key is on; with the hand-off key off it is the only one).
    // FAT feeds what vanilla's WaterVolume feeds its material (WaterVolume.cs 1.0.15 :118-135): _WaterTime every frame, _depth[4] and
    // _UseGlobalWind. Lost under it, on purpose: FAT's river current, rapids foam and the river-to-sea fade (Fire's eyes decide).
    // Kept on FAT's shader by the host: ice / lava / crust sheets, built pieces, waterfalls (WaterLookHost.Eligible).
    internal static class InlandVanillaMatch
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<float> WaveDepth;
        internal static bool Enabled => On != null && On.Value;

        internal static void Bind(ConfigFile cfg)
        {
            On = cfg.Bind("FiresInlandWater 0 Vanilla match", "Vanilla-match look", false,
                "FAT's own rivers, lakes and placed water are drawn with vanilla's own water material (the sea's shader and values), so they are the same " +
                "water as the sea. Rivers show no current under it. EBM's / Vedr's water look still wins while [Custom Water] Hand Off To Look Mods is on " +
                "and one of them draws. Off = FAT's own water shader. Live." + " " + VerdantsAscent.Terrain.Voxel.FatShaders.Needs(VerdantsAscent.Terrain.Voxel.FatShaders.InlandWaterKey));
            WaveDepth = cfg.Bind("FiresInlandWater 0 Vanilla match", "Vanilla wave depth", 0f,
                new ConfigDescription("Vanilla's per-water depth value (_depth, 0-1, vanilla uses ocean depth / 10 m) given to FAT's water under the Vanilla-match " +
                "look. It scales vanilla's waves: 0 = calm, no swell. Vanilla's waves reach metres at 1, so keep it small on rivers. Live." + " " + VerdantsAscent.Terrain.Voxel.FatShaders.Needs(VerdantsAscent.Terrain.Voxel.FatShaders.InlandWaterKey),
                new AcceptableValueRange<float>(0f, 1f)));
            // 0.2.1477 ([worldgen], lead overnight 11-18; _work\PLAN_fat_inlandwater_vanilla_tune_20261118.md phase 1, Fire: "we really need to tune the
            // existing shader to look better" / "we dont want to lose all this"): FAT's OWN shader, its material values set FROM vanilla's sea material
            // (the pristine copy), every FAT flow feature kept. Same section, so a change re-skins live (FiresInlandWaterConfig's hot-reload).
            Tuned = cfg.Bind("FiresInlandWater 0 Vanilla match", "Vanilla-tuned look", false,
                "FAT's own water shader (current, chips, rapids, eddy foam, river-to-sea fade all kept) with its colour, clarity, ripple, refraction and " +
                "reflection values taken from vanilla's sea material, so a river beside the sea reads as the same water. Biome / class water tints are not " +
                "used while it is on (one global look). Off = the configured look. Ignored while Vanilla-match look draws. Live." + " " + VerdantsAscent.Terrain.Voxel.FatShaders.Needs(VerdantsAscent.Terrain.Voxel.FatShaders.InlandWaterKey));
            TunedDepth = cfg.Bind("FiresInlandWater 0 Vanilla match", "Vanilla-tuned depth value", 0.25f,
                new ConfigDescription("The vanilla depth value (0-1; vanilla: ocean depth / 10 m, 0 at the shore) whose sea colour FAT's water takes under the " +
                "Vanilla-tuned look: its colour = vanilla's shallow-bottom colour blended toward its deep-bottom colour by this much. 0.25 = the sea about " +
                "2.5 m deep. Live." + " " + VerdantsAscent.Terrain.Voxel.FatShaders.Needs(VerdantsAscent.Terrain.Voxel.FatShaders.InlandWaterKey), new AcceptableValueRange<float>(0f, 1f)));
            On.SettingChanged += (_, __) => Sync("key");
            WaveDepth.SettingChanged += (_, __) => { if (s_registered) VerdantsAscent.FatApi.RefreshWaterLook(Owner); };
            // TEST ONLY ([lead] 11-17: an unattended look leg needs FAT water at a pose; TD can set cfg keys live but not press tools). Set live
            // to "pond x z radius depth" or "river x1 z1 x2 z2 width depth": FAT's own FatApi.WaterPlacePond / WaterPlaceRiver (the Lake / Vriver
            // carve + fill path, undo snapshot included) at the ground there. Load does nothing, empty does nothing; one Warning per placement.
            WaterTestPlace = cfg.Bind("Voxel Terrain - World", "Water Test Place", "",
                "TEST ONLY. Set while in a world to \"pond x z radius depth\", \"river x1 z1 x2 z2 width depth\" or \"fall x z width [facing deg]\" (metres) to place FAT water there, " +
                "so a rig leg can look at FAT water with no input. Empty = nothing. Only a change made in game places water.");
            WaterTestPlace.SettingChanged += (_, __) => PlaceTestWater(WaterTestPlace.Value);
        }

        internal static ConfigEntry<string> WaterTestPlace;   // 0.2.1462 test-only

        // 0.2.1464 ([lead] water1463 leg, dedi.log :4333 "refused: not loaded: no ground at (240,40)"): the 1463 RPC sent the spec to the
        // dedi, but a dedicated server has no terrain colliders / heightmaps to carve on (FatStudioWater reads ZoneSystem.GetGroundHeight, and
        // the carve's StampUndoable needs Heightmap.FindHeightmap). A real admin's Lake / Vriver tool never runs on the dedi either: it runs on
        // the admin's CLIENT (VoxelBrushDrag dispatch, ground from the client's own colliders), and only the results go to the server (terrain
        // through the TerrainComp ZDO / voxel write RPCs, water disks through WaterOperationRPC, which the server authorises: AdminCheck first).
        // So a client now places the test water itself through that same tool path (the Lake tool's carve = WaterShapingTools.StudioPond,
        // the Vriver click carve = WaterToolDriver.StudioRiver); a host / single player keeps FatApi (snapshot included). The 1463 RPC is gone.
        private static void PlaceTestWater(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return;
            try
            {
                if (ZoneSystem.instance == null) { FiresLogger.LogWarning("[FiresWater] Water Test Place: no world yet; nothing placed."); return; }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var w = spec.Split(new[] { ' ', ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                float[] f = new float[w.Length];
                for (int i = 1; i < w.Length; i++) if (!float.TryParse(w[i], System.Globalization.NumberStyles.Float, inv, out f[i])) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': '{w[i]}' is not a number; nothing placed."); return; }
                string kind = w.Length > 0 ? w[0].ToLowerInvariant() : "";
                if (ZNet.instance != null && !ZNet.instance.IsServer()) { PlaceOnClient(spec, kind, w, f); return; }
                bool ok; string reason; VerdantsAscent.FatWaterHandle handle;
                if (kind == "pond" && w.Length >= 5)
                {
                    var c = Ground(f[1], f[2]);
                    ok = VerdantsAscent.FatApi.WaterPlacePond(new VerdantsAscent.FatPondArgs { Center = c, Radius = Mathf.Clamp(f[3], 2f, 40f), Depth = Mathf.Clamp(f[4], 0.5f, 12f) }, out handle, out _, out reason);
                }
                else if (kind == "river" && w.Length >= 7)
                {
                    var a = Ground(f[1], f[2]); var b = Ground(f[3], f[4]);
                    ok = VerdantsAscent.FatApi.WaterPlaceRiver(new VerdantsAscent.FatRiverArgs { Path = new[] { a, b }, Width = Mathf.Clamp(f[5], 1f, 30f), Depth = Mathf.Clamp(f[6], 0.5f, 8f) }, out handle, out _, out reason);
                }
                else if (kind == "fall" && w.Length >= 4)   // 0.2.1479
                {
                    StartTestFall(spec, Ground(f[1], f[2]), f[3], w.Length >= 5 ? f[4] : 90f, "server / host");
                    return;
                }
                else { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': want \"pond x z radius depth\", \"river x1 z1 x2 z2 width depth\" or \"fall x z width [facing]\"; nothing placed."); return; }
                FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': {(ok ? "placed " + (handle != null ? handle.Kind + " at " + handle.Position : "") : "refused: " + reason)} (test-only key, 0.2.1462)");
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresWater] Water Test Place threw: {ex.Message}"); }
        }

        // 0.2.1464: a dedi client, as an admin's tool click would (see PlaceTestWater). The client gate is the one FAT's water tools use
        // (configSync.IsAdmin); the server still authorises every water disk and voxel write on its own.
        private static void PlaceOnClient(string spec, string kind, string[] w, float[] f)
        {
            if (FiresAdminTerrain.Instance?.configSync?.IsAdmin != true) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': refused on this client: not an admin (0.2.1464)"); return; }
            string done, how;
            if (kind == "pond" && w.Length >= 5)
            {
                if (!ClientGround(f[1], f[2], out var c, out how)) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': refused on this client: no ground at ({f[1]:0},{f[2]:0}) (0.2.1464)"); return; }
                VerdantsAscent.Terrain.FatUndo.BeginPlayerEdit("a test pond"); VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope++;   // as a V-tool click (DispatchNow)
                try { done = VerdantsAscent.Water.Tools.WaterShapingTools.StudioPond(c, Mathf.Clamp(f[3], 2f, 40f), Mathf.Clamp(f[4], 0.5f, 12f), float.NaN, false); }
                finally { VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope--; VerdantsAscent.Terrain.FatUndo.EndPlayerEdit(); }
                if (done == null) done = "refused: the pond would hold no water there";
            }
            else if (kind == "river" && w.Length >= 7)
            {
                if (!ClientGround(f[1], f[2], out var a, out how) || !ClientGround(f[3], f[4], out var b, out string how2)) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': refused on this client: no ground at an end (0.2.1464)"); return; }
                how += " / " + how2;
                if (VerdantsAscent.Water.Tools.WaterToolDriver.StudioRiverCarving) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': refused on this client: a river is still carving (0.2.1464)"); return; }
                done = VerdantsAscent.Water.Tools.WaterToolDriver.StudioRiver(new System.Collections.Generic.List<Vector3> { a, b }, Mathf.Clamp(f[5], 1f, 30f) * 0.5f, Mathf.Clamp(f[6], 0.5f, 8f))
                    ?? "refused: a river is still carving";   // the carve runs over the next frames (VoxelBrushDrag.Update pumps it), its water lays after
            }
            else if (kind == "fall" && w.Length >= 4)   // 0.2.1479
            {
                if (!ClientGround(f[1], f[2], out var fc, out how)) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': refused on this client: no ground at ({f[1]:0},{f[2]:0}) (0.2.1479)"); return; }
                StartTestFall(spec, fc, f[3], w.Length >= 5 ? f[4] : 90f, "on this client (the admin tool path; ground from " + how + ")");
                return;
            }
            else { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': want \"pond x z radius depth\", \"river x1 z1 x2 z2 width depth\" or \"fall x z width [facing]\"; nothing placed."); return; }
            FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': on this client (the admin tool path; ground from {how}): {done}; the server authorises the water disks (test-only key, 0.2.1464)");
        }

        // The ground a tool click reads on a client: the terrain ray onto the real colliders (voxel or heightmap), else the heightmap.
        // 0.2.1479 ([lead] 11-18, the Waterfall shader stand-down proof needs a real fall in an unattended leg): "fall x z width [facing]".
        // FAT's own Vfall path, as the admin tools test does it (AdminToolsWaterCases.VFall): a pit carved beside (x, z) along the facing
        // (Valheim yaw degrees: 0 = +z, 90 = +x; default 90), the colliders given 2 s, then ToolFalls.Place from just short of the pit's edge
        // looking across it, so the fall drops into the pit. Each step is one undoable tool edit. One Warning with Vfall's own result line.
        private const float TestFallPitDepth = 6f, TestFallColliderWait = 2f;

        private static void StartTestFall(string spec, Vector3 c, float width, float facingDeg, string where)
        {
            var host = VerdantsAscent.FiresAdminTerrain.Instance;
            if (host == null) { FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': no FAT instance; nothing placed (0.2.1479)"); return; }
            host.StartCoroutine(TestFall(spec, c, Mathf.Clamp(width, 2f, 16f), facingDeg, where));
        }

        private static System.Collections.IEnumerator TestFall(string spec, Vector3 c, float width, float facingDeg, string where)
        {
            float rad = facingDeg * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            float pitR = Mathf.Max(2f, width * 0.75f);
            string pit;
            VerdantsAscent.Terrain.FatUndo.BeginPlayerEdit("a test fall pit"); VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope++;
            try
            {
                pit = VerdantsAscent.Terrain.Voxel.VoxelWorld.ApplyBrush(c + dir * pitR + Vector3.down * (TestFallPitDepth * 0.5f), pitR, TestFallPitDepth,
                    VerdantsAscent.Terrain.Voxel.VoxelBrushShape.Cylinder, VerdantsAscent.Terrain.Voxel.VoxelBrushMode.Carve);
            }
            catch (System.Exception ex) { pit = "pit threw: " + ex.Message; }
            finally { VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope--; VerdantsAscent.Terrain.FatUndo.EndPlayerEdit(); }
            yield return new WaitForSecondsRealtime(TestFallColliderWait);   // the landing ray needs the pit's colliders
            string line;
            float offset = VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset;
            VerdantsAscent.Terrain.FatUndo.BeginPlayerEdit("a test fall"); VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope++;
            VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset = 0f;
            VerdantsAscent.Water.Tools.WaterShapingTools.ClearAnchor();
            try { line = VerdantsAscent.Water.Tools.ToolFalls.Place(c - dir * pitR, dir, width * 0.5f); }
            catch (System.Exception ex) { line = "Vfall threw: " + ex.Message; }
            finally
            {
                VerdantsAscent.Water.Tools.WaterShapingTools.ClearAnchor();
                VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset = offset;
                VerdantsAscent.Terrain.Voxel.VoxelWorld.VToolScope--; VerdantsAscent.Terrain.FatUndo.EndPlayerEdit();
            }
            FiresLogger.LogWarning($"[FiresWater] Water Test Place '{spec}': {where}: pit radius {pitR:0.0} m, {TestFallPitDepth:0} m deep, facing {facingDeg:0} ({pit}); {line} (test-only key, 0.2.1479)");
        }

        private static bool ClientGround(float x, float z, out Vector3 p, out string how)
        {
            p = new Vector3(x, 0f, z); how = "none";
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float y)) { p.y = y; how = "the terrain colliders"; return true; }
            if (Heightmap.GetHeight(new Vector3(x, 0f, z), out y)) { p.y = y; how = "the heightmap"; return true; }
            return false;
        }

        private static Vector3 Ground(float x, float z)
        {
            float y = 30f;
            if (!Heightmap.GetHeight(new Vector3(x, 0f, z), out y) && ZoneSystem.instance != null) y = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 500f, z));
            return new Vector3(x, y, z);
        }


        // ================================================================== vanilla's own material as a FAT provider (0.2.1466)

        internal const string Owner = "FiresAdminTerrain (vanilla water material)";
        internal const int Priority = 100;
        private static Material s_pristine;   // copy of the zone prefab's Custom/Water material, taken at ZoneSystem.Awake (never handed out)
        private static Material s_work;       // the one material FAT's water wears (a copy of s_pristine); _WaterTime is set on it every frame
        private static bool s_registered, s_saidDraw;
        private static MaterialPropertyBlock s_mpb;
        private static readonly float[] s_depth = new float[4];
        private static readonly int P_WaterTime = Shader.PropertyToID("_WaterTime"), P_Depth = Shader.PropertyToID("_depth"),
            P_UseGlobalWind = Shader.PropertyToID("_UseGlobalWind"), P_IsLod = Shader.PropertyToID("_IsLod");

        // True while FAT's own renderers wear vanilla's material (the host's top provider is this one).
        internal static bool Drawing => s_registered && WaterLookHost.TopIsInternal;

        // 0.2.1465 capture, now also the copy: zones spawn later (ZoneSystem.Update), so no WaterVolume.OnEnable has run and no mod has
        // re-shadered the shared asset yet (EBM swaps at the first zone's WaterVolume.OnEnable; water1464 :3363 / :9160). new Material(mat)
        // keeps Custom/Water and every value even after the asset itself is re-shadered later. Once per session; skipped on a headless server.
        [HarmonyLib.HarmonyPatch(typeof(ZoneSystem), "Awake")]
        private static class CapturePristineAtZoneSystemAwake
        {
            [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
            private static void Postfix(ZoneSystem __instance)
            {
                if (s_pristine != null || __instance == null) return;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
                try
                {
                    var zp = __instance.m_zonePrefab;
                    var wv = zp != null ? zp.GetComponentInChildren<WaterVolume>(true) : null;
                    var mat = wv != null && wv.m_waterSurface != null ? wv.m_waterSurface.sharedMaterial : null;
                    string sh = mat != null && mat.shader != null ? mat.shader.name : "?";
                    if (mat == null || sh != "Custom/Water")
                    {
                        FiresLogger.LogInfo($"[FiresInlandWater] vanilla material: no copy at ZoneSystem.Awake (zone prefab '{(zp != null ? zp.name : "none")}', water material '{(mat != null ? mat.name : "none")}', shader {sh}): the Vanilla-match look cannot draw this session (0.2.1466)");
                        return;
                    }
                    s_pristine = new Material(mat) { name = mat.name + " (vanilla, FAT copy)", hideFlags = HideFlags.DontUnloadUnusedAsset };
                    FiresLogger.LogInfo($"[FiresInlandWater] vanilla material: copied '{mat.name}' of the zone prefab '{zp.name}' (shader Custom/Water, queue {mat.renderQueue}) at ZoneSystem.Awake, before any zone water enabled (0.2.1466)");
                    DumpPristine();
                    Sync("material copied");
                }
                catch (System.Exception ex) { FiresLogger.LogInfo($"[FiresInlandWater] vanilla material: copy skipped: {ex.Message} (0.2.1466)"); }
            }
        }

        // Key on + a copy = registered with FAT's host; else withdrawn (FAT's own materials go back on, the host releases ours).
        private static void Sync(string why)
        {
            bool want = Enabled && s_pristine != null;
            if (want == s_registered) return;
            if (want)
            {
                WaterLookHost.RegisterInternal(Owner, Priority, () => Enabled && s_pristine != null, Build, ApplyBody, Release);
                s_registered = true;
            }
            else
            {
                s_registered = false;
                WaterLookHost.Unregister(Owner);
            }
            FiresLogger.LogInfo($"[FiresInlandWater] vanilla material: {(want ? "registered with FAT's water look host (priority 100)" : "withdrawn: FAT's own water shader")} ({why}) (0.2.1466)");
        }

        private static Material Build(VerdantsAscent.FatWaterLookRequest req)
        {
            if (s_pristine == null) return null;
            if (s_work == null)
            {
                s_work = new Material(s_pristine) { name = s_pristine.name.Replace("(vanilla, FAT copy)", "(vanilla, FAT water)"), hideFlags = HideFlags.DontUnloadUnusedAsset };
                if (s_work.HasProperty(P_IsLod)) s_work.SetFloat(P_IsLod, 0f);              // a near tile, not a far LOD plane
                if (s_work.HasProperty(P_UseGlobalWind)) s_work.SetFloat(P_UseGlobalWind, 1f);   // WaterVolume.SetupMaterial: m_useGlobalWind (default true)
                SetWaterTime();
            }
            return s_work;   // one shared material: FAT's host asks once per FAT material and may hand the same one back
        }

        // Per renderer (merged into its property block, FAT keeps its own values there): vanilla's _depth (4 corners, WaterVolume.SetupMaterial),
        // one value for the whole body. And tangents: vanilla's water pass reads TANGENT, FAT's sheet meshes carry none.
        private static void ApplyBody(Renderer r, VerdantsAscent.FatWaterLookRequest req)
        {
            if (r == null) return;
            float d = WaveDepth != null ? Mathf.Clamp01(WaveDepth.Value) : 0f;
            s_depth[0] = s_depth[1] = s_depth[2] = s_depth[3] = d;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_mpb);
            s_mpb.SetFloatArray(P_Depth, s_depth);
            r.SetPropertyBlock(s_mpb);
            EnsureTangents(r);
            if (!s_saidDraw) { s_saidDraw = true; FiresLogger.LogInfo($"[FiresInlandWater] vanilla material: first FAT water drawn with vanilla's material ('{r.name}', {req.Kind}, depth value {d:0.###}) (0.2.1466)"); }
        }

        private static void Release(Material m) { }   // the shared copy stays for the session (handed out again on the next switch)

        // Constant tangent along +X with the bitangent along +Z (w -1 with an up normal) on FAT's flat water meshes. UNCONFIRMED until the
        // sea-tile RenderDoc capture shows what vanilla's pass does with TANGENT; until then this is the tangent of a level sheet. Only while
        // FAT's water wears vanilla's material; meshes FAT rebuilds come back through the host's Adopt (WaterLookHost) or MeshTangents below.
        private static readonly List<Vector4> s_tan = new List<Vector4>(4096);
        internal static void EnsureTangents(Renderer r)
        {
            var mf = r != null ? r.GetComponent<MeshFilter>() : null;
            if (mf != null) MeshTangents(mf.sharedMesh);
        }

        internal static void MeshTangents(Mesh m)
        {
            if (!Drawing || m == null) return;
            int n = m.vertexCount;
            if (n == 0 || m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent)) return;
            s_tan.Clear();
            var t = new Vector4(1f, 0f, 0f, -1f);
            for (int i = 0; i < n; i++) s_tan.Add(t);
            m.SetTangents(s_tan);
        }

        private static void SetWaterTime()
        {
            // vanilla's s_waterTime follows ZNet's wrapped day time (WaterVolume.UpdateWaterTime), so FAT's water moves in step with the sea
            if (s_work != null && ZNet.instance != null) s_work.SetFloat(P_WaterTime, ZNet.instance.GetWrappedDayTimeSeconds());
        }

        // From WaterLookHost.Tick (Game.Update), every frame: _WaterTime on the shared material while it is in use. Then every 5 s: the key /
        // copy sync and the river-to-sea fade's sea level global for FAT's own shader (written only while nobody else writes it).
        private static float s_next;
        private static bool s_ownSea;
        private static readonly int P_Sea = Shader.PropertyToID("_FiresSeaLevelY");
        internal static void Tick()
        {
            if (s_registered && s_work != null) SetWaterTime();
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 5f;
            Sync("5 s check");
            if (!Enabled)
            {
                if (s_ownSea) { Shader.SetGlobalFloat(P_Sea, 0f); s_ownSea = false; FiresLogger.LogInfo("[FiresInlandWater] vanilla-match off: sea level global given back (0) (0.2.1462)"); }
                return;
            }
            float sea = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float cur = Shader.GetGlobalFloat(P_Sea);
            if (s_ownSea)
            {
                if (!Mathf.Approximately(cur, sea)) { s_ownSea = false; FiresLogger.LogInfo($"[FiresInlandWater] vanilla-match: another writer set the sea level global ({cur}): FAT stops writing it (0.2.1462)"); }
            }
            else if (cur == 0f)
            {
                Shader.SetGlobalFloat(P_Sea, sea);
                s_ownSea = true;
                FiresLogger.LogInfo($"[FiresInlandWater] vanilla-match: sea level global set to {sea} (nobody else writes it; the river-to-sea fade works) (0.2.1462)");
            }
        }

        // ================================================================== 0.2.1477: vanilla's real values (dump) + the Vanilla-tuned look (phase 1)

        internal static ConfigEntry<bool> Tuned;
        internal static ConfigEntry<float> TunedDepth;
        private static bool TunedOn => Tuned != null && Tuned.Value && s_pristine != null;

        // Every value vanilla's water pass reads, from the pristine copy, once at load (Info: the plan's step 1; the decompiled shader's Properties
        // block only carries placeholder defaults).
        private static readonly string[] s_dumpColors = { "_ColorTop", "_ColorBottom", "_ColorBottomShallow", "_SurfaceColor", "_FoamColor" };
        private static readonly string[] s_dumpFloats = { "_DepthFade", "_ShoreFade", "_FoamDepth", "_NormalScale", "_NormalPower", "_Glossiness", "_Metallic",
            "_RefractionScale", "_RefractionMax", "_WaveVel", "_Tess", "_UseGlobalWind", "_IsLod" };
        private static readonly string[] s_dumpTex = { "_Normal", "_NormalFine", "_FoamTex" };
        private static void DumpPristine()
        {
            var m = s_pristine; if (m == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var p in s_dumpColors) { if (!m.HasProperty(p)) { sb.Append($" {p}=absent;"); continue; } var c = m.GetColor(p); sb.Append(string.Format(inv, " {0}=({1:0.####},{2:0.####},{3:0.####},{4:0.####});", p, c.r, c.g, c.b, c.a)); }
            foreach (var p in s_dumpFloats) sb.Append(m.HasProperty(p) ? string.Format(inv, " {0}={1:0.#####};", p, m.GetFloat(p)) : $" {p}=absent;");
            foreach (var p in s_dumpTex)
            {
                if (!m.HasProperty(p)) { sb.Append($" {p}=absent;"); continue; }
                var t = m.GetTexture(p); var s = m.GetTextureScale(p); var o = m.GetTextureOffset(p);
                sb.Append(string.Format(inv, " {0}='{1}' {2}x{3} scale ({4},{5}) offset ({6},{7});", p, t != null ? t.name : "none", t != null ? t.width : 0, t != null ? t.height : 0, s.x, s.y, o.x, o.y));
            }
            sb.Append($" keywords [{string.Join(" ", m.shaderKeywords)}]");
            FiresLogger.LogInfo($"[FiresInlandWater] vanilla sea material values:{sb} (0.2.1477)");
        }

        // FAT's 1 - exp(-d / c) (ShoreBlend 1) fitted by least squares to vanilla's sqrt(d / fade) over 0..fade (64 samples), golden-section on c.
        private static float FitClear(float fade)
        {
            fade = Mathf.Max(fade, 0.05f);
            float Err(float c) { double e = 0; for (int i = 1; i <= 64; i++) { float d = fade * i / 64f; double a = 1.0 - System.Math.Exp(-d / c) - System.Math.Sqrt(d / fade); e += a * a; } return (float)e; }
            float lo = 0.01f, hi = fade * 4f, g = 0.6180339887f;
            float x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = Err(x1), f2 = Err(x2);
            for (int k = 0; k < 60; k++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = Err(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = Err(x2); }
            }
            return 0.5f * (lo + hi);
        }

        private static float PF(string p, float dflt) => s_pristine != null && s_pristine.HasProperty(p) ? s_pristine.GetFloat(p) : dflt;
        private static Color PC(string p, Color dflt) => s_pristine != null && s_pristine.HasProperty(p) ? s_pristine.GetColor(p) : dflt;
        private static void Set(Material m, string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
        private static void Set(Material m, string p, Color v) { if (m.HasProperty(p)) m.SetColor(p, v); }

        private static bool s_absorbOverridden;
        private static string s_saidTuned;

        // FiresInlandWaterConfig.Apply sets every value below from the cfg first; this then overrides them (material only, the cfg is never
        // written). Run again after InlandLookConfig.Apply (AfterLook), which can lower _ReflStrength. Off: Apply's own values stand; the one
        // value Apply never sets (_AbsorbBlend, shader default 1) is put back.
        internal static void AfterApply(Material m)
        {
            if (m == null) return;
            if (!TunedOn)
            {
                if (s_absorbOverridden) { Set(m, "_AbsorbBlend", 1f); s_absorbOverridden = false; }
                if (s_saidTuned != null) { s_saidTuned = null; FiresLogger.LogInfo("[FiresInlandWater] Vanilla-tuned look OFF: the configured look is back (0.2.1477)"); }
                return;
            }
            float dR = TunedDepth != null ? Mathf.Clamp01(TunedDepth.Value) : 0.25f;
            Color shallow = PC("_ColorBottomShallow", new Color(0.196f, 0.176f, 0.106f, 1f)), deep = PC("_ColorBottom", shallow);
            Color col = Color.Lerp(shallow, deep, dR); col.a = 1f;
            float fade = PF("_DepthFade", 3f), clear = FitClear(fade);
            float refr = Mathf.Min(PF("_RefractionScale", 0.006f) * 5f, PF("_RefractionMax", 0.2f));
            float nScale = PF("_NormalScale", 0.1f), nPower = PF("_NormalPower", 0.6f), gloss = PF("_Glossiness", 0.85f);

            Set(m, "_ShallowColor", col); Set(m, "_DeepColor", col);            // vanilla's hue does not change with the view column, only its opacity
            Set(m, "_ClearDepth", clear); Set(m, "_ShoreBlend", 1f);           // 1 - exp(-d / c) fitted to vanilla's sqrt(d / _DepthFade)
            Set(m, "_AbsorbBlend", 0f); s_absorbOverridden = true;             // vanilla has no per-channel absorption
            Set(m, "_MinOpacity", 1f); Set(m, "_MaxOpacity", 1f);              // the bed shows through FAT's own grab (body = lerp(bed, colour, depth)), as vanilla's
            Set(m, "_Turbidity", 0f); Set(m, "_MinMurk", 0f); Set(m, "_Churn", 0f);
            Set(m, "_RefractStrength", Mathf.Clamp(refr, 0f, 0.2f));
            Set(m, "_BumpTiling", 0.2f * nScale); Set(m, "_DetailTiling", nScale);   // vanilla's large tap (0.2 x) and its fine taps
            Set(m, "_NormalStrength", nPower);
            Set(m, "_ProcRipple", 0f);                                          // vanilla has no procedural ripple
            Set(m, "_NormalOpacityMin", 1f); Set(m, "_NormalOpacityMax", 1f); Set(m, "_DetailNormalOpacityMin", 1f); Set(m, "_DetailNormalOpacityMax", 1f);
            Set(m, "_FresnelPower", 5f);                                        // Schlick, as Unity Standard
            Set(m, "_ReflStrength", 1f); Set(m, "_StillReflBoost", 1f); Set(m, "_ReflWaterTint", 0f); Set(m, "_ReflTint", Color.white);
            Set(m, "_Smoothness", gloss);
            if (s_pristine.HasProperty("_FoamColor")) Set(m, "_FoamColor", s_pristine.GetColor("_FoamColor"));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string said = string.Format(inv, "colour ({0:0.###},{1:0.###},{2:0.###}) at depth value {3:0.##}; clear {4:0.###} m (fit to sqrt over _DepthFade {5:0.###} m); refraction {6:0.####}; normal tiling {7:0.####} / {8:0.####}, strength {9:0.###}; smoothness {10:0.###}",
                col.r, col.g, col.b, dR, clear, fade, refr, 0.2f * nScale, nScale, nPower, gloss);
            if (said != s_saidTuned) { s_saidTuned = said; FiresLogger.LogInfo($"[FiresInlandWater] Vanilla-tuned look ON ({m.name}): {said}; absorption 0, opacity 1/1, murk 0, proc ripple 0, fresnel 5, reflection 1 untinted (0.2.1477)"); }
        }

        private static Vector4[] s_zeroC, s_zeroB;
        internal static void AfterLook(Material m, int classes, int biomes)
        {
            if (m == null || !TunedOn) return;
            // one global look: the class / biome tables zeroed (no tint, no clarity / murk / ripple / reflection overrides); the cfg tables untouched
            if (s_zeroC == null || s_zeroC.Length != classes) s_zeroC = new Vector4[Mathf.Max(classes, 1)];
            if (s_zeroB == null || s_zeroB.Length != biomes) s_zeroB = new Vector4[Mathf.Max(biomes, 1)];
            m.SetVectorArray("_ClassTintS", s_zeroC); m.SetVectorArray("_ClassTintD", s_zeroC); m.SetVectorArray("_ClassLook", s_zeroC); m.SetVectorArray("_ClassLook2", s_zeroC);
            m.SetVectorArray("_BiomeTintS", s_zeroB); m.SetVectorArray("_BiomeTintD", s_zeroB); m.SetVectorArray("_BiomeLook", s_zeroB); m.SetVectorArray("_BiomeLook2", s_zeroB);
            Set(m, "_ReflStrength", 1f);   // InlandLookConfig.Apply may have capped it from the cfg's global reflection
        }
    }
}
