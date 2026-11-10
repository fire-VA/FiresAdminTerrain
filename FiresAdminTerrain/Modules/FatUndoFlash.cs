using System;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain
{
    // ── Undo-area flash (0.2.1057; Fire's Voxheim pick 6, our own code) ─────────────────────────────────────────────────────
    // When Numpad 7 / 9 applies a FAT terrain step (a voxel stroke, a water tool's vanilla-ground cut), the box it changes
    // flashes for FlashSeconds: the gizmo's green dashed lines round the step's bounds, so it is plain what the undo touched.
    // Steps that know their bounds record through FatUndo.Record(what, bounds, undo, redo); the others are unchanged.
    public static partial class FatUndo
    {
        public static void Record(string what, Bounds area, Func<string> undo, Func<string> redo)
            => Record(what, () => { UndoFlash.Show(area, "undo " + what); return undo(); },
                            () => { UndoFlash.Show(area, "redo " + what); return redo(); });
    }

    internal sealed class UndoFlash : MonoBehaviour
    {
        private const float FlashSeconds = 0.6f, LineWidth = 0.12f;
        private static readonly Color Line = new Color(0.30f, 0.95f, 0.35f, 1f);
        private static UndoFlash s_view;
        private LineRenderer _box;
        private float _until;

        internal static void Show(Bounds b, string what)
        {
            try
            {
                if (s_view == null)
                {
                    var go = new GameObject("FAT_UndoFlash");
                    DontDestroyOnLoad(go);
                    s_view = go.AddComponent<UndoFlash>();
                    var lineGo = new GameObject("box");
                    lineGo.transform.SetParent(go.transform, false);
                    var lr = lineGo.AddComponent<LineRenderer>();
                    lr.sharedMaterial = VoxelBrushDrag.SharedLineMat();
                    lr.useWorldSpace = true;
                    lr.startWidth = LineWidth; lr.endWidth = LineWidth;
                    lr.startColor = Line; lr.endColor = Line;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    s_view._box = lr;
                }
                Vector3 n = b.min, x = b.max;
                // One line through the box's 12 edges (some walked twice).
                var pts = new[]
                {
                    new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, n.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, n.y, n.z),
                    new Vector3(n.x, x.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(x.x, x.y, x.z), new Vector3(n.x, x.y, x.z), new Vector3(n.x, x.y, n.z),
                    new Vector3(n.x, x.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z),
                    new Vector3(x.x, x.y, n.z), new Vector3(x.x, n.y, n.z),
                };
                s_view._box.positionCount = pts.Length;
                s_view._box.SetPositions(pts);
                s_view._until = Time.unscaledTime + FlashSeconds;
                s_view.gameObject.SetActive(true);
                FiresLogger.LogDebug($"[FiresVoxel] undo flash: bounds ({n.x:0.0},{n.y:0.0},{n.z:0.0})-({x.x:0.0},{x.y:0.0},{x.z:0.0}) for {what}");
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[FiresVoxel] undo flash skipped: {ex.Message}"); }
        }

        private void Update()
        {
            if (Time.unscaledTime > _until) gameObject.SetActive(false);
        }
    }
}
