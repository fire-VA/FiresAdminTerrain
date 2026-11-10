using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1083 (Fire: "our cultivated ground ... does not get a bumpy texture"): on voxel ground a cultivate op paints the
    // cultivated layer, and the voxel shader's cultivated layer is the same tap as vanilla's on flat faces (g_upW = 1 at
    // n.y >= 0.8: VArrD_auv / V_N_AUV return the plain top-down sample, FiresVoxelHeightmap.shader:225-250), so the look comes
    // down to the ground's SHAPE. A cultivate op adds a small deterministic per-vertex height jitter (a hash of the world
    // x,z on vanilla's 1 m vertex grid) to the TerrainComp's level delta inside its paint radius, on voxel zones only. It is
    // saved and synced with the comp like any terrain edit, and the voxel zone rebuilds from it like any hoe edit. Paths,
    // farm plots and non-cultivated ground are untouched; heightmap worlds keep vanilla's own look.
    internal static class FatCultivateLumps
    {
        private const float Amplitude = 0.07f;   // metres, either way

        private static readonly AccessTools.FieldRef<TerrainComp, float[]> LevelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> Modified = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, int> Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static int s_ops, s_verts;
        private static float s_logAt;

        internal static float Jitter(float wx, float wz)
        {
            int ix = Mathf.FloorToInt(wx), iz = Mathf.FloorToInt(wz);
            uint h = unchecked((uint)(ix * 73856093) ^ (uint)(iz * 19349663) ^ 0x9E3779B9u);
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return ((h & 0xFFFF) / 65535f * 2f - 1f) * Amplitude;
        }

        // 0.2.1085: REMOVED (no [HarmonyPatch]). A Cultivate op repeated over RPC (~30/s at one spot) re-added the same signed
        // jitter every time and stacked it to the +-8 m clamp: saved pillars and pits that the voxel zone rebuilt as rock walls
        // round Fire's hut. FAT never adds height on a terrain op again (Fire re-levels his test world himself; no repair command).
        // 0.2.1397: the dead Patch_Cultivate class (never attached, no [HarmonyPatch]) is deleted; nothing ran before or after.

        // 0.2.1086 (Fire: "same with our cultivated ground"): vanilla's tilled look is ONE texture, _CultivatedNormal (world xz x 0.25)
        // plus the layer-10 diffuse, blended by the paint mask's g (Custom/Heightmap; cultivate_v2 only smooths and paints, no height).
        // Two runtime links are made sure of here; neither writes any terrain data:
        //  (1) the shared voxel material carries vanilla's cultivated textures/values, copied from the zone prefab's Heightmap.m_material;
        //  (2) a cultivate op re-composes the voxel paint copies of the columns it touched straight away (the Regenerate hook also does,
        //      once the Poke lands), and logs the g cells it wrote.
        [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
        private static class Patch_CultivatePaint
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier) => OnDoOperation(__instance, pos, modifier);
        }

        // The DoOperation postfix's body, also called per item by FatTrailBatch (a batched op never runs DoOperation).
        internal static void OnDoOperation(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
        {
            {
                try
                {
                    if (modifier == null || !modifier.m_paintCleared || modifier.m_paintType != TerrainModifier.PaintType.Cultivate) return;
                    float reach = modifier.m_paintRadius + 2f;
                    int c0x = Mathf.FloorToInt((pos.x - reach) / 32f), c1x = Mathf.FloorToInt((pos.x + reach) / 32f);
                    int c0z = Mathf.FloorToInt((pos.z - reach) / 32f), c1z = Mathf.FloorToInt((pos.z + reach) / 32f);
                    for (int cz = c0z; cz <= c1z; cz++)
                        for (int cx = c0x; cx <= c1x; cx++) VoxelWorld.MarkPaintDirty(new Vector2Int(cx, cz));
                    if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_paintLogAt) return;
                    s_paintLogAt = Time.unscaledTime + 5f;
                    int cells = 0;
                    var paint = PaintMask(__instance);
                    var hm = Hmap(__instance);
                    if (paint != null && hm != null)
                    {
                        int w = Width(__instance) + 1;   // 0.2.1100: the paint mask pitch is m_width + 1 (TerrainComp.getIndex)
                        hm.WorldToVertexMask(pos, out int mx, out int my);
                        int r = Mathf.CeilToInt(modifier.m_paintRadius / hm.m_scale);
                        for (int y = my - r; y <= my + r; y++)
                            for (int x = mx - r; x <= mx + r; x++)
                                if (x >= 0 && y >= 0 && x < w && y < w && paint[y * w + x].g > 0.5f) cells++;
                    }
                    FiresLogger.LogDebug($"[Cultivated] cultivate paint g written {cells} cells at ({pos.x:0.0},{pos.z:0.0}) r {modifier.m_paintRadius:0.0} strength {modifier.m_paintStrength:0.00}{(modifier.m_paintStrength < 0f ? " (a snow melter: vanilla Deep North fire)" : "")}, voxel copy refreshed (columns {c0x}..{c1x} x {c0z}..{c1z} re-composed)");
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Cultivated] paint refresh skipped (exception): {ex.Message}"); }
            }
        }
        private static float s_paintLogAt;

        // (1) Re-checked every 2 s (a cheap reference compare): a capture, recapture or shader swap hands the voxel skin a new material.
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class Patch_BindCultivated
        {
            private static Material s_boundTo;
            private static float s_nextCheck, s_failLogAt;
            internal static Material s_earlyVanilla;
            private static bool s_said;

            // 0.2.1089: capture the zone prefab's ORIGINAL material on ZoneSystem.Awake, before Core swaps in its tile material.
            [HarmonyPatch(typeof(ZoneSystem), "Awake")]
            private static class Patch_Early
            {
                [HarmonyPriority(Priority.First)]
                private static void Prefix(ZoneSystem __instance)
                {
                    try
                    {
                        var hm = __instance.m_zonePrefab != null ? __instance.m_zonePrefab.GetComponentInChildren<Heightmap>(true) : null;
                        if (hm != null && hm.m_material != null && hm.m_material.shader != null && hm.m_material.shader.name == "Custom/Heightmap")
                            s_earlyVanilla = hm.m_material;
                    }
                    catch { }
                }
            }

            private static void Postfix(ZoneSystem __instance)
            {
                if (Time.unscaledTime < s_nextCheck) return;
                s_nextCheck = Time.unscaledTime + 2f;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
                // 0.2.1316 (P10 event-driven, SAME behaviour): with Core's TerrainMaterialCache.MaterialChanged present the bind body
                // only runs when the event fired or the last run did not settle (s_boundTo != the material it read: no material yet,
                // an exception before the bind); the poll's only trigger was "GetCurrentMaterial() != s_boundTo", and every change
                // of that reference goes through Core's SetCached (raises the event) or the fallback built inside our own read.
                // Same 2 s tick, same body. No event (older Core) = the 2 s poll unchanged.
                if (EventMode())
                {
                    if (!s_dirty) return;
                    s_dirty = false;
                }
                Material voxRead = null;
                try
                {
                    var vox = FiresCore.Materials.TerrainMaterialCache.GetCurrentMaterial();
                    voxRead = vox;
                    if (!s_said) { s_said = true; FiresLogger.LogInfo("[Cultivated] bind hook executing (ZoneSystem.Update, every 2 s)"); }
                    if (vox == null)
                    {
                        if (Time.unscaledTime >= s_failLogAt) { s_failLogAt = Time.unscaledTime + 60f; FiresLogger.LogInfo("[Cultivated] bail: Core TerrainMaterialCache has no current voxel material yet"); }
                        return;
                    }
                    if (vox == s_boundTo) return;   // already bound to this material (logged when bound)
                    // 0.2.1089: at runtime the zone prefab's Heightmap.m_material is Core's FiresUnifiedCore_TileMaterial (Fire's log:
                    // "bound from 'FiresUnifiedCore_TileMaterial' (shader 'Custom/FiresVoxelHeightmap'): _CultivatedNormal=NULL"), i.e.
                    // ourselves. The source must be a VANILLA 'Custom/Heightmap' material holding a _CultivatedNormal; failing that,
                    // vanilla's texture by name ('cultivated_n', world/terrain/cultivated_n.png in the 1.0 rip). Never a NULL value.
                    var tried = new System.Text.StringBuilder();
                    var candidates = new System.Collections.Generic.List<Material>();
                    if (s_earlyVanilla != null) candidates.Add(s_earlyVanilla);
                    var hm = __instance.m_zonePrefab != null ? __instance.m_zonePrefab.GetComponentInChildren<Heightmap>(true) : null;
                    if (hm != null && hm.m_material != null) candidates.Add(hm.m_material);
                    foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                        if (m != null && m.shader != null && m.shader.name == "Custom/Heightmap") candidates.Add(m);
                    Material van = null;
                    foreach (var m in candidates)
                    {
                        if (m == null || m == vox) continue;
                        var cn = m.HasProperty("_CultivatedNormal") ? m.GetTexture("_CultivatedNormal") : null;
                        if (tried.Length < 1500) tried.Append($"'{m.name}' ({m.shader?.name}) _CultivatedNormal={(cn != null ? cn.name : "NULL")}; ");
                        if (van == null && cn != null && m.shader != null && m.shader.name == "Custom/Heightmap") van = m;
                    }
                    if (van == null)
                    {
                        Texture2D byName = null;
                        foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                            if (t != null && t.name == "cultivated_n") { byName = t; break; }
                        if (byName == null || !vox.HasProperty("_CultivatedNormal"))
                        {
                            // ONE search per voxel material (lead: FindObjectsOfTypeAll on a timer is a ZoneSystem.Update cost suspect);
                            // a new material (capture / shader swap) searches again.
                            s_boundTo = vox;
                            FiresLogger.LogInfo($"[Cultivated] cultivated bind: NO vanilla source for '{vox.name}' (searched once; again on the next voxel material); tried {tried}texture 'cultivated_n' {(byName == null ? "not loaded" : "loaded but the voxel material has no _CultivatedNormal")}");
                            return;
                        }
                        s_boundTo = vox;
                        vox.SetTexture("_CultivatedNormal", byName);
                        FiresLogger.LogInfo($"[Cultivated] voxel material cultivated props bound from texture by name onto '{vox.name}' (shader '{vox.shader?.name}'): _CultivatedNormal={byName.name} {byName.width}x{byName.height}; materials tried: {tried}");
                        return;
                    }
                    s_boundTo = vox;
                    int copied = 0, filled = 0;
                    var sh = van.shader;
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        string name = sh.GetPropertyName(i);
                        int id = sh.GetPropertyNameId(i);
                        if (name == "_ClearedMaskTex" || !vox.HasProperty(id)) continue;
                        bool cultivated = name.IndexOf("Cultivated", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        switch (sh.GetPropertyType(i))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                var t = van.GetTexture(id);
                                if (t == null) break;
                                if (cultivated)
                                {
                                    if (vox.GetTexture(id) != t) { vox.SetTexture(id, t); copied++; }
                                    vox.SetTextureScale(id, van.GetTextureScale(id)); vox.SetTextureOffset(id, van.GetTextureOffset(id));
                                }
                                else if (vox.GetTexture(id) == null) { vox.SetTexture(id, t); filled++; }   // an unbound layer only; never overrides a season/FTS texture
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Float:
                            case UnityEngine.Rendering.ShaderPropertyType.Range:
                                if (cultivated && !Mathf.Approximately(vox.GetFloat(id), van.GetFloat(id))) { vox.SetFloat(id, van.GetFloat(id)); copied++; }
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Color:
                                if (cultivated && vox.GetColor(id) != van.GetColor(id)) { vox.SetColor(id, van.GetColor(id)); copied++; }
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                if (cultivated && vox.GetVector(id) != van.GetVector(id)) { vox.SetVector(id, van.GetVector(id)); copied++; }
                                break;
                        }
                    }
                    string Tex(string p) => vox.HasProperty(p) ? (vox.GetTexture(p) != null ? vox.GetTexture(p).name : "NULL") : "no property";
                    var cnT = vox.HasProperty("_CultivatedNormal") ? vox.GetTexture("_CultivatedNormal") : null;
                    FiresLogger.LogInfo($"[Cultivated] voxel material cultivated props bound from vanilla material '{van.name}' ({(cnT != null ? cnT.width + "x" + cnT.height : "no size")}) (shader '{sh.name}') onto '{vox.name}' (shader '{vox.shader?.name}'): _CultivatedNormal={Tex("_CultivatedNormal")}, _CultivatedTex={Tex("_CultivatedTex")}; {copied} cultivated value(s) changed, {filled} unbound texture(s) filled");
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Cultivated] bind skipped (exception): {ex.Message}"); }
                finally
                {
                    // 0.2.1316: not settled -> run again next tick, exactly when the poll would have.
                    if (s_eventMode == 1 && (voxRead == null || voxRead != s_boundTo)) s_dirty = true;
                }
            }

            // 0.2.1316 (P10): 0 = not resolved yet, 1 = event (Core has MaterialChanged), 2 = poll fallback. Resolved once, by reflection
            // (the referenced Core build may predate the event).
            private static int s_eventMode;
            private static volatile bool s_dirty = true;   // true = the bind at subscribe time (covers load order)
            private static bool EventMode()
            {
                if (s_eventMode == 0)
                {
                    s_eventMode = 2;
                    try
                    {
                        var ev = typeof(FiresCore.Materials.TerrainMaterialCache).GetEvent("MaterialChanged",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (ev != null && ev.EventHandlerType == typeof(System.Action))
                        {
                            ev.AddEventHandler(null, new System.Action(() => s_dirty = true));
                            s_eventMode = 1;
                        }
                    }
                    catch (System.Exception ex) { FiresLogger.LogDebug($"[Cultivated] MaterialChanged subscribe failed ({ex.Message}); keeping the 2 s poll"); s_eventMode = 2; }
                    FiresLogger.LogDebug(s_eventMode == 1
                        ? "[Cultivated] bind mode: EVENT - Core TerrainMaterialCache.MaterialChanged subscribed, bind runs once now and on each change, no 2 s poll (0.2.1316)"
                        : "[Cultivated] bind mode: POLL - Core has no TerrainMaterialCache.MaterialChanged, keeping the 2 s re-check (0.2.1316)");
                }
                return s_eventMode == 1;
            }
        }

        // fat_cultivate_probe: 5 x 5 heights around the crosshair at 1 m spacing, the voxel surface and the hidden vanilla
        // heightmap side by side, plus the cultivated paint (g) and this cell's jitter. Run it before and after cultivating.
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Command
        {
            private static bool s_registered;
            private static void Postfix()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("fat_cultivate_probe",
                    "[FiresAdminTerrain] 5x5 ground heights (voxel vs hidden heightmap), cultivated paint and the lump jitter at the crosshair",
                    args => args.Context?.AddString(Probe()), isCheat: true);
            }
        }




        private static string Probe()
        {
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return "[FatCultivateProbe] no camera";
            if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 60f, LayerMask.GetMask("terrain", "Default", "static_solid"), QueryTriggerInteraction.Ignore))
                return "[FatCultivateProbe] nothing under the crosshair";
            float cx = Mathf.Floor(hit.point.x), cz = Mathf.Floor(hit.point.z);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FatCultivateProbe] at ({hit.point.x:0.0},{hit.point.y:0.0},{hit.point.z:0.0}) on '{hit.collider.name}'. rows z-2..z+2, voxel / heightmap (m):");
            for (int dz = -2; dz <= 2; dz++)
            {
                sb.Append("\n  ");
                for (int dx = -2; dx <= 2; dx++)
                {
                    float x = cx + dx, z = cz + dz;
                    string v = VoxelWorld.TrySampleSurfaceY(x, z, hit.point.y + 4f, hit.point.y - 4f, out float vy, out _) ? vy.ToString("0.000") : "  -   ";
                    string h = Heightmap.GetHeight(new Vector3(x, 0f, z), out float hy) ? hy.ToString("0.000") : "  -   ";
                    sb.Append(v).Append('/').Append(h).Append("  ");
                }
            }
            var hm = Heightmap.FindHeightmap(hit.point);
            string paint = "no heightmap";
            if (hm != null) { hm.WorldToVertexMask(hit.point, out int mx, out int my); var c = hm.GetPaintMask(mx, my); paint = $"r {c.r:0.00} g (cultivated) {c.g:0.00} b {c.b:0.00} a {c.a:0.00}"; }
            sb.Append($"\n  paint {paint}; lump jitter here {Jitter(hit.point.x, hit.point.z):+0.000;-0.000} m; voxel zone {(VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneKnown(ZoneSystem.GetZone(hit.point)))}");
            // 0.2.1086: what the ground's material actually binds for vanilla's cultivated look (_CultivatedNormal at 0.25 tiling).
            var rend = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
            var mat = rend != null ? rend.sharedMaterial : null;
            if (mat == null) sb.Append("\n  material: none on the hit collider");
            else
            {
                string Tex(string p) => mat.HasProperty(p) ? (mat.GetTexture(p) != null ? mat.GetTexture(p).name : "NULL") : "no property";
                sb.Append($"\n  material '{mat.name}' shader '{mat.shader?.name}': _CultivatedNormal {Tex("_CultivatedNormal")}, _CultivatedTex {Tex("_CultivatedTex")}");
            }
            string s = sb.ToString();
            FiresLogger.LogInfo(s);
            return s;
        }
    }
}
