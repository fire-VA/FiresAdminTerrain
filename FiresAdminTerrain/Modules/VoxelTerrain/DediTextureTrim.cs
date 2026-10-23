using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// A -nographics dedicated server held ~12,000 textures (663 MB). A headless server draws nothing. A texture that
    /// only a MATERIAL points at, and that is not CPU-readable, can never be used there: no GPU to sample it, and GetPixels refuses a
    /// non-readable texture. Opt-in (default OFF), dedicated + Null graphics device only, once per world load, a minute after ZNetScene is up.
    /// Kept: readable textures, sprite textures (icons / atlases), render textures and anything no material references. Materials keep
    /// their (now missing) texture slot; meshes and colliders are untouched.
    /// </summary>
    internal static class DediTextureTrim
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile cfg, string section)
        {
            Enabled = cfg.Bind(section, "Dedicated server releases material textures", false,
                "Headless dedicated server only (no effect on clients or listen servers): a minute after the world loads, free the textures "
                + "that only materials use and nothing can read (a server draws nothing). Saves several hundred MB on a modded server. "
                + "Sprites/icons, readable textures, meshes and colliders are kept.");
        }

        internal static IEnumerator Run()
        {
            if (!Application.isBatchMode || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) yield break;
            object lastScene = null;
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                var scene = ZNetScene.instance;
                if (scene == null || ReferenceEquals(scene, lastScene)) continue;
                lastScene = scene;
                yield return new WaitForSecondsRealtime(60f);
                if (Enabled == null || !Enabled.Value || ZNetScene.instance != scene) continue;
                try { Trim(); }
                catch (System.Exception e) { FiresLogger.LogWarning($"[FiresVoxel] dedi texture release stopped: {e.GetType().Name}: {e.Message}"); }
            }
        }

        private static void Trim()
        {
            var keep = new HashSet<Texture>();
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && s.texture != null) keep.Add(s.texture);

            var drop = new HashSet<Texture2D>();
            var ids = new List<int>();
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                ids.Clear();
                m.GetTexturePropertyNameIDs(ids);
                for (int i = 0; i < ids.Count; i++)
                {
                    if (!(m.GetTexture(ids[i]) is Texture2D t) || t == null) continue;
                    if (t.isReadable || keep.Contains(t)) continue;
                    drop.Add(t);
                }
            }

            long bytes = 0;
            int n = 0;
            foreach (var t in drop)
            {
                if (t == null) continue;
                bytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t);
                Object.DestroyImmediate(t, true);
                n++;
            }
            FiresLogger.LogDebug($"[FiresVoxel] dedi texture release: {n} material-only texture(s), {bytes >> 20} MB freed; kept {keep.Count} sprite texture(s) and every readable one");
        }
    }
}
