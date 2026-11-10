using BepInEx.Configuration;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // Per water CLASS and per BIOME look
    // values on top of the global inland keys. The sheet mesh carries uv2.y = class and uv2.z = biome code; Custom/FiresInlandWater reads
    // the arrays below (Core's firescoreshaders). ZERO everywhere = no change, so on a shader without the
    // tables, or with every entry on "inherit", the water looks exactly as it did.
    // Resolve order per pixel: the global keys, then the CLASS entry, then the BIOME entry (biome wins where both set a field), and the
    // floors (Minimum shallow visibility / Minimum ripple) last.
    // Encoding (per entry): TintS/TintD rgb + a = blend weight (0 = inherit); Params x = see-through depth m (0 = inherit),
    // y = turbidity + 1 (0 = inherit), z = ripple multiplier - 1, w = reflection multiplier - 1; Params2 x = swell multiplier - 1.
    internal static class InlandLookConfig
    {
        private const int Classes = 10, Biomes = 9;

        private sealed class Look
        {
            internal ConfigEntry<Color> Shallow, Deep;
            internal ConfigEntry<float> SeeThrough, Turbidity, Ripple, Reflection, Swell, Churn;   // Churn: biomes only (0.2.1092)
        }

        private static readonly Look[] s_class = new Look[Classes];
        private static readonly Look[] s_biome = new Look[Biomes];
        private static readonly Vector4[] s_cTS = new Vector4[Classes], s_cTD = new Vector4[Classes], s_cP = new Vector4[Classes], s_cP2 = new Vector4[Classes];
        private static readonly Vector4[] s_bTS = new Vector4[Biomes], s_bTD = new Vector4[Biomes], s_bP = new Vector4[Biomes], s_bP2 = new Vector4[Biomes];

        // class code -> section name (null = no section: none/ocean)
        private static readonly string[] ClassNames = { null, null, "River", "Stream", "Lake", "Pond", "Swamp", "Frozen", "Hot spring", "Waterfall" };
        // FiresWorldGenBase biome code order
        private static readonly string[] BiomeNames = { "Meadows", "Black Forest", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "Deep North", "Ocean" };
        // 0.2.1092 (v5 look, [seasons]): per-biome sediment churn along the flow, -1 = keep the global "Churn". Same order as BiomeNames.
        private static readonly float[] BiomeChurn = { 0.3f, 0.35f, 1.0f, 0.15f, 0.5f, 0.2f, 0.8f, 0.1f, -1f };

        // Biome defaults: swamps murky and dirty, mountains/forest clear and glassy (Meadows lakes too), plains warm (with steam,
        // InlandSteam), Mistlands tropical (bright turquoise into deep blue, so a mist-cleared build site is beautiful), Ashlands hot (steam), Deep North icy and clear. Tint alpha = how strongly it applies.
        private static readonly (string tS, string tD, float see, float turb, float rip, float refl, float swell)[] BiomeDefaults =
        {
            ("6FB5A699", "0F423499", 4.0f, 0.06f, 0.8f, 1.0f, 1.0f),   // Meadows: clear alpine
            ("5A9E8E99", "0C3A2C99", 3.5f, 0.08f, 0.6f, 1.0f, 0.8f),   // Black Forest: clear, glassy, darker
            ("4A4A2EFF", "1F2614FF", 0.8f, 0.70f, 0.7f, 0.5f, 0.5f),   // Swamp: murky, dirty brown-green
            ("7FC4C0B3", "12405AB3", 6.0f, 0.03f, 0.5f, 1.0f, 0.6f),   // Mountain: clear icy teal, glassy
            ("8AB09680", "2E5A4A80", 2.5f, 0.20f, 1.0f, 1.0f, 1.0f),   // Plains: warm, a little silty (steam on)
            ("4FE0D0CC", "0A3C8ACC", 8.0f, 0.02f, 0.5f, 1.0f, 0.6f),   // Mistlands: tropical: turquoise shallows, deep blue, very clear
            ("6A5A4699", "2A1E16B3", 1.2f, 0.50f, 1.2f, 0.7f, 1.0f),   // Ashlands: hot, ashy (steam on)
            ("9AD0D8B3", "1A4A66B3", 7.0f, 0.02f, 0.4f, 1.0f, 0.5f),   // Deep North: icy, very clear
            ("00000000", "00000000", 0f, -1f, 1f, 1f, 1f),             // Ocean: inherit (the sea is not this sheet)
        };

        internal static void Bind(ConfigFile cfg)
        {
            for (int c = 0; c < Classes; c++)
            {
                if (ClassNames[c] == null) continue;
                bool swamp = c == FiresWorldGenBase.WC_SWAMP;
                s_class[c] = BindLook(cfg, "Inland look - " + ClassNames[c],
                    swamp ? "4A4A2ECC" : "00000000", swamp ? "1F2614CC" : "00000000", swamp ? 1.0f : 0f, swamp ? 0.6f : -1f, 1f, swamp ? 0.6f : 1f, 1f,
                    $"{ClassNames[c]} water. Biome looks (below) win where both set a value.");
            }
            for (int b = 0; b < Biomes; b++)
            {
                var d = BiomeDefaults[b];
                s_biome[b] = BindLook(cfg, "Inland look - Biome " + BiomeNames[b], d.tS, d.tD, d.see, d.turb, d.rip, d.refl, d.swell,
                    $"Lakes, ponds and rivers in {BiomeNames[b]}.");
                s_biome[b].Churn = cfg.Bind("Inland look - Biome " + BiomeNames[b], "Churn", BiomeChurn[b],
                    new ConfigDescription("Muddy sediment churned along the flow, stronger near the bed and banks. -1 = keep the global.", new AcceptableValueRange<float>(-1f, 2f)));
            }
            // hot-reload, like the global inland keys
            cfg.SettingChanged += (_, args) =>
            {
                var sec = args?.ChangedSetting?.Definition?.Section;
                if (sec == null || !sec.StartsWith("Inland look - ")) return;
                try { Apply(VoxelChunkView.CurrentSheetMaterial); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[InlandLookConfig] re-apply failed: {ex.Message}"); }
            };
        }

        private static Look BindLook(ConfigFile cfg, string section, string tS, string tD, float see, float turb, float rip, float refl, float swell, string what)
        {
            ColorUtility.TryParseHtmlString("#" + tS, out Color cS);
            ColorUtility.TryParseHtmlString("#" + tD, out Color cD);
            return new Look
            {
                Shallow = cfg.Bind(section, "Shallow tint", cS, what + " Shallow colour; its ALPHA is how strongly it replaces the global tint (0 = keep the global)."),
                Deep = cfg.Bind(section, "Deep tint", cD, "Deep colour; its ALPHA is how strongly it applies (0 = keep the global)."),
                SeeThrough = cfg.Bind(section, "See-through depth (m)", see,
                    new ConfigDescription("Metres to reach the deep tint. 0 = keep the global.", new AcceptableValueRange<float>(0f, 40f))),
                Turbidity = cfg.Bind(section, "Turbidity", turb,
                    new ConfigDescription("Murk. -1 = keep the global.", new AcceptableValueRange<float>(-1f, 1f))),
                Ripple = cfg.Bind(section, "Ripple multiplier", rip,
                    new ConfigDescription("× the global ripple (1 = no change). The Minimum ripple setting still applies.", new AcceptableValueRange<float>(0f, 3f))),
                Reflection = cfg.Bind(section, "Reflection multiplier", refl,
                    new ConfigDescription("× the global reflection strength (1 = no change).", new AcceptableValueRange<float>(0f, 3f))),
                Swell = cfg.Bind(section, "Swell multiplier", swell,
                    new ConfigDescription("× the global swell (1 = no change).", new AcceptableValueRange<float>(0f, 3f))),
            };
        }

        // Uploads the tables to the inland material (called with FiresInlandWaterConfig.Apply; a shader without the arrays ignores them).
        internal const float MaxReflection = 0.8f;
        private static float StillBoost() => FiresInlandWaterConfig.StillReflBoost != null ? FiresInlandWaterConfig.StillReflBoost.Value : 1.4f;
        private static float GlobalReflection() => (FiresInlandWaterConfig.ReflStrength != null ? FiresInlandWaterConfig.ReflStrength.Value : 0.35f) * StillBoost();

        // The most a still, grazing-view pixel of this class in this biome reflects (before Fresnel), as uploaded. For water_look_test.
        internal static float EffectiveReflection(int cls, int biome)
        {
            float g = Mathf.Min(GlobalReflection(), MaxReflection);
            float cm = cls >= 0 && cls < Classes ? s_cP[cls].w + 1f : 1f, bm = biome >= 0 && biome < Biomes ? s_bP[biome].w + 1f : 1f;
            return g * cm * bm;
        }

        internal static void Apply(Material m)
        {
            if (m == null) return;
            Pack(s_class, s_cTS, s_cTD, s_cP, s_cP2);
            Pack(s_biome, s_bTS, s_bTD, s_bP, s_bP2);
            // The reflection amount ReflStrength x StillBoost x class x biome
            // reached 1.3 before Fresnel, so at a grazing view the lake became pure (overcast, white) sky. Capped at MaxReflection.
            float g = GlobalReflection();
            if (g > MaxReflection) m.SetFloat("_ReflStrength", MaxReflection / Mathf.Max(StillBoost(), 0.01f));
            g = Mathf.Min(g, MaxReflection);
            for (int c = 0; c < Classes; c++) s_cP[c].w = Mathf.Min(s_cP[c].w + 1f, g > 0f ? MaxReflection / g : 1f) - 1f;
            float cMax = 1f; for (int c = 0; c < Classes; c++) cMax = Mathf.Max(cMax, s_cP[c].w + 1f);
            for (int b = 0; b < Biomes; b++) s_bP[b].w = Mathf.Min(s_bP[b].w + 1f, g > 0f ? MaxReflection / (g * cMax) : 1f) - 1f;
            m.SetVectorArray("_ClassTintS", s_cTS); m.SetVectorArray("_ClassTintD", s_cTD);
            m.SetVectorArray("_ClassLook", s_cP); m.SetVectorArray("_ClassLook2", s_cP2);
            m.SetVectorArray("_BiomeTintS", s_bTS); m.SetVectorArray("_BiomeTintD", s_bTD);
            m.SetVectorArray("_BiomeLook", s_bP); m.SetVectorArray("_BiomeLook2", s_bP2);
            LogBind(m);
            // Minimum ripple, re-applied by the shader after the multipliers (a biome at 0.5x never drops under the floor)
            var floor = FiresInlandWaterConfig.MinRipple;
            m.SetFloat("_RippleFloor", floor != null ? floor.Value : 0.12f);
        }

        // 0.2.1090: the largest "Swell multiplier" over every class and biome look (the shader takes the biome's when it is not 1,
        // else the class's: amp *= max(1 + sx, 0)), so a caller can bound the swell's height anywhere. 1 when nothing is bound.
        internal static float MaxSwellMultiplier()
        {
            float m = 1f;
            foreach (var l in s_class) if (l?.Swell != null) m = Mathf.Max(m, l.Swell.Value);
            foreach (var l in s_biome) if (l?.Swell != null) m = Mathf.Max(m, l.Swell.Value);
            return m;
        }

        // [WaterLook] one line per bind (when the values change): what was sent, and whether this shader declares the new slots.
        private static string s_lastBind;
        private static void LogBind(Material m)
        {
            var sb = new System.Text.StringBuilder("[WaterLook] bound _BiomeLook2.y churn [");
            for (int b = 0; b < Biomes; b++) { if (b > 0) sb.Append(", "); sb.Append(BiomeNames[b]).Append(' ').Append(s_bP2[b].y.ToString("0.00")); }
            float mm = FiresInlandWaterConfig.MinMurk != null ? FiresInlandWaterConfig.MinMurk.Value : -1f;
            float ch = FiresInlandWaterConfig.Churn != null ? FiresInlandWaterConfig.Churn.Value : -1f;
            sb.Append("] (churn + 1, 0 = keep), _MinMurk ").Append(mm.ToString("0.00")).Append(m.HasProperty("_MinMurk") ? "" : " (NOT declared by this shader: skipped)")
              .Append(", _Churn ").Append(ch.ToString("0.00")).Append(m.HasProperty("_Churn") ? "" : " (NOT declared by this shader: skipped)")
              .Append(" on '").Append(m.shader != null ? m.shader.name : "?").Append('\'');
            string line = sb.ToString();
            if (line == s_lastBind) return;
            s_lastBind = line;
            FiresLogger.LogInfo(line);
        }

        private static void Pack(Look[] looks, Vector4[] tS, Vector4[] tD, Vector4[] p, Vector4[] p2)
        {
            for (int i = 0; i < looks.Length; i++)
            {
                var l = looks[i];
                if (l == null) { tS[i] = tD[i] = p[i] = p2[i] = Vector4.zero; continue; }
                tS[i] = l.Shallow.Value; tD[i] = l.Deep.Value;
                p[i] = new Vector4(l.SeeThrough.Value, l.Turbidity.Value < 0f ? 0f : l.Turbidity.Value + 1f, l.Ripple.Value - 1f, l.Reflection.Value - 1f);
                // y = churn + 1 (0 = keep the global), the Turbidity encoding; zw reserved
                float churn = l.Churn != null ? l.Churn.Value : -1f;
                p2[i] = new Vector4(l.Swell.Value - 1f, churn < 0f ? 0f : churn + 1f, 0f, 0f);
            }
        }
    }
}
