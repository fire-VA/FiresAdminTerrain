using System.Collections.Generic;
using FiresCore.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Falling-water curtains drawn with Custom/FiresWaterfall from the embedded "fireswaterfall" bundle: the
    // river that leaves an open zone over a closed one, and the short falls under skylands. The mesh contract
    // is documented in the shader; the geometry here is the same one the bundle builder previews. A falls
    // renderer carries both Materials: the first dims what is behind each curtain, the second adds its light,
    // so curtains that overlap blend the same in any order, after the water sheets have taken their screen grab.
    internal static class FiresWaterfalls
    {
        internal const float SeaLevel = 30f;
        internal const float PlungeDepth = 0.7f;
        internal const float EdgeLipOut = 1f;
        private const float Curl = 0.6f;
        private const float CurlLength = 1.2f;
        private const float LowerRowStep = 3f;
        private const int TransmitQueue = 3002;
        private const int AddQueue = 3003;
        private const float FogSyncSeconds = 1f;
        private static readonly float[] UpperRows = { 0f, 0.3f, 0.7f, 1.2f, 1.8f, 2.6f, 3.6f };
        private const float ApproachLength = 1.2f;
        private const float ApproachLift = 0.03f;
        private const float ApproachRise = 0.12f;
        private static readonly float[] ApproachRows = { -1.2f, -0.8f, -0.45f, -0.18f };

        // The falling water is a slab: the sheet at the lip, thickening outward as it falls towards Thickness (never
        // thicker than the curtain is wide), closed by rounded sides and a rear face, so it keeps a side profile from
        // every angle. Where the terrain catches it, it thins back to the sheet over GroundTaper. Every face is wound
        // outward and the shader culls back faces, so only the faces towards the eye draw and they never overlap.
        private const float Thickness = 1.5f;
        private const float MinThickness = 0.5f;
        private const float SpreadLength = 3f;
        private const float GroundTaper = 1.5f;
        private const int SideSegments = 4;
        // An edge is open (it fades, as the sheet's ends always did) until the slab is CloseThickness thick; past
        // that it is ClosedEdge from any open edge, beyond the shader's softest fade.
        private const float CloseThickness = 0.4f;
        private const float ClosedEdge = 2f;

        internal struct Column
        {
            internal Vector3 Base;
            internal float LipY;
            internal float FallLength;
            // A curved lip's own direction for this column; zero means the curtain's.
            internal Vector3 Outward;
            // Ends on dry ground rather than in water or thin air: the slab closes to a sheet where it lands.
            internal bool OntoGround;
        }

        // The least outward offset a curtain row may take at world height y so it hangs in front of the terrain: 0 at
        // a vertical face, more where the face bulges out below the lip, and negative when terrain fills the whole
        // reach at that height, which is where the water lands.
        internal delegate float OutwardProfile(Vector3 basePos, Vector3 outward, float y);

        private const float ProbeStep = 0.5f;
        private const float ProbeReach = 12f;
        private const float ProbeClearance = 0.35f;
        private const float MinFallLength = 2f;
        private static readonly List<float> s_rowOut = new List<float>(32);

        private const int MistQueue = 3010;
        private const string BundleName = "fireswaterfall";
        private const string OfferingMod = "FiresAdminTerrain";
        private const string MistShaderName = "FiresWaterfallMist";
        private const string MistShaderFullName = "Custom/FiresWaterfallMist";
        private const string MistPuffsName = "FiresWaterfallMistPuffs";
        private const float MistLipSprayOut = 0.8f;
        private const float MistLipSprayDrop = 0.8f;

        private static AssetBundle s_bundle;
        private static Material s_material;
        private static Material s_addMaterial;
        private static Material[] s_materials;
        private static Material s_mistMaterial;
        private static Material s_veilMaterial;
        private static bool s_loadTried;
        private static bool s_bundleTried;
        private static float s_nextFogSync;

        // Where the curtain AppendCurtain last drew leaves its lip and lands (or thins out), for its mist.
        internal static FiresWaterfallMist.Site LastSite;

        private static readonly List<Vector3> s_verts = new List<Vector3>(2048);
        private static readonly List<Vector3> s_norms = new List<Vector3>(2048);
        private static readonly List<Vector4> s_uv0 = new List<Vector4>(2048);
        private static readonly List<Vector4> s_uv1 = new List<Vector4>(2048);
        private static readonly List<Color32> s_cols = new List<Color32>(2048);
        private static readonly List<int> s_tris = new List<int>(6144);
        private static readonly List<float> s_rows = new List<float>(32);
        private static readonly List<float> s_along = new List<float>(128);
        private static readonly List<Vector3> s_rear = new List<Vector3>(2048);
        private static readonly List<float> s_thick = new List<float>(2048);
        private static readonly List<float> s_fallen = new List<float>(2048);
        private static readonly List<Vector3> s_dirs = new List<Vector3>(128);
        private static readonly List<float> s_lengths = new List<float>(128);
        private static readonly List<float> s_plunges = new List<float>(128);
        private static readonly List<float> s_lipY = new List<float>(128);
        private static readonly List<bool> s_landed = new List<bool>(128);
        private static readonly List<float> s_offsets = new List<float>(4096);

        // Smoothing along the curtain (columns are ~1 m apart): two passes of a 1-2-1 average.
        private const int AlongPasses = 2;
        // A triangle thinner than this (twice its area over its longest edge squared) is a needle: a sliver no wider than
        // a tenth of its length, the shape that read as ribbons.
        private const float NeedleRatio = 0.1f;

        // What AppendCurtain last shaped, for the Debug evidence line: the largest column-to-column step of the lip height
        // and of the fall length before and after smoothing, and the needle triangles left in the rear face.
        internal struct ShapeReport
        {
            internal int Columns, Rows, Needles;
            internal bool Approach;
            internal float RawLipSpread, LipSpread, RawLengthSpread, LengthSpread;
        }

        internal static ShapeReport LastShape;

        // A 1-2-1 average along the columns (ends keep their own weight). shortenOnly: a value may only drop, so a column
        // the terrain caught is never made longer than where it landed.
        private static void SmoothAlong(List<float> values, bool shortenOnly)
        {
            int n = values.Count;
            if (n < 3) return;
            float before = values[0];
            for (int c = 0; c < n; c++)
            {
                float own = values[c];
                float next = c + 1 < n ? values[c + 1] : own;
                float mean = (before + 2f * own + next) * 0.25f;
                before = own;
                values[c] = shortenOnly ? Mathf.Min(own, mean) : mean;
            }
        }

        // Per row, each column's outward offset moves towards its neighbours' mean but never in front of less than its own
        // terrain clearance.
        private static void EvenOffsets(int columns, int rows)
        {
            if (columns < 3) return;
            for (int r = 0; r < rows; r++)
            {
                float before = s_offsets[r];
                for (int c = 0; c < columns; c++)
                {
                    int i = c * rows + r;
                    float own = s_offsets[i];
                    float next = c + 1 < columns ? s_offsets[i + rows] : own;
                    s_offsets[i] = Mathf.Max(own, (before + 2f * own + next) * 0.25f);
                    before = own;
                }
            }
        }

        private static float MaxStep(List<float> values)
        {
            float step = 0f;
            for (int c = 1; c < values.Count; c++) step = Mathf.Max(step, Mathf.Abs(values[c] - values[c - 1]));
            return step;
        }

        // Needle triangles in the stitched rear grid of the curtain just shaped (s_rear, column-major).
        private static int CountNeedles(int columns, int rows)
        {
            int needles = 0;
            int baseIndex = s_rear.Count - columns * rows;
            for (int c = 0; c < columns - 1; c++)
                for (int k = 0; k < rows - 1; k++)
                {
                    Vector3 a = s_rear[baseIndex + c * rows + k], a1 = s_rear[baseIndex + c * rows + k + 1];
                    Vector3 b = s_rear[baseIndex + (c + 1) * rows + k], b1 = s_rear[baseIndex + (c + 1) * rows + k + 1];
                    if (IsNeedle(a, b, a1)) needles++;
                    if (IsNeedle(b, b1, a1)) needles++;
                }
            return needles;
        }

        private static bool IsNeedle(Vector3 p, Vector3 q, Vector3 r)
        {
            float longest = Mathf.Max((q - p).sqrMagnitude, Mathf.Max((r - q).sqrMagnitude, (p - r).sqrMagnitude));
            if (longest < 1e-6f) return false;
            float twiceArea = Vector3.Cross(q - p, r - p).magnitude;
            return twiceArea / longest < NeedleRatio;
        }

        internal static Material Material
        {
            get
            {
                if (s_material == null && !s_loadTried) Load();
                return s_material;
            }
        }

        // What a falls renderer carries (sharedMaterials) on its one-submesh mesh: Unity draws the mesh once per material.
        internal static Material[] Materials
        {
            get
            {
                if (s_material == null && !s_loadTried) Load();
                return s_materials;
            }
        }

        // The mist shader is offered to Core's broker, so other mods (FDT's ghost) draw with the one live copy instead
        // of shipping their own; the falls take theirs from the broker too. The bundle loads once, whoever asks first.
        internal static void OfferShaders()
        {
            FiresShaderBroker.Offer(MistShaderFullName, OfferingMod, LoadMistShader);
            RegisterProbe();
            FiresRainbow.Init(FiresAdminTerrain.Instance != null ? FiresAdminTerrain.Instance.Config : null);
        }

        // Another shader from the fireswaterfall bundle (FiresRainbow's Custom/FiresRainbowSky), or null.
        internal static Shader LoadShader(string name)
        {
            var bundle = Bundle;
            return bundle != null ? bundle.LoadAsset<Shader>(name) : null;
        }

        // va_falls_probe [radius]: read-only. Every falls mesh near the player - its path, materials, size, the approach-row
        // vertices it still carries (uv0.y < 0, laid on the river above the lip) and its needle triangles (the ribbons).
        private static bool s_probeRegistered;

        private static void RegisterProbe()
        {
            if (s_probeRegistered) return;
            s_probeRegistered = true;
            new Terminal.ConsoleCommand("va_falls_probe", "[radius m] - falls meshes near you: approach-row vertices and needle (ribbon) triangles",
                args =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context?.AddString("no local player"); return; }
                    float radius = args.Length > 1 && float.TryParse(args[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : 80f;
                    Vector3 at = player.transform.position;
                    var uvs = new List<Vector4>();
                    var tris = new List<int>();
                    var verts = new List<Vector3>();
                    int meshes = 0;
                    foreach (var filter in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
                    {
                        var mesh = filter != null ? filter.sharedMesh : null;
                        if (mesh == null || mesh.name != "genwater_falls" || !filter.gameObject.activeInHierarchy) continue;
                        var renderer = filter.GetComponent<MeshRenderer>();
                        if (renderer == null || (renderer.bounds.ClosestPoint(at) - at).magnitude > radius) continue;
                        mesh.GetVertices(verts);
                        mesh.GetUVs(0, uvs);
                        mesh.GetTriangles(tris, 0);
                        int approachVerts = 0;
                        foreach (var uv in uvs) if (uv.y < 0f) approachVerts++;
                        int needles = 0;
                        for (int t = 0; t + 2 < tris.Count; t += 3)
                        {
                            Vector3 p = filter.transform.TransformPoint(verts[tris[t]]), q = filter.transform.TransformPoint(verts[tris[t + 1]]),
                                s = filter.transform.TransformPoint(verts[tris[t + 2]]);
                            if (IsNeedle(p, q, s)) needles++;
                        }
                        var names = new List<string>();
                        foreach (var material in renderer.sharedMaterials)
                            names.Add(material == null ? "null" : $"{material.name} ({(material.shader != null ? material.shader.name : "no shader")})");
                        var mist = filter.transform.Find(FiresWaterfallMist.RootName);
                        string line = $"[FiresWaterfall] probe: {Path(filter.transform)} at {renderer.bounds.center:F1}, "
                            + $"{(renderer.bounds.ClosestPoint(at) - at).magnitude:0} m: {verts.Count} vertices, {tris.Count / 3} triangles, "
                            + $"{approachVerts} approach-row vertices, {needles} needle triangle(s) (thinner than 1:{1f / NeedleRatio:0}); "
                            + $"materials {string.Join(", ", names)}; mist emitters {(mist != null ? mist.childCount : 0)}";
                        args.Context?.AddString(line);
                        FiresLogger.LogInfo(line);
                        meshes++;
                    }
                    args.Context?.AddString($"[FiresWaterfall] probe: {meshes} falls mesh(es) within {radius:0} m");
                });
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null && path.Length < 160; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private static Shader LoadMistShader()
        {
            var bundle = Bundle;
            return bundle != null ? bundle.LoadAsset<Shader>(MistShaderName) : null;
        }

        private static AssetBundle Bundle
        {
            get
            {
                if (s_bundleTried) return s_bundle;
                s_bundleTried = true;
                s_bundle = FiresAdminTerrain.GetAssetBundleFromResources(BundleName);
                return s_bundle;
            }
        }

        private static void Load()
        {
            s_loadTried = true;
            try
            {
                s_bundle = Bundle;
                if (s_bundle == null)
                {
                    FiresLogger.LogWarning("[FiresWaterfall] embedded bundle 'fireswaterfall' missing — no falling water.");
                    return;
                }
                var shader = s_bundle.LoadAsset<Shader>("FiresWaterfall");
                if (shader == null)
                    foreach (var candidate in s_bundle.LoadAllAssets<Shader>())
                        if (candidate != null && candidate.name == "Custom/FiresWaterfall") shader = candidate;
                if (shader == null || !shader.isSupported)
                {
                    FiresLogger.LogWarning($"[FiresWaterfall] Custom/FiresWaterfall {(shader == null ? "not in the bundle" : "not supported on this GPU")} — no falling water.");
                    return;
                }
                s_material = new Material(shader) { name = "FiresWaterfallTransmit", renderQueue = TransmitQueue };
                s_material.SetFloat("_SrcBlend", (float)BlendMode.Zero);
                s_material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                s_addMaterial = new Material(shader) { name = "FiresWaterfallAdd", renderQueue = AddQueue };
                s_addMaterial.SetFloat("_SrcBlend", (float)BlendMode.One);
                // 0.2.839 (Fire 10-07, 117.webp: "waterfalls still get far too white and hard geometry when they stack"): the light pass
                // was pure additive (One One), so N overlapping curtains summed N times their light and clipped to flat white. Screen
                // (One, OneMinusSrcColor) adds less the brighter the pixel already is and never passes white.
                s_addMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcColor);
                s_materials = new[] { s_material, s_addMaterial };
                LoadMist();
                SyncFog();
                FiresLogger.LogInfo($"[FiresWaterfall] {shader.name} loaded from bundle '{s_bundle.name}' (queues {TransmitQueue} dim + {AddQueue} light).");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWaterfall] bundle load failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The mist's two materials: the veil rising off a landing is the only one that shows the rainbow.
        internal static Material MistMaterial
        {
            get
            {
                if (s_material == null && !s_loadTried) Load();
                return s_mistMaterial;
            }
        }

        internal static Material VeilMaterial
        {
            get
            {
                if (s_material == null && !s_loadTried) Load();
                return s_veilMaterial;
            }
        }

        private static void LoadMist()
        {
            var shader = FiresShaderBroker.Get(MistShaderFullName) ?? LoadMistShader();
            var puffs = s_bundle.LoadAsset<Texture2D>(MistPuffsName);
            if (shader == null || !shader.isSupported || puffs == null)
            {
                FiresLogger.LogWarning($"[FiresWaterfall] mist {(shader == null ? "shader not in the bundle" : !shader.isSupported ? "shader not supported on this GPU" : "puff atlas not in the bundle")} — falls draw without mist.");
                return;
            }
            s_mistMaterial = new Material(shader) { name = "FiresWaterfallMist", renderQueue = MistQueue, mainTexture = puffs };
            s_mistMaterial.SetFloat("_Rainbow", 0f);
            s_veilMaterial = new Material(shader) { name = "FiresWaterfallMistVeil", renderQueue = MistQueue, mainTexture = puffs };
            FiresLogger.LogInfo($"[FiresWaterfall] {shader.name} loaded with its puff atlas (queue {MistQueue}).");
        }

        internal static void Tick()
        {
            if (s_material == null || Time.time < s_nextFogSync) return;
            s_nextFogSync = Time.time + FogSyncSeconds;
            SyncFog();
        }

        private static void SyncFog()
        {
            float mode = !RenderSettings.fog ? 0f
                : RenderSettings.fogMode == FogMode.Linear ? 1f
                : RenderSettings.fogMode == FogMode.Exponential ? 2f : 3f;
            s_material.SetFloat("_FiresFogMode", mode);
            s_addMaterial.SetFloat("_FiresFogMode", mode);
            if (s_mistMaterial != null) s_mistMaterial.SetFloat("_FiresFogMode", mode);
            if (s_veilMaterial != null) s_veilMaterial.SetFloat("_FiresFogMode", mode);
            // 0.2.840: 'Waterfall Brightness' on the light pass (the shader's _LightScale), and both kept in step on the
            // before-the-water copies FatSinkFalls makes of these materials (they missed every later fog sync until now).
            float light = FiresVoxelConfig.WaterfallBrightness != null ? FiresVoxelConfig.WaterfallBrightness.Value : 1f;
            s_addMaterial.SetFloat("_LightScale", light);
            FatSinkFalls.SyncUnderRiver(mode, light);
        }

        internal static void BeginMesh()
        {
            s_verts.Clear(); s_norms.Clear(); s_uv0.Clear(); s_uv1.Clear(); s_cols.Clear(); s_tris.Clear();
        }

        // One curtain along `columns` (in order along the lip). From the lip, lipOut past the plane the water
        // leaves, it arcs Curl further out over the first metres, then falls straight down to its length. A column
        // with its own Outward follows a curved lip. With a profile, every row stays in front of the terrain and the
        // fall ends where the terrain catches it. The sheet those rows trace is the slab's rear face; its front face,
        // the approach on the river and the rounded sides are one ribbon, the rear another. Returns whether it drew, and
        // leaves the curtain's lip and foot in LastSite.
        // approach: lay the approach rows on the river upstream of the lip. The voxel sheet's curtains leave them out (Fire
        // 10-05): they sat on the stored step level while the sheet there bends over the lip, so two surfaces crossed at
        // every lip and read as stringy ribbons; the lip sprays mist instead.
        // 0.2.809 (Fire 10-06: "smooth them together and blend them so you don't see hard seam edges on the ribbon"): a caller
        // whose lip run CONTINUES past this curtain (into the next chunk's curtain) sets OpenStart / OpenEnd before the call: that
        // end gets no rounded side cap and no edge fade, so two curtains meet as one ribbon. AcrossStart anchors the texture's
        // across coordinate in the world (the lip run's own distance), so the pattern runs on across the join instead of
        // restarting. All three are consumed (reset) by the call.
        internal static bool OpenStart, OpenEnd;
        internal static float AcrossStart = float.NaN;
        private const float OpenEdgeFar = 1e4f;

        internal static bool AppendCurtain(List<Column> columns, Vector3 outward, float lipOut, float fadeLength, float seed,
            OutwardProfile profile = null, bool approach = true)
        {
            bool openStart = OpenStart, openEnd = OpenEnd;
            float acrossStart = AcrossStart;
            OpenStart = OpenEnd = false;
            AcrossStart = float.NaN;
            int n = columns.Count;
            if (n < 2) return false;
            float longest = 0f;
            foreach (var column in columns) longest = Mathf.Max(longest, column.FallLength);
            if (longest <= 0.5f) return false;

            s_rows.Clear();
            if (approach) s_rows.AddRange(ApproachRows);
            foreach (float f in UpperRows) if (f < longest) s_rows.Add(f);
            for (float f = UpperRows[UpperRows.Length - 1] + LowerRowStep; f < longest; f += LowerRowStep) s_rows.Add(f);
            s_rows.Add(longest);
            int rows = s_rows.Count;
            int firstFallRow = approach ? ApproachRows.Length : 0;

            s_along.Clear();
            s_along.Add(0f);
            for (int c = 1; c < n; c++)
            {
                Vector3 a = columns[c - 1].Base, b = columns[c].Base;
                s_along.Add(s_along[c - 1] + new Vector2(b.x - a.x, b.z - a.z).magnitude);
            }
            float width = s_along[n - 1];
            float thickness = Mathf.Min(Thickness, Mathf.Max(width, MinThickness));

            s_rear.Clear(); s_thick.Clear(); s_fallen.Clear(); s_dirs.Clear(); s_lengths.Clear(); s_plunges.Clear();
            s_lipY.Clear(); s_landed.Clear(); s_offsets.Clear();
            bool anyLanded = false;
            Vector3 lipSum = Vector3.zero, footSum = Vector3.zero, dirSum = Vector3.zero;

            // Fire 10-05 ("the mesh splits off into almost thin strips like ribbons or noodles"): every column kept its own
            // lip height, its own length (rows past it all collapsed onto its foot) and its own terrain offset per row, so
            // next to a longer or further-out neighbour the stitch drew needle triangles - a sawtooth edge and pale facets.
            // Now the lip height and length are smoothed along the curtain (a length only ever shortens, so no column is
            // pushed into rock it landed on), every column spreads the whole row schedule over its OWN length, and each
            // row's offset is evened out along the curtain without pulling any column back towards the terrain.
            float rawLipSpread = 0f, rawLengthSpread = 0f;
            for (int c = 0; c < n; c++)
            {
                var column = columns[c];
                Vector3 dir = column.Outward.sqrMagnitude > 0.01f ? column.Outward : outward;
                s_dirs.Add(dir);
                s_lengths.Add(Shape(column, dir, profile, out bool landed));
                s_landed.Add(landed);
                s_lipY.Add(column.LipY);
                if (c > 0)
                {
                    rawLipSpread = Mathf.Max(rawLipSpread, Mathf.Abs(column.LipY - columns[c - 1].LipY));
                    rawLengthSpread = Mathf.Max(rawLengthSpread, Mathf.Abs(s_lengths[c] - s_lengths[c - 1]));
                }
            }
            for (int pass = 0; pass < AlongPasses; pass++)
            {
                SmoothAlong(s_lipY, false);
                SmoothAlong(s_lengths, true);
            }

            for (int c = 0; c < n; c++)
            {
                var column = columns[c];
                Vector3 dir = s_dirs[c];
                float length = Mathf.Max(s_lengths[c], 0f);
                float stretch = length / longest;
                float lipY = s_lipY[c];
                float last = 0f;
                for (int r = 0; r < rows; r++)
                {
                    float f = s_rows[r];
                    if (f < 0f || profile == null) { s_offsets.Add(0f); continue; }
                    float outAt = profile(column.Base, dir, lipY - f * stretch);
                    if (outAt < 0f) outAt = last;
                    s_offsets.Add(outAt);
                    last = outAt;
                }
            }
            for (int pass = 0; pass < AlongPasses; pass++) EvenOffsets(n, rows);

            for (int c = 0; c < n; c++)
            {
                var column = columns[c];
                Vector3 dir = s_dirs[c];
                float length = Mathf.Max(s_lengths[c], 0f);
                float stretch = length / longest;
                float lipY = s_lipY[c];
                bool landed = s_landed[c];
                bool closesOnGround = landed || column.OntoGround;
                bool plunges = !closesOnGround && fadeLength <= 0f;
                anyLanded |= landed;
                dirSum += dir;
                lipSum += new Vector3(column.Base.x, lipY - MistLipSprayDrop, column.Base.z) + dir * (lipOut + MistLipSprayOut);
                footSum += new Vector3(0f, plunges ? PlungeDepth : 0f, 0f);
                s_plunges.Add(plunges ? 1f : 0f);
                for (int r = 0; r < rows; r++)
                {
                    float f = s_rows[r];
                    if (f < 0f)
                    {
                        float upstream = -f;
                        float back = lipOut - upstream;
                        float lift = ApproachLift + ApproachRise * upstream / ApproachLength;
                        s_rear.Add(new Vector3(column.Base.x + dir.x * back, lipY + lift, column.Base.z + dir.z * back));
                        s_thick.Add(0f);
                        s_fallen.Add(f);
                        continue;
                    }
                    float fall = f * stretch;
                    float offset = Mathf.Max(lipOut + Curl * (1f - Mathf.Exp(-fall / CurlLength)), s_offsets[c * rows + r]);
                    s_rear.Add(new Vector3(column.Base.x + dir.x * offset, lipY - fall, column.Base.z + dir.z * offset));
                    s_thick.Add(SlabThickness(thickness, fall, length, closesOnGround));
                    s_fallen.Add(fall);
                }
            }
            LastShape = new ShapeReport
            {
                Columns = n, Rows = rows, Approach = approach, RawLipSpread = rawLipSpread, RawLengthSpread = rawLengthSpread,
                LipSpread = MaxStep(s_lipY), LengthSpread = MaxStep(s_lengths), Needles = CountNeedles(n, rows),
            };

            // A lip the terrain catches at once is not a spillway: the profile lands every column in the first metre, and
            // what would be drawn is a stub of curtain lying in the rock, poking through the ground it was meant to leave.
            // Nothing is drawn there at all, approach rows included.
            float shaped = 0f;
            foreach (float length in s_lengths) shaped = Mathf.Max(shaped, length);
            if (shaped < MinFallLength) return false;

            for (int c = 0; c < n; c++) footSum += s_rear[c * rows + rows - 1];
            Vector3 meanDir = dirSum.sqrMagnitude > 1e-4f ? dirSum.normalized : outward;
            LastSite = new FiresWaterfallMist.Site
            {
                Lip = lipSum / n,
                Foot = footSum / n + meanDir * (Mathf.Min(Thickness, Mathf.Max(s_along[n - 1], MinThickness)) * 0.5f),
                Outward = meanDir,
                Width = s_along[n - 1],
                Drop = shaped,
                FadeLength = fadeLength,
                Dissolves = fadeLength > 0f && !anyLanded,
            };

            float sideArc = Mathf.PI * thickness * 0.5f;
            Vector3 leftSide = SideDirection(columns[0].Base - columns[1].Base, s_dirs[0]);
            Vector3 rightSide = SideDirection(columns[n - 1].Base - columns[n - 2].Base, s_dirs[n - 1]);
            var curtain = new CurtainPart { Rows = rows, Width = width, FadeLength = fadeLength, Seed = seed, OpenStart = openStart, OpenEnd = openEnd };
            bool inward = WindsInward(columns);
            // The front's across coordinate starts at the world anchor when one is given (a continuing run), else after the cap.
            float front = float.IsNaN(acrossStart) ? sideArc : acrossStart;

            int ribbon = s_verts.Count;
            if (!openStart)
                for (int k = 0; k < SideSegments; k++)
                    AppendSide(curtain, 0, leftSide, Mathf.PI * (1f - (float)k / SideSegments), front - sideArc + sideArc * k / SideSegments);
            for (int c = 0; c < n; c++) AppendFront(curtain, c, front + s_along[c]);
            if (!openEnd)
                for (int k = 1; k <= SideSegments; k++)
                    AppendSide(curtain, n - 1, rightSide, Mathf.PI * k / SideSegments, front + width + sideArc * k / SideSegments);
            Stitch(ribbon, n + (openStart ? 0 : SideSegments) + (openEnd ? 0 : SideSegments), rows, inward);

            int rear = s_verts.Count;
            for (int c = n - 1; c >= 0; c--) AppendRear(curtain, c, firstFallRow, front + sideArc + width + (width - s_along[c]));
            Stitch(rear, n, rows - firstFallRow, inward);
            return true;
        }

        // Columns running left to right as seen from outside the fall wind its faces outward; the other order winds
        // them inward and the stitch flips them.
        private static bool WindsInward(List<Column> columns)
        {
            float facing = 0f;
            for (int c = 0; c < columns.Count - 1; c++)
                facing += Vector3.Dot(Vector3.Cross(Vector3.up, columns[c + 1].Base - columns[c].Base), s_dirs[c]);
            return facing < 0f;
        }

        private struct CurtainPart
        {
            internal int Rows;
            internal float Width;
            internal float FadeLength;
            internal float Seed;
            internal bool OpenStart, OpenEnd;   // 0.2.809: the run continues past that end (no cap, no fade)
        }

        private static float SlabThickness(float thickness, float fall, float length, bool closesOnGround)
        {
            float t = thickness * (1f - Mathf.Exp(-fall / SpreadLength));
            return closesOnGround ? t * Mathf.SmoothStep(0f, 1f, (length - fall) / GroundTaper) : t;
        }

        // Metres to the nearest open edge: an edge closes as the slab reaches CloseThickness.
        private static float EdgeDistance(float openEdge, float thick) => openEdge + ClosedEdge * Mathf.Clamp01(thick / CloseThickness);

        // Horizontal, square to the column's outward direction, pointing away from the curtain.
        private static Vector3 SideDirection(Vector3 awayAlongLip, Vector3 dir)
        {
            var side = new Vector3(awayAlongLip.x, 0f, awayAlongLip.z);
            side -= dir * Vector3.Dot(side, dir);
            if (side.sqrMagnitude > 1e-6f) return side.normalized;
            return new Vector3(-dir.z, 0f, dir.x);
        }

        private static void AppendFront(in CurtainPart curtain, int c, float across)
        {
            float openEdge = Mathf.Min(curtain.OpenStart ? OpenEdgeFar : s_along[c], curtain.OpenEnd ? OpenEdgeFar : curtain.Width - s_along[c]);
            Vector3 dir = s_dirs[c];
            for (int r = 0; r < curtain.Rows; r++)
            {
                int i = c * curtain.Rows + r;
                float fallen = s_fallen[i];
                float thick = s_thick[i];
                Vector3 normal = fallen < 0f
                    ? Vector3.Lerp(dir, Vector3.up, Mathf.Clamp01(-fallen * 2f / ApproachLength)).normalized
                    : dir;
                AddVertex(s_rear[i] + dir * thick, normal, across, fallen, EdgeDistance(openEdge, thick), s_lengths[c], s_plunges[c], curtain);
            }
        }

        // One column of a rounded side: theta 0 is the front face's end, PI the rear face's.
        private static void AppendSide(in CurtainPart curtain, int c, Vector3 side, float theta, float across)
        {
            Vector3 dir = s_dirs[c];
            float cos = Mathf.Cos(theta), sin = Mathf.Sin(theta);
            Vector3 normal = (dir * cos + side * sin).normalized;
            for (int r = 0; r < curtain.Rows; r++)
            {
                int i = c * curtain.Rows + r;
                float half = s_thick[i] * 0.5f;
                Vector3 position = s_rear[i] + dir * (half + half * cos) + side * (half * sin);
                AddVertex(position, normal, across, s_fallen[i], EdgeDistance(0f, s_thick[i]), s_lengths[c], s_plunges[c], curtain);
            }
        }

        // The rear face, seen only from behind the fall, starts at the lip where it lies on the front.
        private static void AppendRear(in CurtainPart curtain, int c, int firstRow, float across)
        {
            float openEdge = Mathf.Min(curtain.OpenStart ? OpenEdgeFar : s_along[c], curtain.OpenEnd ? OpenEdgeFar : curtain.Width - s_along[c]);
            Vector3 normal = -s_dirs[c];
            for (int r = firstRow; r < curtain.Rows; r++)
            {
                int i = c * curtain.Rows + r;
                AddVertex(s_rear[i], normal, across, s_fallen[i], EdgeDistance(openEdge, s_thick[i]), s_lengths[c], s_plunges[c], curtain);
            }
        }

        private static void AddVertex(Vector3 position, Vector3 normal, float across, float fallen, float edgeDistance,
            float length, float plunge, in CurtainPart curtain)
        {
            s_verts.Add(position);
            s_norms.Add(normal);
            s_uv0.Add(new Vector4(across, fallen, ApproachLength, plunge));
            s_uv1.Add(new Vector4(edgeDistance, length, curtain.FadeLength, curtain.Seed));
            s_cols.Add(new Color32(255, 255, 255, 255));
        }

        private static void Stitch(int baseIndex, int columns, int rows, bool flip)
        {
            for (int c = 0; c < columns - 1; c++)
            {
                for (int k = 0; k < rows - 1; k++)
                {
                    int a = baseIndex + c * rows + k, b = baseIndex + (c + 1) * rows + k;
                    if (flip)
                    {
                        s_tris.Add(a); s_tris.Add(a + 1); s_tris.Add(b);
                        s_tris.Add(b); s_tris.Add(a + 1); s_tris.Add(b + 1);
                        continue;
                    }
                    s_tris.Add(a); s_tris.Add(b); s_tris.Add(a + 1);
                    s_tris.Add(b); s_tris.Add(b + 1); s_tris.Add(a + 1);
                }
            }
        }

        // Per row of the schedule, the profile's outward offset for this column, and the fall's length once the
        // terrain catches it. Without a profile every offset is 0 and the length is the column's own. A landed row
        // keeps the offset above it, so the curtain's foot stays in front of the face it landed on.
        private static float Shape(Column column, Vector3 dir, OutwardProfile profile, out bool landed)
        {
            s_rowOut.Clear();
            float length = column.FallLength;
            landed = false;
            for (int r = 0; r < s_rows.Count; r++)
            {
                float f = s_rows[r];
                if (profile == null || f < 0f) { s_rowOut.Add(0f); continue; }
                if (landed) { s_rowOut.Add(s_rowOut[r - 1]); continue; }
                float fall = Mathf.Min(f, column.FallLength);
                float outAt = profile(column.Base, dir, column.LipY - fall);
                if (outAt < 0f)
                {
                    landed = true;
                    length = Mathf.Min(length, fall);
                    s_rowOut.Add(r > 0 ? s_rowOut[r - 1] : 0f);
                    continue;
                }
                s_rowOut.Add(outAt);
            }
            return length;
        }

        // The default profile: marches outward from the lip at height y until it leaves the terrain, reading voxel
        // density where a chunk holds it and the heightmap ground elsewhere.
        internal static float TerrainProfile(Vector3 basePos, Vector3 outward, float y)
        {
            for (float d = 0f; d <= ProbeReach; d += ProbeStep)
            {
                if (!SolidAt(basePos.x + outward.x * d, y, basePos.z + outward.z * d))
                    return d <= 0f ? 0f : d + ProbeClearance;
            }
            return -1f;
        }

        private static bool SolidAt(float x, float y, float z)
        {
            var key = new Vector3Int(FloorDiv(Mathf.FloorToInt(x), VoxelChunk.CELLS), FloorDiv(Mathf.FloorToInt(y), VoxelChunk.CELLS),
                FloorDiv(Mathf.FloorToInt(z), VoxelChunk.CELLS));
            if (VoxelWorld.TryGetChunk(key, out var chunk) && chunk != null)
            {
                sbyte d = chunk.SampleDensityWorld(new Vector3(x, y, z));
                if (d != sbyte.MinValue) return d > 0;
            }
            return VoxelZoneStreamer.TryGroundHeight(x, z, out float ground) && ground > y;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

        internal static int EndMesh(Mesh mesh)
        {
            mesh.Clear(false);
            if (s_tris.Count == 0) return 0;
            mesh.indexFormat = s_verts.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(s_verts);
            mesh.SetNormals(s_norms);
            mesh.SetUVs(0, s_uv0);
            mesh.SetUVs(1, s_uv1);
            mesh.SetColors(s_cols);
            mesh.SetTriangles(s_tris, 0, calculateBounds: true);
            return s_verts.Count;
        }

        // The rush of a fall is the jack-o-turnip's fire loop pitched far up: the user's in-game recipe
        // (spawn_object piece_jackoturnip f=ZSFX,minPitch,15 f=ZSFX,maxPitch,16), without the piece around it.
        private const string SoundPrefab = "piece_jackoturnip";
        private const string SoundObject = "sfx_fire_loop";
        private const float SoundMinPitch = 15f;
        private const float SoundMaxPitch = 16f;
        private const float SoundDrop = 4f;
        private static GameObject s_sound;

        // Where a curtain sounds from: its middle column, a few metres below the lip, out over the drop.
        internal static Vector3 SoundSite(List<Column> columns, Vector3 outward)
        {
            var middle = columns[columns.Count / 2];
            float drop = Mathf.Min(middle.FallLength * 0.5f, SoundDrop);
            return new Vector3(middle.Base.x, middle.LipY - drop, middle.Base.z) + outward * EdgeLipOut;
        }

        // Replaces the sounds a falls object carries (its children besides the mist) with one per site.
        internal static void SetSounds(Transform falls, List<Vector3> sites)
        {
            for (int i = falls.childCount - 1; i >= 0; i--)
            {
                var child = falls.GetChild(i).gameObject;
                if (child.name != FiresWaterfallMist.RootName) Object.Destroy(child);
            }
            var source = SoundSource();
            if (source == null) return;
            foreach (var site in sites)
            {
                var sfx = Object.Instantiate(source, site, Quaternion.identity, falls).GetComponent<ZSFX>();
                sfx.m_minPitch = SoundMinPitch;
                sfx.m_maxPitch = SoundMaxPitch;
            }
        }

        private static GameObject SoundSource()
        {
            if (s_sound != null) return s_sound;
            var piece = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(SoundPrefab) : null;
            if (piece == null) return null;
            foreach (var sfx in piece.GetComponentsInChildren<ZSFX>(true))
                if (sfx.name == SoundObject) s_sound = sfx.gameObject;
            return s_sound;
        }
    }
}
