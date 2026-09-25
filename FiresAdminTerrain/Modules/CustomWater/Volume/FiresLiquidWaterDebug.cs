using HarmonyLib;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-63 — per-instance diagnostics for spawned FiresLiquidWater
    // volumes. The user gets wet (LiquidSurface trigger works → the sim
    // runs) but sees NO water. This component logs every link of the
    // visual chain on a schedule (0.5s, 1s, 2s, 4s, 8s, 15s after spawn)
    // so one test run shows exactly where the chain breaks:
    //
    //   sim state    → m_haveHeights, max column in m_depths
    //   mesh         → MeshFilter.sharedMesh assigned? verts/tris/bounds
    //   renderer     → enabled, isVisible (frustum/cull verdict!),
    //                  material + SHADER (catches any swap by another
    //                  system), renderQueue, shadow mode
    //   camera       → Camera.main.depthTextureMode (Custom/Tar's
    //                  depth-fade samples _CameraDepthTexture — if depth
    //                  mode is off, tar-family shaders can render
    //                  fully transparent)
    //   network      → ZNetView valid/owner (sim only steps on owner)
    //
    // `va_water_liquid_debug` dumps the same info for EVERY LiquidVolume
    // in the scene on demand (vanilla tar pits included, as a known-good
    // baseline to diff against).
    public sealed class FiresLiquidWaterDebug : MonoBehaviour
    {
        private static readonly float[] SCHEDULE = { 0.5f, 1f, 2f, 4f, 8f, 15f };

        // Round-86 — all tracing is gated behind the "FiresLiquid Diagnostics"
        // config toggle (default off). The origin-destroy bug these hunted is
        // fixed, so on a healthy world they're just noise on every unload.
        internal static bool On =>
            FiresLiquidConfig.Diagnostics != null && FiresLiquidConfig.Diagnostics.Value;

        private float _age;
        private int _next;

        private void Update()
        {
            if (!On) return;
            if (_next >= SCHEDULE.Length) return;
            _age += Time.deltaTime;
            if (_age < SCHEDULE[_next]) return;
            _next++;

            FiresLogger.LogInfo(Describe(gameObject, $"T+{_age:F1}s"));
        }

        // Round-66 — the 07-10 test showed every pour destroyed in <0.5s
        // (zero T+ lines, dump found only the pool template). This logs
        // the moment of death with the pour's final state and age.
        private void OnDestroy()
        {
            if (!On) return;
            FiresLogger.LogWarning($"[LiquidWaterDebug] '{name}' DESTROYED at T+{_age:F2}s | {Describe(gameObject, "FINAL")}");
        }

        // ── Round-66 destroy TRACERS: REMOVED (Round-87) ─────────────────
        // These were Harmony prefixes on ZNetScene.OnZDODestroyed / ZNetView.ResetZDO / ZNetScene.Destroy /
        // ZDOMan.DestroyZDO that dumped a stack trace when a FiresLiquidWater pour died. The origin-destroy
        // bug they hunted is long fixed, and — being on the hottest object-lifecycle paths — they ran (and
        // captured Environment.StackTrace) on EVERY object destroy on the server, spamming the log and adding
        // real overhead during bulk removals (Upgrade World zone/location resets remove every ZDO in a zone).
        // Deleted outright rather than gated: the config gate still executed the prefix per-destroy, and a
        // process that cached the toggle at launch kept tracing after the file was set to false. The pour
        // lifecycle can still be inspected via the opt-in dump command + the water GO's own Update/OnDestroy
        // Describe logging below, neither of which touches the global destroy paths.

        public static string Describe(GameObject go, string tag)
        {
            var sb = new StringBuilder();
            sb.Append($"[LiquidWaterDebug] {tag} '{go.name}'");

            try
            {
                sb.Append($" | pos={go.transform.position} active={go.activeInHierarchy} layer={go.layer}");

                var lv = go.GetComponentInChildren<LiquidVolume>(true);
                if (lv != null)
                {
                    float maxDepth = 0f;
                    int wetCells = 0;
                    var depths = lv.m_depths;
                    if (depths != null)
                    {
                        for (int i = 0; i < depths.Count; i++)
                        {
                            if (depths[i] > 0.01f) wetCells++;
                            if (depths[i] > maxDepth) maxDepth = depths[i];
                        }
                    }
                    sb.Append($" | sim: type={lv.m_liquidType} haveHeights={lv.m_haveHeights} wetCells={wetCells} maxColumn={maxDepth:F2}");
                }
                else sb.Append(" | sim: NO LiquidVolume");

                var mf = go.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null)
                    sb.Append($" | mesh: verts={mf.sharedMesh.vertexCount} tris={mf.sharedMesh.triangles.Length / 3} bounds={mf.sharedMesh.bounds.size}");
                else
                    sb.Append($" | mesh: {(mf == null ? "NO FILTER" : "FILTER EMPTY (no mesh assigned)")}");

                var mr = go.GetComponentInChildren<MeshRenderer>(true);
                if (mr != null)
                {
                    var mat = mr.sharedMaterial;
                    sb.Append($" | renderer: enabled={mr.enabled} isVisible={mr.isVisible} " +
                        $"mat={(mat != null ? mat.name : "NULL")} shader={(mat != null && mat.shader != null ? mat.shader.name : "NULL")} " +
                        $"queue={(mat != null ? mat.renderQueue : -1)}");
                }
                else sb.Append(" | renderer: MISSING");

                // Round-83 — swim/trigger diagnostics. The LiquidSurface
                // trigger (layer-21 box) is what drives swimming; if it's
                // missing/disabled or the player never registers InWater,
                // that's the swim bug.
                var surf = go.GetComponentInChildren<LiquidSurface>(true);
                if (surf != null)
                {
                    var box = surf.GetComponent<Collider>();
                    sb.Append($" | swimTrig: LiquidSurface present, collider={(box != null ? $"{box.GetType().Name} enabled={box.enabled} trigger={box.isTrigger} layer={surf.gameObject.layer}" : "NONE")}");
                }
                else sb.Append(" | swimTrig: NO LiquidSurface");

                var p = Player.m_localPlayer;
                if (p != null)
                {
                    float liq = p.GetLiquidLevel();
                    float py = p.transform.position.y;
                    sb.Append($" | player: InWater={p.InWater()} liquidLvl={liq:F2} y={py:F2} below={(liq - py):F2} swimming={p.IsSwimming()}");
                }

                var nview = go.GetComponent<ZNetView>();
                if (nview != null)
                    sb.Append($" | net: valid={nview.IsValid()} owner={(nview.IsValid() && nview.IsOwner())}");
                else sb.Append(" | net: NO ZNetView");

                var cam = Camera.main;
                if (cam != null)
                {
                    float dist = Vector3.Distance(cam.transform.position, go.transform.position);
                    sb.Append($" | cam: dist={dist:F0}m depthTexMode={cam.depthTextureMode}");
                }
            }
            catch (System.Exception ex)
            {
                sb.Append($" | DESCRIBE THREW: {ex.Message}");
            }

            return sb.ToString();
        }

        // Round-65 — same render-chain report for a WaterMeshCombiner grid
        // mesh (drawn disks, springs, settled volumes). The round-63 dump
        // only covered LiquidVolume, so the user's invisible SPRING never
        // appeared in it at all — this closes that blind spot.
        public static string DescribeCombiner(WaterMeshCombiner c, string tag)
        {
            var sb = new StringBuilder();
            sb.Append($"[LiquidWaterDebug] {tag} COMBINER '{c.gameObject.name}'");

            try
            {
                sb.Append($" | pos={c.transform.position} active={c.gameObject.activeInHierarchy} layer={c.gameObject.layer}");
                sb.Append($" | type={c.CustomLiquidType} gelled={c.IsGelled} disks={(c.WaterDisks != null ? c.WaterDisks.Count : -1)}");

                var mf = c.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null)
                    sb.Append($" | mesh: verts={mf.sharedMesh.vertexCount} tris={mf.sharedMesh.triangles.Length / 3} bounds={mf.sharedMesh.bounds.size}");
                else
                    sb.Append($" | mesh: {(mf == null ? "NO FILTER" : "FILTER EMPTY (no mesh assigned)")}");

                var mr = c.GetComponentInChildren<MeshRenderer>(true);
                if (mr != null)
                {
                    var mat = mr.sharedMaterial;
                    sb.Append($" | renderer: enabled={mr.enabled} isVisible={mr.isVisible} " +
                        $"mat={(mat != null ? mat.name : "NULL")} shader={(mat != null && mat.shader != null ? mat.shader.name : "NULL")} " +
                        $"queue={(mat != null ? mat.renderQueue : -1)} " +
                        $"wbounds=c{mr.bounds.center} sz={mr.bounds.size}");
                }
                else sb.Append(" | renderer: MISSING");

                var cam = Camera.main;
                if (cam != null)
                {
                    float dist = Vector3.Distance(cam.transform.position, c.transform.position);
                    sb.Append($" | cam: dist={dist:F0}m");
                }
            }
            catch (System.Exception ex)
            {
                sb.Append($" | DESCRIBE THREW: {ex.Message}");
            }

            return sb.ToString();
        }

        // Console dump of every LiquidVolume in the scene — spawned pours
        // AND vanilla tar pits (the known-good baseline to diff against) —
        // PLUS every WaterMeshCombiner grid mesh (drawn water / springs /
        // settled volumes), so one command covers both water systems.
        public static void RegisterDumpCommand()
        {
            new Terminal.ConsoleCommand("va_water_liquid_debug",
                "Dump the render-chain state of every LiquidVolume AND every custom water combiner mesh in the scene.",
                args =>
                {
                    var all = Object.FindObjectsByType<LiquidVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    var combiners = WaterMeshCombiner.AllCombiners;
                    args.Context.AddString($"va_water_liquid_debug: {all.Length} LiquidVolume + {combiners.Count} combiner instance(s) — details in log.");

                    FiresLogger.LogInfo($"[LiquidWaterDebug] ===== scene dump: {all.Length} LiquidVolume instance(s) =====");
                    foreach (var lv in all)
                    {
                        if (lv == null) continue;
                        FiresLogger.LogInfo(Describe(lv.transform.root.gameObject, "DUMP"));
                    }

                    FiresLogger.LogInfo($"[LiquidWaterDebug] ===== combiner dump: {combiners.Count} WaterMeshCombiner instance(s) (mat debug mode={WaterMeshCombiner.DebugMaterialMode}) =====");
                    foreach (var c in combiners)
                    {
                        if (c == null) continue;
                        FiresLogger.LogInfo(DescribeCombiner(c, "DUMP"));
                    }
                }, isCheat: true);
        }
    }
}
