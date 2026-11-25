using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Softness / Strength for the V-tool shape brushes (0.2.1454, [ghost]; lead 11-17 Voxheim diff row P1, agreed with [worldgen]) ──
    // The V-tool brushes were hard-edged only: a carve was exactly the shape cut out, a fill exactly the shape added
    // (VoxelChunk.ApplyShape: carve = min(density, -inside), fill = max(density, inside)). Two values, our own design:
    //   Softness (metres, 0 = today's hard edge): where the shape meets the existing ground, the two blend over this width instead
    //     of meeting at a sharp crease (a smooth minimum / maximum), so a carved tunnel mouth or a filled mound rounds into the land.
    //   Strength (100 % = today): each dab moves the ground only this share of the way to the shape, so repeated dabs build up and
    //     converge (a dab always moves at least one density step while it is short of the shape).
    // Only Vcarve, Vfill, Vcube and Vsphere, only inside a V-tool stroke (VoxelWorld.VToolScope): the pickaxe, the hoe, water and
    // river tools, Vlevel / Vskin / Vramp and every console command keep the exact old maths. Defaults change nothing.
    // Keys (Core's wheel rule has no Ctrl+wheel, so keys): Numpad + / - = softness, Numpad / = strength 100 -> 50 -> 25 %.
    internal static class VoxelBrushSoft
    {
        internal static ConfigEntry<KeyCode> SofterKey, HarderKey, StrengthKey;
        internal static ConfigEntry<bool> TrueSmooth;   // 0.2.1455 (P2)
        internal static bool TrueSmoothOn => TrueSmooth == null || TrueSmooth.Value;
        // 0.2.1491: "Vfall digs a landing pool" removed (Fire: "waterfall tool should not dig anything"); a saved value stays in the cfg, unread.

        // 0.2.1455 ([ghost], Voxheim diff P4): Vcube turns with Q / E in 15 degree steps (the ghost turns with it; Vramp's Q / E stay its
        // own 90 degree turns), and the Vramp angle key steps the ramp's slope (VoxelRamp).
        internal const string CubePiece = "Vcube";
        private const float CubeTurnStep = 15f;
        private static float s_cubeYaw;
        internal static ConfigEntry<KeyCode> RampAngleKey;
        internal static float CubeYaw => s_cubeYaw;
        /// <summary>The cube's turn for a shape op right now: only inside a Vcube stroke (else 0, the old axis-aligned cube).</summary>
        internal static float CubeYawNow() => VoxelWorld.VToolScope > 0 && SelectedPiece == CubePiece ? s_cubeYaw : 0f;

        /// <summary>0.2.1455: how far past radius + 1 a shape op reaches right now (the soft blend; a turned cube's corners, up to
        /// radius x 0.414). VoxelWorld.ApplyBrush widens its chunk range AND FatUndo's capture by it, so every chunk the op changes is
        /// visited (shared border samples stay equal on both sides: no seam) and undoable. 0 = the old range exactly.</summary>
        internal static float ExtraReach(VoxelBrushShape shape, float radius, VoxelBrushMode mode)
        {
            if (mode == VoxelBrushMode.Restore) return 0f;
            float extra = Active(out float softK, out _) ? softK : 0f;
            if (shape == VoxelBrushShape.Cube && CubeYawNow() != 0f) extra += radius * 0.4143f;   // sqrt(2) - 1
            return extra;
        }
        private static readonly float[] Softness = { 0f, 0.5f, 1f, 2f, 3f, 4f };
        private static readonly float[] Strengths = { 1f, 0.5f, 0.25f };
        private static int s_soft, s_strength;
        private static readonly HashSet<string> Tools = new HashSet<string> { "Vcarve", "Vfill", "Vcube", "Vsphere" };

        // the V-tool selected this frame (set by VoxelBrushDrag before it dispatches any dab)
        internal static string SelectedPiece;

        internal static void Bind(ConfigFile cfg)
        {
            const string TOOLS = "Voxel Terrain - Tools";
            SofterKey = cfg.Bind(TOOLS, "Brush softer key", KeyCode.KeypadPlus,
                "Vcarve / Vfill / Vcube / Vsphere: wider soft blend where the shape meets the ground (0, 0.5, 1, 2, 3, 4 m). 0 = hard edge.");
            HarderKey = cfg.Bind(TOOLS, "Brush harder key", KeyCode.KeypadMinus,
                "Vcarve / Vfill / Vcube / Vsphere: narrower soft blend, down to the hard edge.");
            StrengthKey = cfg.Bind(TOOLS, "Brush strength key", KeyCode.KeypadDivide,
                "Vcarve / Vfill / Vcube / Vsphere: how far one dab moves the ground toward the shape, cycling 100 % -> 50 % -> 25 %. "
                + "Below 100 %, hold or repeat dabs to build up. Also sets how strongly Vlevel's Left Alt smooth works.");
            RampAngleKey = cfg.Bind(TOOLS, "Vramp angle key", KeyCode.KeypadMultiply,
                "Vramp: step the ramp's slope through free (the rise you set), 10, 20, 30 and 45 degrees. A negative rise makes it go down.");
            TrueSmooth = cfg.Bind(TOOLS, "Vlevel Alt is a true smooth", true,
                "Vlevel with Left Alt held: each bit of ground moves toward the height of the ground around it, so bumps even out and slopes "
                + "stay slopes (0.2.1455). Off: the old behaviour, the whole circle levelled toward one height.");
        }

        internal static float SoftnessMetres => Softness[s_soft];
        internal static float Strength => Strengths[s_strength];
        internal static bool AppliesTo(string piece) => piece != null && Tools.Contains(piece);

        /// <summary>The blend values for a shape op right now: active only inside a V-tool stroke of one of the four shape tools,
        /// and only when either value differs from the hard full-strength default.</summary>
        internal static bool Active(out float softness, out float strength)
        {
            softness = 0f; strength = 1f;
            if (VoxelWorld.VToolScope <= 0 || !AppliesTo(SelectedPiece)) return false;
            softness = SoftnessMetres; strength = Strength;
            return softness > 0f || strength < 1f;
        }

        /// <summary>Per frame while a V-tool is selected (VoxelBrushDrag): the keys, with a top-left readout on change.</summary>
        internal static void Tick(string piece, Player player)
        {
            SelectedPiece = piece;
            bool shape = AppliesTo(piece), level = piece == "Vlevel" && TrueSmoothOn;   // 0.2.1455: strength also drives Vlevel's Alt smooth
            if ((!shape && !level) || SofterKey == null) return;
            if (piece == CubePiece && (FatInput.KeyDown(KeyCode.Q) || FatInput.KeyDown(KeyCode.E)))
            {
                s_cubeYaw = Mathf.Repeat(s_cubeYaw + (FatInput.KeyDown(KeyCode.E) ? CubeTurnStep : -CubeTurnStep), 90f);   // a cube repeats every 90
                string turn = $"Vcube turned {s_cubeYaw:0} degrees (Q / E)";
                FiresLogger.LogDebug("[FiresVoxel] " + turn);
                player?.Message(MessageHud.MessageType.TopLeft, turn, 0, null, false);
            }
            int soft = s_soft, str = s_strength;
            if (shape && FatInput.KeyDown(SofterKey.Value)) s_soft = Mathf.Min(s_soft + 1, Softness.Length - 1);
            if (shape && FatInput.KeyDown(HarderKey.Value)) s_soft = Mathf.Max(s_soft - 1, 0);
            if (FatInput.KeyDown(StrengthKey.Value)) s_strength = (s_strength + 1) % Strengths.Length;
            if (soft == s_soft && str == s_strength) return;
            string line = shape ? $"{piece} softness {(SoftnessMetres > 0f ? SoftnessMetres.ToString("0.#") + " m" : "off (hard edge)")}, strength {Strength * 100f:0} %"
                                : $"{piece} smooth strength {Strength * 100f:0} % (Left Alt + drag)";
            FiresLogger.LogDebug("[FiresVoxel] " + line);
            player?.Message(MessageHud.MessageType.TopLeft, line, 0, null, false);
        }

        // ── the maths (metres; density positive = solid) ─────────────────────────────────────────────────────────────────────
        // smooth minimum of a and b over width k (k = 0: plain min)
        private static float SoftMin(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// <summary>The new density bound (sbyte units) for one sample: carve = soft-min(old, -inside), fill = soft-max(old, inside), then
        /// moved `strength` of the way from old, at least one unit while short of it. Same clamp as ApplyShape.</summary>
        internal static sbyte Bound(sbyte old, float insideMetres, bool fill, float softness, float strength, float quant)
        {
            float oldM = old / quant;
            float target = fill ? -SoftMin(-oldM, -insideMetres, softness) : SoftMin(oldM, -insideMetres, softness);
            float want = Mathf.Lerp(oldM, target, strength);
            int q = Mathf.RoundToInt(want * quant);
            int qt = Mathf.RoundToInt(target * quant);
            if (q == old && qt != old) q = old + (qt > old ? 1 : -1);   // always some progress toward the shape
            return (sbyte)Mathf.Clamp(q, -127, 127);
        }
    }
}
