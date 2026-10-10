using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // LAYERED PAINT (FAT 0.2.758; Fire 10-06: "all the tools should only affect the layer they are raycast on", paint, undo and
    // MP "exactly like TerrainComp edits"). The ground keeps vanilla's paint: the zone's TerrainComp mask (dirt / cultivated /
    // paved) copied into each chunk column's mask texture, and BiomePaint's turned-ground circles. A cave floor and a skyland
    // are layers of their own: their paint lives in the chunk's samples (VoxelChunk.PaintColumn; one byte a sample, ground
    // paint + biome), rides the chunk's anchor blob (so it syncs to every peer and FatUndo keeps it with the chunk's edits),
    // and is drawn per vertex:
    //   * every vertex is classed Ground / Cave / Island the way FiresTerrainLayers resolves a tool's hit (an island's body; rock
    //     over the point under the column's ground top; else ground);
    //   * a ground vertex samples the column's mask as before (UV0 squeezed onto its 33 texels) and takes the turned-ground tint;
    //   * a cave or skyland vertex points UV0 into the palette beside the mask: a 2x2 block whose corners are none / dirt /
    //     paved / cultivated, so the bilinear filter blends between painted and unpainted vertices like vanilla's mask does;
    //     the biome in its samples, if any, is its colour.
    // No shader change: FiresVoxelHeightmap samples _ClearedMaskTex at raw UV0 and reads UV0 for nothing else on voxel chunks.
    internal static class VoxelLayerPaint
    {
        internal const byte None = 0, Dirt = 1, Cultivated = 2, Paved = 3;

        // The column mask: 33 ground texels, a copy of the last one (so the ground's u = 1 edge still reads it), the palette.
        internal const int MaskWidth = 36;
        private const int MaskHeight = 33, PaletteX = 34, PaletteGrowRow = 0, PaletteBareRow = 3;
        private const float GroundU = 33f / MaskWidth;

        private const byte LayerGround = 0, LayerCave = 1, LayerIsland = 2;
        private const float GroundMargin = 1f;     // FiresTerrainLayers: a point within this of the ground's top is never in a cave
        private const float IslandSkin = 2f;       // an island's underside vertices sit about on its bottom
        private const float IslandOverGround = 8f; // 0.2.763: VoxelChunk unions a skyland in only above ground + 8 m, so that high is island
        private const float SkinBelow = 2f, SkinAbove = 1.5f;   // ground paint: the samples this far round a column's surface
        private const float JobBudgetMs = 4f;

        private static int s_paintedChunks;
        internal static bool Any => s_paintedChunks > 0;
        internal static void NoteChunkPainted() { if (s_paintedChunks < int.MaxValue) s_paintedChunks++; }

        // ── biome codes (the high nibble) ────────────────────────────────────────────────────────────────────────────────
        private static readonly Heightmap.Biome[] s_biomes =
        {
            Heightmap.Biome.None, Heightmap.Biome.Meadows, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest,
            Heightmap.Biome.Plains, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean, Heightmap.Biome.Mistlands,
        };

        internal static byte BiomeCode(Heightmap.Biome biome)
        {
            for (int i = 1; i < s_biomes.Length; i++) if (s_biomes[i] == biome) return (byte)i;
            return 0;
        }

        internal static Heightmap.Biome BiomeOf(int code) => code > 0 && code < s_biomes.Length ? s_biomes[code] : Heightmap.Biome.None;

        internal static byte MaskOf(TerrainModifier.PaintType type)
        {
            switch (type)
            {
                case TerrainModifier.PaintType.Dirt: return Dirt;
                case TerrainModifier.PaintType.Cultivate: return Cultivated;
                case TerrainModifier.PaintType.Paved: return Paved;
                default: return None;   // Reset
            }
        }

        // ── the palette ──────────────────────────────────────────────────────────────────────────────────────────────────
        // Two 2x2 blocks: alpha 1 (vegetation as vanilla's "nothing" paint) and alpha 0 (bare: Ashlands, where the mask's alpha is
        // its lava and a cave or skyland must not glow). Corners: (0,0) none, (1,0) dirt, (0,1) paved, (1,1) cultivated.
        internal static void FillPalette(Color[] pixels)
        {
            for (int j = 0; j < MaskHeight; j++)
            {
                int row = j * MaskWidth;
                pixels[row + 33] = pixels[row + 32];
                pixels[row + PaletteX] = pixels[row + PaletteX + 1] = Heightmap.m_paintMaskNothing;
            }
            Block(pixels, PaletteGrowRow, 1f);
            Block(pixels, PaletteBareRow, 0f);
        }

        private static void Block(Color[] pixels, int row, float a)
        {
            pixels[row * MaskWidth + PaletteX] = new Color(0f, 0f, 0f, a);
            pixels[row * MaskWidth + PaletteX + 1] = new Color(1f, 0f, 0f, a);
            pixels[(row + 1) * MaskWidth + PaletteX] = new Color(0f, 0f, 1f, a);
            pixels[(row + 1) * MaskWidth + PaletteX + 1] = new Color(0f, 1f, 0f, a);
        }

        // wet: the wet-shore band (0..1) a vertex with no paint of its own wears, as the ground's mask carries PaintWet01 in r.
        // 0.2.763: the mask a chunk wears until its column's is composed (VoxelWorld.BindOrQueuePaintMask): no ground paint and the
        // palette, so a skyland or cave vertex reads its own (bare on Ashlands) from its first frame instead of the shared material's
        // blank mask, whose alpha 1 is lava there.
        private static Texture2D s_blankMask, s_bareMask;
        private static MaterialPropertyBlock s_bareBlock;
        private static readonly int s_maskProp = Shader.PropertyToID("_ClearedMaskTex");

        internal static Texture2D BlankMask
        {
            get
            {
                if (s_blankMask != null) return s_blankMask;
                var pixels = new Color[MaskWidth * MaskHeight];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = Heightmap.m_paintMaskNothing;
                FillPalette(pixels);
                s_blankMask = new Texture2D(MaskWidth, MaskHeight, TextureFormat.RGBA32, mipChain: false)
                {
                    name = "VoxelPaint_Blank", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
                s_blankMask.SetPixels(pixels);
                s_blankMask.Apply(false, false);
                return s_blankMask;
            }
        }

        // An Ashlands skyland's far piece (ZoneModeSkylands): a bare mask, so the far island is not drawn as one sheet of lava.
        internal static void BindBare(Renderer renderer)
        {
            if (renderer == null) return;
            if (s_bareMask == null)
            {
                s_bareMask = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false) { name = "VoxelPaint_Bare", hideFlags = HideFlags.HideAndDontSave };
                s_bareMask.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                s_bareMask.Apply(false, false);
            }
            if (s_bareBlock == null) s_bareBlock = new MaterialPropertyBlock();
            s_bareBlock.Clear();
            s_bareBlock.SetTexture(s_maskProp, s_bareMask);
            renderer.SetPropertyBlock(s_bareBlock);
        }

        private static Vector2 PaletteUv(int mask, bool bare, float wet)
        {
            float s = mask == Dirt || mask == Cultivated ? 1f : mask == None ? wet : 0f;
            float t = mask == Paved || mask == Cultivated ? 1f : 0f;
            int row = bare ? PaletteBareRow : PaletteGrowRow;
            return new Vector2((PaletteX + 0.5f + s) / MaskWidth, (row + 0.5f + t) / MaskHeight);
        }

        private static bool IsAshlands(Color32 c) => c.r >= 128 && c.a >= 128 && c.g < 128 && c.b < 128;

        // ── Ashlands lava and turned-ground edges (FAT 0.2.770; Fire 10-06: "ashlands is just all lava, never any land") ───────
        // The terrain shader reads the paint mask's ALPHA as lava wherever the ground is Ashlands; vanilla writes it from
        // WorldGenerator.GetAshlandsHeight (mask = (0,0,0,lava)) and every other biome ships alpha 1. A circle turned to
        // Ashlands kept its old biome's alpha 1, so the whole circle, walls and all, drew as lava. The lava a point holds as
        // Ashlands ground: on a Fires world's own Ashlands its baked lava field, anywhere else vanilla's own lava rule there.
        internal static float AshlandsLava(float wx, float wz)
        {
            if (FiresWorldGenBase.Active && FiresWorldGenBase.BiomeCodeAt(wx, wz) == 6)
                return Mathf.Clamp01(FiresWorldGenBase.SampleLavaMask(wx, wz).a);
            var generator = WorldGenerator.instance;
            if (generator == null) return 0f;
            generator.GetAshlandsHeight(wx, wz, out Color mask);
            return Mathf.Clamp01(mask.a);
        }

        // How fully a turned-ground circle holds a point: 1 inside, easing to 0 over its last few metres, so the circle's edge
        // blends instead of stepping per vertex / mask texel.
        internal static float TurnedWeight(in BiomePaint.Circle circle, float wx, float wz)
        {
            float dx = wx - circle.X, dz = wz - circle.Z;
            float feather = Mathf.Clamp(circle.R * 0.25f, 1f, 4f);
            return Mathf.Clamp01((circle.R - Mathf.Sqrt(dx * dx + dz * dz)) / feather);
        }

        // ── render (VoxelChunkView.ApplyBuffers, client) ─────────────────────────────────────────────────────────────────
        private static byte[] s_layers = new byte[0];
        private const int Cols = VoxelChunk.SAMPLES;   // local -1..CELLS
        private static readonly float[] s_ground = new float[Cols * Cols];
        private static readonly float[] s_bottom = new float[Cols * Cols];
        private static readonly sbyte[] s_rockOver = new sbyte[Cols * Cols];   // rock over the chunk's top, under the ground
        private static readonly int[] s_rockStamp = new int[Cols * Cols];
        private static readonly int[] s_stamp = new int[Cols * Cols];
        private static int s_render;

        internal static void Render(Vector3Int key, List<Vector3> verts, List<Vector2> uvs, List<Vector2> waterlines, List<Color32> colors)
        {
            int n = Mathf.Min(verts.Count, Mathf.Min(uvs.Count, colors.Count));
            if (n == 0) return;
            VoxelWorld.TryGetChunk(key, out var chunk);
            var origin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
            if (s_layers.Length < n) s_layers = new byte[Mathf.NextPowerOfTwo(n)];
            Classify(chunk, origin, verts, n);
            bool turned = BiomePaint.Any;
            bool painted = chunk != null && chunk.HasLayerPaint;
            for (int i = 0; i < n; i++)
            {
                var v = verts[i];
                if (s_layers[i] == LayerGround)
                {
                    if (turned && BiomePaint.TryGround(v.x, v.z, out var ground, out var circle))   // 0.2.770: blended at the edge
                        colors[i] = Color32.Lerp(colors[i], Heightmap.GetBiomeColor(ground), TurnedWeight(in circle, v.x, v.z));
                    uvs[i] = new Vector2(uvs[i].x * GroundU, uvs[i].y);
                    continue;
                }
                byte paint = painted ? PaintNear(chunk, origin, v) : (byte)0;
                if ((paint >> 4) != 0) colors[i] = Heightmap.GetBiomeColor(BiomeOf(paint >> 4));
                float wet = Mathf.Max(WetAt(v.y, i < waterlines.Count ? waterlines[i].x : 0f), SheetWetAt(origin, v));
                uvs[i] = PaletteUv(paint & 0x0F, IsAshlands(colors[i]), wet);
            }
        }

        // Fire 10-06: a cave's river or pool wets its own bed and banks like the ground's water does. The mesher's per-vertex
        // waterline (UV1.x: this chunk's own generated water, dilated a few cells onto the bank; 0 = none) gives the band:
        // fully wet up to WetFull over the line (and the bed under it), fading out by WetReach. The surface's wet field
        // (VoxelZoneStreamer.PaintWet01, in the column's mask) never reaches a cave floor or a skyland.
        private const float WetFull = 0.3f, WetReach = 1.5f;

        private static float WetAt(float y, float waterline)
        {
            if (waterline <= 0.5f) return 0f;
            return 1f - Mathf.Clamp01((y - waterline - WetFull) / (WetReach - WetFull));
        }

        // 0.2.762: placed and laid water too - a cave pool's calm sheet, a Vpool / Vpond, water under the sea's level - through
        // [seasons]' WaterSheets.SurfaceAt, once per column of the chunk (its highest sheet within the chunk's height). Its bed
        // wets only within SheetBedDepth under it, so a lower cave under a pool keeps dry rock.
        private const float SheetBedDepth = 4f;
        private static readonly float[] s_sheet = new float[Cols * Cols];
        private static readonly int[] s_sheetStamp = new int[Cols * Cols];

        private static float SheetWetAt(Vector3 origin, Vector3 v)
        {
            int lx = Mathf.Clamp(Mathf.RoundToInt(v.x - origin.x), -1, VoxelChunk.CELLS), lz = Mathf.Clamp(Mathf.RoundToInt(v.z - origin.z), -1, VoxelChunk.CELLS);
            int col = (lx + 1) + Cols * (lz + 1);
            if (s_sheetStamp[col] != s_render)
            {
                s_sheetStamp[col] = s_render;
                s_sheet[col] = VerdantsAscent.Water.Tools.WaterSheets.SurfaceAt(origin.x + lx, origin.z + lz, origin.y - 2f, origin.y + VoxelChunk.CELLS + 2f);
            }
            float line = s_sheet[col];
            if (float.IsNaN(line) || v.y < line - SheetBedDepth) return 0f;
            return WetAt(v.y, line);
        }

        // The paint nearest a vertex: its nearest sample's, else the first painted corner of the cell it sits in.
        private static byte PaintNear(VoxelChunk chunk, Vector3 origin, Vector3 v)
        {
            float lx = v.x - origin.x, ly = v.y - origin.y, lz = v.z - origin.z;
            byte p = chunk.PaintAt(Mathf.RoundToInt(lx), Mathf.RoundToInt(ly), Mathf.RoundToInt(lz));
            if (p != 0) return p;
            int x0 = Mathf.FloorToInt(lx), y0 = Mathf.FloorToInt(ly), z0 = Mathf.FloorToInt(lz);
            for (int c = 0; c < 8; c++)
            {
                p = chunk.PaintAt(x0 + (c & 1), y0 + ((c >> 1) & 1), z0 + ((c >> 2) & 1));
                if (p != 0) return p;
            }
            return 0;
        }

        private static void Classify(VoxelChunk chunk, Vector3 origin, List<Vector3> verts, int n)
        {
            if (++s_render == int.MaxValue) { s_render = 1; System.Array.Clear(s_stamp, 0, s_stamp.Length); System.Array.Clear(s_rockStamp, 0, s_rockStamp.Length); System.Array.Clear(s_sheetStamp, 0, s_sheetStamp.Length); }
            bool islands = FiresSkyIslands.ChunkOverlaps(origin);
            for (int i = 0; i < n; i++)
            {
                var v = verts[i];
                int fx = Mathf.Clamp(Mathf.FloorToInt(v.x - origin.x), -1, VoxelChunk.CELLS - 1);
                int fz = Mathf.Clamp(Mathf.FloorToInt(v.z - origin.z), -1, VoxelChunk.CELLS - 1);
                int col = Column(origin, fx, fz, islands);
                float bottom = s_bottom[col], ground = s_ground[col];
                // 0.2.763: or, in a skyland chunk, anything over the ground by the voxels' own island clip (the rim skin past the
                // analytic disc read the ground column's mask, whose alpha is lava on Ashlands).
                if ((!float.IsNaN(bottom) && v.y >= bottom - IslandSkin) || (islands && !float.IsNaN(ground) && v.y > ground + IslandOverGround))
                    { s_layers[i] = LayerIsland; continue; }
                if (float.IsNaN(ground) || v.y > ground - GroundMargin) { s_layers[i] = LayerGround; continue; }
                s_layers[i] = InCave(chunk, origin, fx, fz, v.y, ground - GroundMargin) ? LayerCave : LayerGround;
            }
        }

        private static int Column(Vector3 origin, int lx, int lz, bool islands)
        {
            int col = (lx + 1) + Cols * (lz + 1);
            if (s_stamp[col] == s_render) return col;
            s_stamp[col] = s_render;
            float wx = origin.x + lx + 0.5f, wz = origin.z + lz + 0.5f;
            s_ground[col] = VoxelZoneStreamer.TryGroundHeight(wx, wz, out float ground) ? ground : float.NaN;
            s_bottom[col] = islands && FiresSkyIslands.TryBottomAt(wx, wz, out float bottom) ? bottom : float.NaN;
            return col;
        }

        // Under the ground's top: a cave when, in every corner column of the vertex's cell that is open over it, rock closes
        // the way up before the ground's top (a pit's wall or a dug hole open to the sky stays ground). No open corner = a
        // ceiling or an overhang's underside: cave.
        private static bool InCave(VoxelChunk chunk, Vector3 origin, int fx, int fz, float vy, float limit)
        {
            if (chunk == null) return false;   // nothing to read: the ground's paint, as before 0.2.758
            int y0 = Mathf.CeilToInt(vy - origin.y);
            int yTop = Mathf.Min(VoxelChunk.CELLS, Mathf.FloorToInt(limit - origin.y));
            for (int c = 0; c < 4; c++)
            {
                int cx = fx + (c & 1), cz = fz + (c >> 1);
                sbyte here = chunk.DensityAt(cx, Mathf.Min(y0, VoxelChunk.CELLS), cz);
                if (here == sbyte.MinValue || here > 0) continue;
                if (!RockOver(chunk, origin, cx, cz, y0, yTop, limit)) return false;
            }
            return true;
        }

        private static bool RockOver(VoxelChunk chunk, Vector3 origin, int cx, int cz, int y0, int yTop, float limit)
        {
            for (int y = y0; y <= yTop; y++)
            {
                sbyte d = chunk.DensityAt(cx, y, cz);
                if (d != sbyte.MinValue && d > 0) return true;
            }
            float top = origin.y + VoxelChunk.CELLS;
            if (top + 1f > limit) return false;
            int col = Mathf.Clamp(cx, -1, VoxelChunk.CELLS) + 1 + Cols * (Mathf.Clamp(cz, -1, VoxelChunk.CELLS) + 1);
            if (s_rockStamp[col] == s_render) return s_rockOver[col] == 1;
            bool rock = false;
            float wx = origin.x + cx, wz = origin.z + cz;
            for (float y = top + 1f; y <= limit; y += 1f)
            {
                if (!VoxelWorld.TryIsSolid(wx, y, wz, out bool solid)) break;   // not loaded: open, as FiresTerrainLayers.TryRoofAbove reads it
                if (solid) { rock = true; break; }
            }
            s_rockStamp[col] = s_render;
            s_rockOver[col] = (sbyte)(rock ? 1 : 0);
            return rock;
        }

        // ── the biome a layer is turned to at a point (Heightmap.GetBiome on a skyland, the HUD) ────────────────────────
        // Main thread only (the chunk store is not thread-safe); false elsewhere and where nothing is painted.
        private static int s_mainThread = -1;
        internal static void NoteMainThread() => s_mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

        internal static bool TryPaintedBiomeAt(Vector3 point, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!Any || System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThread) return false;
            for (int dy = 0; dy >= -2; dy--)
            {
                var p = new Vector3(point.x, point.y + dy, point.z);
                var key = new Vector3Int(Mathf.FloorToInt(p.x / VoxelChunk.CELLS), Mathf.FloorToInt(p.y / VoxelChunk.CELLS), Mathf.FloorToInt(p.z / VoxelChunk.CELLS));
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasLayerPaint) continue;
                var origin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
                int code = PaintNear(chunk, origin, p) >> 4;
                if (code == 0) continue;
                biome = BiomeOf(code);
                return true;
            }
            return false;
        }

        // 0.2.798 (layered clutter): the ground paint on a cave or skyland floor at a point (None when unpainted or unreadable):
        // the hoe / cultivator mask there, for grass's cleared check on that layer. Main thread only.
        internal static byte PaintedMaskAt(Vector3 point)
        {
            if (!Any || System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThread) return None;
            for (int dy = 0; dy >= -2; dy--)
            {
                var p = new Vector3(point.x, point.y + dy, point.z);
                var key = new Vector3Int(Mathf.FloorToInt(p.x / VoxelChunk.CELLS), Mathf.FloorToInt(p.y / VoxelChunk.CELLS), Mathf.FloorToInt(p.z / VoxelChunk.CELLS));
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || !chunk.HasLayerPaint) continue;
                var origin = new Vector3(key.x * VoxelChunk.CELLS, key.y * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS);
                int mask = PaintNear(chunk, origin, p) & 0x0F;
                if (mask != None) return (byte)mask;
            }
            return None;
        }

        // ── painting a layer ─────────────────────────────────────────────────────────────────────────────────────────────
        // A job paints every column within its radius that resolves to the aimed layer (FiresTerrainLayers.TryResolveColumn),
        // a few rows a frame, inside one FatUndo stroke. Ground paint (dirt / cultivated / paved / reset) lays the samples
        // round each column's surface; a biome turns the whole layer's column (a skyland's sides and underside, a cave's walls
        // and roof), as the turned ground's colour does.
        private sealed class Job
        {
            public TerrainLayer Layer;
            public Vector3 Centre;
            public float Radius;
            public bool SetMask, SetBiome;
            public byte Mask, Biome;
            public string What;
            public int Z, Z1, Columns;
            public float Hint;
            public readonly HashSet<Vector3Int> Kept = new HashSet<Vector3Int>();
            public readonly HashSet<Vector3Int> Changed = new HashSet<Vector3Int>();
        }

        private static readonly Queue<Job> s_jobs = new Queue<Job>();
        private static readonly System.Diagnostics.Stopwatch s_watch = new System.Diagnostics.Stopwatch();

        // A cave's or a skyland's paint from the point a tool aims at (the ground's paint stays with vanilla). False on the
        // ground: the caller paints it the vanilla way.
        internal static bool TryPaint(Vector3 hit, float radius, bool setMask, byte mask, bool setBiome, byte biome, string what)
        {
            var layer = FiresTerrainLayers.Resolve(hit);
            if (layer.Kind == TerrainLayerKind.Ground) return false;
            radius = Mathf.Clamp(radius, 0.5f, BiomePaint.MaxRadius);
            var job = new Job
            {
                Layer = layer, Centre = hit, Radius = radius, SetMask = setMask, Mask = mask, SetBiome = setBiome, Biome = biome, What = what,
                Z = Mathf.FloorToInt(hit.z - radius), Z1 = Mathf.CeilToInt(hit.z + radius), Hint = layer.Surface,
            };
            s_jobs.Enqueue(job);
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture, "[FiresVoxel] layer paint queued: {0} on {1}, {2:0.0} m round ({3:0.0},{4:0.0},{5:0.0})",
                what, layer.Describe(), radius, hit.x, hit.y, hit.z));
            Pump();
            return true;
        }

        // A paint-mask colour (WEC's named paints or "r,g,b[,a]"; vanilla's r dirt, g cultivated, b paved) laid on a cave or a
        // skyland: its strongest channel over half is the paint, else it clears. False on the ground (paint it the vanilla way).
        internal static bool TryPaintColor(Vector3 hit, float radius, Color paint, string what)
        {
            byte mask = None;
            float best = 0.5f;
            if (paint.r > best) { best = paint.r; mask = Dirt; }
            if (paint.g > best) { best = paint.g; mask = Cultivated; }
            if (paint.b > best) mask = Paved;
            return TryPaint(hit, radius, true, mask, false, 0, what);
        }

        // Per frame (VoxelBrushDrag.Update): the queued jobs, within the frame budget.
        internal static void Pump()
        {
            if (s_jobs.Count == 0) return;
            s_watch.Restart();
            FatUndo.BeginPlayerEdit(s_jobs.Peek().What);
            try
            {
                while (s_jobs.Count > 0 && s_watch.Elapsed.TotalMilliseconds < JobBudgetMs)
                {
                    var job = s_jobs.Peek();
                    RunRow(job);
                    if (job.Z <= job.Z1) continue;
                    s_jobs.Dequeue();
                    Flush(job);
                    FiresLogger.LogDebug($"[FiresVoxel] layer paint done: {job.What} on {job.Layer.Describe()}, {job.Columns} column(s), {job.Changed.Count} chunk(s) changed");
                }
            }
            catch (System.Exception ex)
            {
                var job = s_jobs.Dequeue();
                Flush(job);
                FiresLogger.LogWarning($"[FiresVoxel] layer paint ({job.What}) stopped: {ex.Message}");
            }
            finally { FatUndo.EndPlayerEdit(); }
            foreach (var job in s_jobs) Flush(job);
        }

        private static void RunRow(Job job)
        {
            int z = job.Z++;
            float dz = z - job.Centre.z;
            float half = Mathf.Sqrt(Mathf.Max(0f, job.Radius * job.Radius - dz * dz));
            int x0 = Mathf.CeilToInt(job.Centre.x - half), x1 = Mathf.FloorToInt(job.Centre.x + half);
            for (int x = x0; x <= x1; x++)
            {
                // From the last column's surface, so a slope wider than the resolver's reach is still followed.
                var near = job.Layer;
                near.Surface = job.Hint;
                if (!FiresTerrainLayers.TryResolveColumn(x, z, in near, out var col)) continue;
                job.Hint = col.Surface;
                float lo, hi;
                if (job.SetBiome)
                {
                    lo = col.Kind == TerrainLayerKind.Island ? col.Floor - FiresTerrainLayers.IslandUndersideKeep - IslandSkin : col.Surface - SkinBelow;
                    hi = col.Kind == TerrainLayerKind.Cave ? col.Ceiling + FiresTerrainLayers.RoofKeep + 1f : col.Surface + SkinAbove;
                }
                else
                {
                    lo = col.Surface - SkinBelow;
                    hi = Mathf.Min(col.Surface + SkinAbove, col.Ceiling + FiresTerrainLayers.RoofKeep - 0.5f);
                }
                if (hi < lo) continue;
                job.Columns++;
                PaintSpan(job, x, z, Mathf.FloorToInt(lo), Mathf.CeilToInt(hi));
            }
        }

        // World column (x, z), samples y0..y1, in every loaded chunk holding them (border rings included).
        private static void PaintSpan(Job job, int x, int z, int y0, int y1)
        {
            const int C = VoxelChunk.CELLS;
            for (int kz = FloorDiv(z - C, C); kz <= FloorDiv(z + 1, C); kz++)
                for (int kx = FloorDiv(x - C, C); kx <= FloorDiv(x + 1, C); kx++)
                    for (int ky = FloorDiv(y0 - C, C); ky <= FloorDiv(y1 + 1, C); ky++)
                    {
                        int lx = x - kx * C, lz = z - kz * C, ly0 = y0 - ky * C, ly1 = y1 - ky * C;
                        if (lx < -1 || lx > C || lz < -1 || lz > C || ly1 < -1 || ly0 > C) continue;
                        var key = new Vector3Int(kx, ky, kz);
                        if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null || chunk.Kind != ChunkKind.Full) continue;
                        if (job.Kept.Add(key))
                            FatUndo.BeforeVoxelEdit(job.What, new Vector3((kx + 0.5f) * C, (ky + 0.5f) * C, (kz + 0.5f) * C), 1f, 1f);
                        if (chunk.PaintColumn(lx, lz, ly0, ly1, job.SetMask, job.Mask, job.SetBiome, job.Biome)) job.Changed.Add(key);
                    }
        }

        // Changed chunks save their anchors (every peer restores the paint from them) and redraw.
        private static void Flush(Job job)
        {
            foreach (var key in job.Changed)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) continue;
                VoxelWorld.RequestSave(key);
                VoxelMeshPipeline.EnqueueRemesh(chunk);
            }
            job.Changed.Clear();
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
    }
}
