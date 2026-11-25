using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Tools
{
    // ── 0.2.1491: the player's Vfall is ONLY a waterfall visual ─────────────────────────────────────────────────────────
    // Fire 11-17, verbatim: "waterfall tool should not dig anything. its meant for us to be able to place JUST A WATERFALL wherever we
    // want, wether that be at the edge of a pond or in the middle of a river it doesnt matter, were just placing the waterfall visual,
    // and the waterfalls we place with this tool look like shit, they need to look like the waterfalls from our skylands".
    //
    // Until 0.2.1490 a Vfall click cut a spillway notch (VoxelWorld.ApplyBrush Carve) because the notch's chunk anchor was where the fall
    // was saved (vv_falls); the notch also marked the zone brushed, so the ground there became voxel. Now each placed fall is its own
    // small saved object, "fires_vfall": an invisible ZNetView cloned from piece_dirtfloor_vad exactly as VoxelAnchorPrefab does
    // (named-only registration, disable-not-destroy, template-inactive spawn). Its ZDO holds the fall (lip, facing, width, landing,
    // onto ground); vanilla saves, syncs and streams it like any object. Nothing touches the ground.
    //
    // Drawing: graphics peers only. On a dedicated server (GraphicsDeviceType.Null) the component builds nothing; the ZDO is the whole
    // fall there. The curtain is ToolFalls.AppendSkylandCurtain (the Skylands falls' own arguments), with the same material, mist and
    // sound as the Skylands falls (FiresWaterfalls.Materials, FiresWaterfallMist.SetEmitters, FiresWaterfalls.SetSounds).
    //
    // Remove (middle mouse) and Vmop destroy the falls in the ring (ToolFalls.RemoveNear calls RemoveNear here); Numpad 7/9 undo / redo
    // a placement or a removal through FatUndo.Record (Core's one history).
    internal static class PlacedFalls
    {
        internal const string PrefabName = "fires_vfall";
        internal static readonly int PrefabHash = PrefabName.GetStableHashCode();
        private const string SourcePrefabName = "piece_dirtfloor_vad";

        internal const string ZdoVer = "vf_ver", ZdoLip = "vf_lip", ZdoOut = "vf_out", ZdoWidth = "vf_w", ZdoLand = "vf_land", ZdoOnto = "vf_onto";
        private const int FormatVersion = 1;

        private static GameObject s_prefab;
        private static GameObject s_inactiveRoot;
        private static bool s_registered;

        // FatVanillaHooks' ZNetScene.Awake anchor steps, beside VoxelAnchorPrefab.Ensure. Idempotent; re-inserts after a relog.
        internal static bool Ensure()
        {
            if (ZNetScene.instance == null) return false;
            if (s_registered && s_prefab != null)
            {
                if (ZNetScene.instance.m_namedPrefabs != null
                    && ZNetScene.instance.m_namedPrefabs.TryGetValue(PrefabHash, out var inScene) && inScene == s_prefab)
                    return true;
                ZNetScene.instance.m_namedPrefabs[PrefabHash] = s_prefab;   // session re-insert
                FiresLogger.LogInfo($"[Vfall] re-inserted '{PrefabName}' into the new ZNetScene dict.");
                return true;
            }

            GameObject src = null;
            try
            {
                var bundle = VerdantsAscent.FiresAdminTerrain.assetBundle;
                src = bundle != null ? bundle.LoadAsset<GameObject>(SourcePrefabName) : null;
            }
            catch { }
            if (src == null)
            {
                FiresLogger.LogError($"[Vfall] source '{SourcePrefabName}' missing from the FAT bundle: placed waterfalls cannot be saved.");
                return false;
            }
            if (s_inactiveRoot == null)
            {
                s_inactiveRoot = new GameObject("FiresVfallPool");
                s_inactiveRoot.SetActive(false);
                Object.DontDestroyOnLoad(s_inactiveRoot);
            }
            var clone = Object.Instantiate(src, s_inactiveRoot.transform);
            clone.name = PrefabName;
            try { _ = clone.name; }
            catch
            {
                FiresLogger.LogError("[Vfall] prefab clone failed name validation; not registering.");
                return false;
            }
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in clone.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var a in clone.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(mb is ZNetView)) mb.enabled = false;
            var nv = clone.GetComponent<ZNetView>() ?? clone.AddComponent<ZNetView>();
            nv.m_persistent = true;
            clone.AddComponent<PlacedFall>();

            ZNetScene.instance.m_namedPrefabs[PrefabHash] = clone;
            s_prefab = clone;
            s_registered = true;
            FiresLogger.LogInfo($"[Vfall] registered '{PrefabName}' (named-only, from {SourcePrefabName}): a placed waterfall is its own saved object (0.2.1491).");
            return true;
        }

        // The template-inactive spawn (VoxelAnchorPrefab.Spawn): activating at the origin would register the ZDO in zone (0,0).
        private static GameObject Spawn(Vector3 pos)
        {
            if (!Ensure()) return null;
            GameObject go;
            bool wasActive = s_prefab.activeSelf;
            s_prefab.SetActive(false);
            try
            {
                go = Object.Instantiate(s_prefab, pos, Quaternion.identity);
                go.name = PrefabName;
            }
            finally { s_prefab.SetActive(wasActive); }
            go.SetActive(true);
            return go;
        }

        private static string F(string format, params object[] args)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

        private static ZDOID Create(ToolFalls.Fall fall)
        {
            var go = Spawn(fall.Lip);
            var nv = go != null ? go.GetComponent<ZNetView>() : null;
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (zdo == null)
            {
                if (go != null) Object.Destroy(go);
                return ZDOID.None;
            }
            Write(zdo, fall);
            go.GetComponent<PlacedFall>()?.Draw();   // drawn now (its Start would otherwise read the ZDO before these values were set)
            return zdo.m_uid;
        }

        private static void Write(ZDO zdo, ToolFalls.Fall f)
        {
            zdo.Set(ZdoVer, FormatVersion);
            zdo.Set(ZdoLip, f.Lip);
            zdo.Set(ZdoOut, new Vector3(f.Out.x, 0f, f.Out.y));
            zdo.Set(ZdoWidth, f.Width);
            zdo.Set(ZdoLand, f.LandY);
            zdo.Set(ZdoOnto, f.OntoGround);
        }

        internal static bool Read(ZDO zdo, out ToolFalls.Fall f)
        {
            f = default;
            if (zdo == null || zdo.GetInt(ZdoVer, 0) < 1) return false;
            var o = zdo.GetVec3(ZdoOut, Vector3.forward);
            f = new ToolFalls.Fall
            {
                Lip = zdo.GetVec3(ZdoLip, Vector3.zero),
                Out = new Vector2(o.x, o.z),
                Width = zdo.GetFloat(ZdoWidth, 2f),
                LandY = zdo.GetFloat(ZdoLand, 0f),
                OntoGround = zdo.GetBool(ZdoOnto, true),
                Straight = true,
            };
            return true;
        }

        // The player's Vfall click (ToolFalls.Place, after PlanAt). The proof line names the save path and the untouched ground.
        internal static string Place(ToolFalls.Fall fall)
        {
            var id = Create(fall);
            if (id == ZDOID.None)
                return "[FiresWater] Vfall refused: the fires_vfall object could not be created (see the [Vfall] lines)";
            var held = new[] { id };
            var data = new[] { fall };
            FatUndo.Record("a Vfall waterfall", () => Destroy(held, "undo"), () => { held[0] = Create(data[0]); return held[0] != ZDOID.None ? "the waterfall placed again" : "the waterfall could not be placed again"; });
            string line = F("[Vfall] placed: top ({0:0.0},{1:0.0},{2:0.0}) facing ({3:0.00},{4:0.00}) width {5:0.0} m, drop {6:0.0} m onto {7} at y {8:0.0}; saved as fires_vfall {9}, ground untouched (no voxel brush, no zone mark; 0.2.1491)",
                fall.Lip.x, fall.Lip.y, fall.Lip.z, fall.Out.x, fall.Out.y, fall.Width, fall.Lip.y - fall.LandY, fall.OntoGround ? "ground" : "water", fall.LandY, id);
            FiresLogger.LogInfo(line);
            return "[FiresWater] Vfall placed: " + line;
        }

        private static string Destroy(ZDOID[] ids, string why)
        {
            int gone = 0;
            for (int i = 0; i < ids.Length; i++)
                if (DestroyOne(ids[i])) gone++;
            return F("{0} of {1} waterfall(s) removed ({2})", gone, ids.Length, why);
        }

        private static bool DestroyOne(ZDOID id)
        {
            if (id == ZDOID.None || ZNetScene.instance == null) return false;
            var go = ZNetScene.instance.FindInstance(id);
            if (go == null) return false;
            var nv = go.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return false;
            nv.ClaimOwnership();
            if (!nv.IsOwner())
            {
                FiresLogger.LogWarning($"[Vfall] fires_vfall {id} not removed: this peer could not take ownership of it.");
                return false;
            }
            ZNetScene.instance.Destroy(go);
            return true;
        }

        // The falls whose top is within r of the cursor (Remove, Vmop). Returns how many went; one undo step brings them back.
        internal static int RemoveNear(Vector3 cursor, float r)
        {
            var hits = new List<PlacedFall>();
            foreach (var pf in PlacedFall.Live)
                if (pf != null && pf.HasFall && (pf.Fall.Lip - cursor).sqrMagnitude <= r * r) hits.Add(pf);
            if (hits.Count == 0) return 0;
            var data = new List<ToolFalls.Fall>();
            int gone = 0;
            foreach (var pf in hits)
            {
                var f = pf.Fall;
                var nv = pf.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !DestroyOne(nv.GetZDO().m_uid)) continue;
                data.Add(f);
                gone++;
            }
            if (gone > 0)
            {
                var back = new ZDOID[data.Count];
                FatUndo.Record($"a Vfall removal ({gone} waterfall(s))",
                    () => { int made = 0; for (int i = 0; i < data.Count; i++) { back[i] = Create(data[i]); if (back[i] != ZDOID.None) made++; } return $"{made} of {data.Count} waterfall(s) placed back"; },
                    () => Destroy(back, "redo"));
                FiresLogger.LogInfo(F("[Vfall] removed {0} fires_vfall waterfall(s) within {1:0.0} m of ({2:0.0},{3:0.0},{4:0.0})", gone, r, cursor.x, cursor.y, cursor.z));
            }
            return gone;
        }

        internal static int CountNear(Vector3 at, float radius)
        {
            int n = 0;
            foreach (var pf in PlacedFall.Live)
                if (pf != null && pf.HasFall && (pf.Fall.Lip - at).sqrMagnitude <= radius * radius) n++;
            return n;
        }
    }

    // One placed waterfall. On a graphics peer it draws its curtain, mist and sound; on a dedicated server it is the ZDO only.
    internal sealed class PlacedFall : MonoBehaviour
    {
        internal static readonly HashSet<PlacedFall> Live = new HashSet<PlacedFall>();

        internal ToolFalls.Fall Fall;
        internal bool HasFall;
        private ZNetView m_nview;
        private GameObject m_visual;
        private Mesh m_mesh;
        private static readonly List<FiresWaterfallMist.Site> s_sites = new List<FiresWaterfallMist.Site>(1);
        private static readonly List<Vector3> s_sounds = new List<Vector3>(1);

        private static bool Headless => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            Live.Add(this);
        }

        // A fall loaded from the save or arriving from another peer: its ZDO already holds the fall. A fall placed here is drawn by
        // PlacedFalls.Create once its values are written (Draw is idempotent).
        private void Start() => Draw();

        internal void Draw()
        {
            if (m_nview == null || !m_nview.IsValid()) return;
            HasFall = PlacedFalls.Read(m_nview.GetZDO(), out Fall);
            if (!HasFall || Headless) return;   // dedicated server: the ZDO is the fall; nothing is drawn
            Clear();
            if (FiresWaterfalls.Material == null)
            {
                FiresLogger.LogWarning("[Vfall] a placed waterfall was not drawn: the waterfall material is not loaded.");
                return;
            }
            FiresWaterfalls.BeginMesh();
            if (!ToolFalls.AppendSkylandCurtain(Fall, out Vector3 sound)) return;
            s_sites.Clear(); s_sites.Add(FiresWaterfalls.LastSite);
            s_sounds.Clear(); s_sounds.Add(sound);
            // The curtain's vertices are in world space (as the Skylands falls' are), so its object sits at the world origin.
            m_visual = new GameObject("fires_vfall_curtain");
            m_visual.layer = 0;
            m_visual.transform.SetParent(transform, false);
            m_visual.transform.position = Vector3.zero;
            m_visual.transform.rotation = Quaternion.identity;
            m_mesh = new Mesh { name = "vfall_curtain" };
            m_visual.AddComponent<MeshFilter>().sharedMesh = m_mesh;
            var renderer = m_visual.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterials = FiresWaterfalls.Materials;
            VoxelRenderGroup.Apply(renderer);
            int verts = FiresWaterfalls.EndMesh(m_mesh);
            FiresWaterfalls.SetSounds(m_visual.transform, s_sounds);
            FiresWaterfallMist.SetEmitters(m_visual.transform, s_sites);
            FiresLogger.LogDebug($"[Vfall] fires_vfall {m_nview.GetZDO().m_uid} drawn: {verts} vertices (Skylands curtain), top ({Fall.Lip.x:0.0},{Fall.Lip.y:0.0},{Fall.Lip.z:0.0}).");
        }

        private void Clear()
        {
            if (m_visual != null) Object.Destroy(m_visual);
            if (m_mesh != null) Object.Destroy(m_mesh);
            m_visual = null; m_mesh = null;
        }

        private void OnDestroy()
        {
            Live.Remove(this);
            Clear();
        }
    }
}
