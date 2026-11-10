using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Vramp (0.2.1057; Fire's Voxheim pick 6, our own code): a straight ramp cut and filled from the click ──────────────
    // A click lays a ramp from the ground under the crosshair, LENGTH RampLengthPerRadius x the radius long and the radius
    // wide, rising by the tool's offset (Shift+wheel / PgUp/PgDn; negative = down) from its near end to its far end. It runs
    // along the view, snapped to the nearest quarter, turned by Q/E in 90 degree steps. Each step along it is one Vlevel dab
    // (LevelDab: cut and fill to the ramp's height there, kept to the clicked layer), so it is one undo step like any stroke.
    // Q and E are vanilla's AutoRun and Use: while Vramp is the selected piece they turn the ramp and vanilla does not see
    // them (Patch_ZInput_RampKeys); every other tool and every other moment, vanilla's own.
    public sealed partial class VoxelBrushDrag
    {
        private const float RampLengthPerRadius = 4f, RampMinWidth = 1.5f;
        internal const string RampPiece = "Vramp";
        private static int s_rampTurns;

        internal static bool RampSelected()
        {
            var piece = Player.m_localPlayer?.m_buildPieces?.GetSelectedPiece();
            return piece != null && piece.m_name == RampPiece && Player.m_localPlayer.InPlaceMode();
        }

        // The ramp's run: along the view, snapped to a quarter, plus the Q/E turns.
        private static Vector3 RampDir(Vector3 view)
        {
            Vector3 f = new Vector3(view.x, 0f, view.z);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            float yaw = Mathf.Round(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg / 90f) * 90f + s_rampTurns * 90f;
            return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        }

        private sealed class RampDriver : IVToolDriver
        {
            public float DrawSpeed => 1f;
            public bool ClickOnly => true;
            public float DefaultOffset => 2f;
            public float MinOffset => float.MinValue;
            public string OffsetLabel => "rise";

            public Ghost Preview(in StrokeInput s)
            {
                if (FatInput.KeyDown(KeyCode.Q) || FatInput.KeyDown(KeyCode.E))
                {
                    s_rampTurns = (s_rampTurns + (FatInput.KeyDown(KeyCode.E) ? 1 : 3)) % 4;
                    Vector3 d = RampDir(s.View);
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"Vramp turned: runs {Compass(d)}", 0, null, false);
                }
                return new Ghost { Shape = GhostShape.None };
            }

            public void Begin(in StrokeInput s)
            {
                Vector3 dir = RampDir(s.View);
                float width = Mathf.Max(RampMinWidth, s.Radius), len = Mathf.Max(4f, s.Radius * RampLengthPerRadius);
                float dabR = width * 0.5f, step = Mathf.Max(0.5f, dabR * 0.5f);
                Vector3 start = s.HitGround;
                int dabs = 0;
                for (float t = 0f; t <= len + 0.01f; t += step)
                {
                    float y = start.y + s.Offset * (t / len);
                    Vector3 at = new Vector3(start.x + dir.x * t, y, start.z + dir.z * t);
                    LevelDab(at, dabR, y);
                    dabs++;
                }
                FiresLogger.LogDebug($"[FiresVoxel] ramp: from ({F(start.x)},{F(start.y)},{F(start.z)}) width {F(width)}, {dabs} dab(s) (len {F(len)}, rise {F(s.Offset)}, dir {Compass(dir)})");
            }

            public void Dab(in StrokeInput s) { }
            public void End(in StrokeInput s) { }

            public void DrawExtras(in StrokeInput s)
            {
                Vector3 dir = RampDir(s.View);
                float width = Mathf.Max(RampMinWidth, s.Radius), len = Mathf.Max(4f, s.Radius * RampLengthPerRadius);
                RampView.Draw(SharedLineMat(), s.HitGround, dir, width, len, s.Offset);
            }
        }

        private static string Compass(Vector3 d)
            => Mathf.Abs(d.x) > Mathf.Abs(d.z) ? (d.x > 0f ? "east" : "west") : (d.z > 0f ? "north" : "south");

        // Q / E while Vramp is held turn the ramp; vanilla's AutoRun (Q) and Use (E) do not fire then.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown), typeof(string))]
        private static class Patch_ZInput_RampKeys
        {
            private static void Postfix(string name, ref bool __result)
            {
                if (__result && (name == "AutoRun" || name == "Use") && RampSelected()) __result = false;
            }
        }

        // PlayerController reads AutoRun with GetButton (1.0.15 PlayerController.cs:125), so that overload is blocked too.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), typeof(string))]
        private static class Patch_ZInput_RampKeysHeld
        {
            private static void Postfix(string name, ref bool __result)
            {
                if (__result && (name == "AutoRun" || name == "Use") && RampSelected()) __result = false;
            }
        }
    }

    // The ramp's footprint (its outline at the ramp's own heights) and an arrow up its run; hidden when a frame does not draw it.
    internal sealed class RampView : MonoBehaviour
    {
        private const float LineWidth = 0.1f;
        private static readonly Color Line = new Color(0.30f, 0.95f, 0.35f, 1f);
        private static RampView s_view;
        private LineRenderer _outline, _arrow;
        private int _drawnFrame;

        internal static void Draw(Material mat, Vector3 start, Vector3 dir, float width, float len, float rise)
        {
            if (s_view == null)
            {
                var go = new GameObject("FAT_RampView");
                DontDestroyOnLoad(go);
                s_view = go.AddComponent<RampView>();
                s_view._outline = NewLine(go.transform, mat, 5);
                s_view._arrow = NewLine(go.transform, mat, 5);
            }
            if (!s_view.gameObject.activeSelf) s_view.gameObject.SetActive(true);
            s_view._drawnFrame = Time.frameCount;
            Vector3 side = Vector3.Cross(Vector3.up, dir).normalized * (width * 0.5f);
            Vector3 lift = Vector3.up * 0.2f, end = start + dir * len + Vector3.up * rise;
            var o = s_view._outline;
            o.SetPosition(0, start - side + lift);
            o.SetPosition(1, end - side + lift);
            o.SetPosition(2, end + side + lift);
            o.SetPosition(3, start + side + lift);
            o.SetPosition(4, start - side + lift);
            var a = s_view._arrow;
            Vector3 tip = end + lift, back = tip - (end - start).normalized * Mathf.Min(2f, len * 0.25f);
            a.SetPosition(0, start + lift);
            a.SetPosition(1, tip);
            a.SetPosition(2, back + side * 0.5f);
            a.SetPosition(3, tip);
            a.SetPosition(4, back - side * 0.5f);
        }

        private static LineRenderer NewLine(Transform parent, Material mat, int count)
        {
            var go = new GameObject("line");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.positionCount = count;
            lr.startWidth = LineWidth; lr.endWidth = LineWidth;
            lr.startColor = Line; lr.endColor = Line;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private void LateUpdate()
        {
            if (Time.frameCount > _drawnFrame + 1) gameObject.SetActive(false);
        }
    }
}
