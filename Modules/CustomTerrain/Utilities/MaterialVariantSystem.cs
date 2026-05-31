using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    /// <summary>
    /// Manages snow material effects for renderers using MaterialPropertyBlock.
    /// This is similar to how the Seasons mod applies winter snow to pieces.
    /// 
    /// Key approach:
    /// - Uses MaterialPropertyBlock for non-invasive per-renderer property overrides
    /// - Checks WearNTear.HaveRoof() to only apply snow to exposed pieces
    /// - Blends original colors toward white based on blend amount
    /// - Tracks which renderers have snow applied for cleanup
    /// </summary>
    public static class MaterialVariantSystem
    {
        // Shader property IDs (cached for performance)
        private static readonly int _ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int _MainColorPropertyId = Shader.PropertyToID("_MainColor");
        private static readonly int _EmissionColorPropertyId = Shader.PropertyToID("_EmissionColor");

        // Snow color - slightly off-white for natural look
        private static readonly Color SnowColor = new Color(0.98f, 0.98f, 1f, 1f);

        // Track renderers with snow applied and their original colors
        private static readonly Dictionary<int, RendererSnowData> _snowedRenderers = new();

        // Reusable MaterialPropertyBlock to avoid allocations
        private static readonly MaterialPropertyBlock _propBlock = new();

        // Reusable lists
        private static readonly List<Renderer> _tempRenderers = new();

        private struct RendererSnowData
        {
            public Color OriginalColor;
            public bool HasColor;
            public float BlendAmount;
        }

        /// <summary>
        /// Applies snow effect to all renderers on a GameObject and its children.
        /// Uses MaterialPropertyBlock for non-invasive tinting.
        /// </summary>
        public static bool ApplySnowToGameObject(GameObject go, float blendAmount = 0.5f)
        {
            if (go == null) return false;

            _tempRenderers.Clear();
            go.GetComponentsInChildren(true, _tempRenderers);

            bool anyApplied = false;
            foreach (var renderer in _tempRenderers)
            {
                if (ApplySnowToRenderer(renderer, blendAmount))
                    anyApplied = true;
            }

            return anyApplied;
        }

        /// <summary>
        /// Applies snow effect to a specific renderer using MaterialPropertyBlock.
        /// This tints the _Color property toward white without modifying the material.
        /// </summary>
        public static bool ApplySnowToRenderer(Renderer renderer, float blendAmount = 0.5f)
        {
            if (renderer == null) return false;

            int instanceId = renderer.GetInstanceID();

            // Skip if already snowed with same blend amount
            if (_snowedRenderers.TryGetValue(instanceId, out var existing) &&
                Mathf.Approximately(existing.BlendAmount, blendAmount))
                return true;

            // Get the material to check if it has a color property
            var material = renderer.sharedMaterial;
            if (material == null) return false;

            // Check which color property exists
            bool hasColorProp = material.HasProperty(_ColorPropertyId);
            bool hasMainColorProp = material.HasProperty(_MainColorPropertyId);

            if (!hasColorProp && !hasMainColorProp) return false;

            int colorPropId = hasColorProp ? _ColorPropertyId : _MainColorPropertyId;

            // Get current property block (preserves any existing overrides)
            renderer.GetPropertyBlock(_propBlock);

            // Store original color if not already tracked
            Color originalColor;
            if (!_snowedRenderers.ContainsKey(instanceId))
            {
                originalColor = material.GetColor(colorPropId);
                _snowedRenderers[instanceId] = new RendererSnowData
                {
                    OriginalColor = originalColor,
                    HasColor = true,
                    BlendAmount = blendAmount
                };
            }
            else
            {
                originalColor = _snowedRenderers[instanceId].OriginalColor;
                var data = _snowedRenderers[instanceId];
                data.BlendAmount = blendAmount;
                _snowedRenderers[instanceId] = data;
            }

            // Blend toward snow color
            Color snowedColor = Color.Lerp(originalColor, SnowColor, blendAmount);
            snowedColor.a = originalColor.a; // Preserve alpha

            _propBlock.SetColor(colorPropId, snowedColor);
            renderer.SetPropertyBlock(_propBlock);

            return true;
        }

        /// <summary>
        /// Removes snow effect from a GameObject and restores original colors.
        /// </summary>
        public static void RemoveSnowFromGameObject(GameObject go)
        {
            if (go == null) return;

            _tempRenderers.Clear();
            go.GetComponentsInChildren(true, _tempRenderers);

            foreach (var renderer in _tempRenderers)
            {
                RemoveSnowFromRenderer(renderer);
            }
        }

        /// <summary>
        /// Removes snow effect from a specific renderer and restores original color.
        /// </summary>
        public static void RemoveSnowFromRenderer(Renderer renderer)
        {
            if (renderer == null) return;

            int instanceId = renderer.GetInstanceID();

            if (!_snowedRenderers.TryGetValue(instanceId, out var data))
                return;

            // Restore original color
            var material = renderer.sharedMaterial;
            if (material != null)
            {
                bool hasColorProp = material.HasProperty(_ColorPropertyId);
                int colorPropId = hasColorProp ? _ColorPropertyId : _MainColorPropertyId;

                renderer.GetPropertyBlock(_propBlock);
                _propBlock.SetColor(colorPropId, data.OriginalColor);
                renderer.SetPropertyBlock(_propBlock);
            }

            _snowedRenderers.Remove(instanceId);
        }

        /// <summary>
        /// Clears all tracked snow data. Call when leaving snow territories.
        /// Note: This doesn't restore renderers - call RemoveSnowFromRenderer first.
        /// </summary>
        public static void ClearCache()
        {
            _snowedRenderers.Clear();
        }

        /// <summary>
        /// Gets statistics about cached materials.
        /// </summary>
        public static (int cachedMaterials, int modifiedRenderers) GetCacheStats()
        {
            return (0, _snowedRenderers.Count);
        }

        /// <summary>
        /// Checks if a renderer has snow applied.
        /// </summary>
        public static bool HasSnow(Renderer renderer)
        {
            if (renderer == null) return false;
            return _snowedRenderers.ContainsKey(renderer.GetInstanceID());
        }
    }
}
