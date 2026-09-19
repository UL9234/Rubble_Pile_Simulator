// DISTRIBUTION STATEMENT A. Approved for public release. Distribution is unlimited.
//  
// This material is based upon work supported by the Department of the Air Force under Air Force Contract No. FA8702-15-D-0001. Any opinions, findings, conclusions or recommendations expressed in this material are those of the author(s) and do not necessarily reflect the views of the Department of the Air Force.
//  
// © 2024 Massachusetts Institute of Technology.
// Subject to FAR52.227-11 Patent Rights - Ownership by the contractor (May 2014)
//  
// The software/firmware is provided to you on an As-Is basis
//  
// Delivered to the U.S. Government with Unlimited Rights, as defined in DFARS Part 252.227-7013 or 7014 (Feb 2014). Notwithstanding any copyright notice, U.S. Government rights in this work are defined by DFARS 252.227-7013 or 252.227-7014 as detailed above. Use of this work other than as specifically authorized by the U.S. Government may violate any copyrights that exist in this work.

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds reinforced-concrete floor slab fragments procedurally, one Voronoi layer at a time:
///
///   1. scatter seed points over the layer plane with Poisson-disk sampling plus jitter, so no cell
///      degenerates into a sliver (8..11 cells by default),
///   2. cut the plane with a Voronoi diagram (each cell is the half-plane intersection against all
///      other seeds - the cells are convex, which keeps every later step cheap and robust),
///   3. shrink each cell inwards by dd, a small distance derived from the cell size (the saw kerf
///      between neighbouring fragments),
///   4. extrude the cell upwards by a slab thickness that is constant for the whole scene
///      (120..180 mm by default), sampling Perlin noise along the two outlines with an amplitude
///      bounded by dd so the edges become irregular but still match each other,
///   5. loft the bottom outline to the top outline into a closed solid,
///   6. optionally break sharp corners: the sharper the corner, the higher the chance of shearing it
///      off with one planar cut, roughly half the slab thickness deep.
///
/// The factory only makes geometry; spawning, physics and lifetimes stay in DebrisSpawner.
/// </summary>
public static class ProceduralSlabFactory
{
    public struct Settings
    {
        public float width;            // layer plane size X (m)
        public float depth;            // layer plane size Z (m)
        public int cellsMin;           // 8
        public int cellsMax;           // 11
        public float inset;            // <= 0: no kerf at all - the cells tile exactly and the
                                       //         separation is left to the slab repulsion in physics
        public float noiseAmplitude;   // <= 0: derive from the cell size via noiseFraction
        public float noiseFraction;    // noise amplitude as a fraction of the cell size
        public float edgeStep;         // target edge subdivision length (m)
        public float noiseScale;       // Perlin frequency (1/m)
        public bool edgeNoise;
        public float verticalNoise;    // fraction of the thickness: how uneven the top face is
        public bool breakCorners;
        public float cornerChance;     // multiplies the angle-derived probability
        public float cornerDepth;      // <0: half the thickness
    }

    public struct Slab
    {
        public Mesh mesh;
        public Vector3 center;         // renderer-space centre of the piece (local space)
        public Vector2 size;           // XZ extent
        public float footprint;        // 2D area of the fragment (m^2)
        public float volume;           // m^3
    }

    // Colour blocks inside Assets/MITLL/Models/Debris/debris_A.png (a 256x256 palette atlas whose
    // left half holds four solid colours). Ranges are Unity UV space, i.e. v grows upwards while the
    // image rows grow downwards, hence the v ranges counted from the top of the image.
    private static readonly Rect[] PaletteBlocks =
    {
        new Rect(0.010f, 0.733f, 0.253f, 0.253f),   // rust orange (226,102,27)
        new Rect(0.293f, 0.733f, 0.253f, 0.253f),   // mid grey (143,149,158)
        new Rect(0.575f, 0.733f, 0.253f, 0.253f),   // light blue grey (196,203,213)
        new Rect(0.010f, 0.451f, 0.253f, 0.253f),   // dark rust brown (113,56,39)
    };

    public static int PaletteBlockCount { get { return PaletteBlocks.Length; } }

    /// <summary>Builds one layer of slabs in plane-local space (y = 0 is the bottom face).</summary>
    public static List<Slab> BuildLayer(Settings s, float thickness, System.Random rng, out int cornerCuts)
    {
        var result = new List<Slab>();
        cornerCuts = 0;

        Rect rect = new Rect(-s.width * 0.5f, -s.depth * 0.5f, s.width, s.depth);
        int target = Mathf.Max(1, s.cellsMin + rng.Next(s.cellsMax - s.cellsMin + 1));
        List<Vector2> sites = SampleSites(rect, target, rng);

        foreach (Vector2 site in sites)
        {
            List<Vector2> cell = VoronoiCell(site, sites, rect);
            if (cell == null || cell.Count < 3) continue;

            float area = Mathf.Abs(SignedArea(cell));
            if (area < 0.02f) continue;                       // ignore slivers
            float perimeter = Mathf.Max(1e-4f, Perimeter(cell));
            float inradius = 2f * area / perimeter;
            float compactness = perimeter * perimeter / (4f * Mathf.PI * area);   // 1.0 = circle
            if (inradius < 0.08f) continue;                   // cells too small to make a real slab
            if (compactness > 2.5f) continue;                 // needle / long strip shaped cells

            // No kerf by default: every cell keeps its exact Voronoi outline, so one layer is a
            // continuous floor slab. Keeping the fragments apart is the job of the slab repulsion.
            List<Vector2> inner = cell;
            if (s.inset > 0f)
            {
                List<Vector2> shrunk = InsetPolygon(cell, Mathf.Min(s.inset, inradius * 0.25f));
                if (shrunk != null && shrunk.Count >= 3) inner = shrunk;
            }

            // The relief is sampled on the shared Voronoi boundary and mirrored between neighbours,
            // so a small amplitude only opens a hairline crack - it can never push one slab into
            // another. Amplitude therefore scales with the cell size, not with a kerf.
            float noiseAmp = s.noiseAmplitude > 0f
                ? s.noiseAmplitude
                : Mathf.Clamp(s.noiseFraction * Mathf.Sqrt(area), 0.005f, 0.025f);
            int[] cornerIdx;
            // One outline, duplicated for the top face. The noise is sampled on the *shared Voronoi
            // boundary* (not on this cell's inset copy), so both neighbours along that boundary get the
            // same displacement mirrored about it - the two crack faces stay complementary.
            Vector2[] bottom = Subdivide(inner, cell, s.edgeStep,
                                         s.edgeNoise ? noiseAmp : 0f, s.noiseScale, 0f, out cornerIdx);
            Vector2[] top = (Vector2[])bottom.Clone();

            List<Vector3[]> faces = BuildPrism(bottom, top, thickness,
                                               s.verticalNoise > 0f ? s.verticalNoise : 0f);
            if (s.breakCorners && cornerIdx.Length > 0)
            {
                cornerCuts += BreakCorners(faces, bottom, cornerIdx, thickness, s, rng);
            }

            // One palette colour per fragment (not per layer) so a pile reads as mixed debris.
            Slab slab = ToSlab(faces, thickness, rng.Next(PaletteBlocks.Length), Mathf.Abs(SignedArea(inner)));
            if (slab.mesh != null) result.Add(slab);
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    //  1. seed points: Poisson-disk sampling + jitter
    // ---------------------------------------------------------------------------------------------
    private static List<Vector2> SampleSites(Rect rect, int target, System.Random rng)
    {
        float area = rect.width * rect.height;
        float r = Mathf.Sqrt(area / (target * 0.72f));
        for (int attempt = 0; attempt < 14; attempt++)
        {
            List<Vector2> candidates = Bridson(rect, r, rng);
            if (candidates.Count >= target)
            {
                // Bridson returns points in spatial growth order, so the first N points would be a
                // cluster. Pick a well spread subset by farthest-point sampling instead: that is what
                // keeps every Voronoi cell a fat polygon rather than a sliver.
                List<Vector2> picked = FarthestPointSubset(candidates, target, rect.center);
                float jitter = r * 0.16f;
                for (int i = 0; i < picked.Count; i++)
                {
                    picked[i] = new Vector2(
                        Mathf.Clamp(picked[i].x + Range(rng, -jitter, jitter), rect.xMin, rect.xMax),
                        Mathf.Clamp(picked[i].y + Range(rng, -jitter, jitter), rect.yMin, rect.yMax));
                }
                return picked;
            }
            r *= 0.85f;
        }

        var fallback = new List<Vector2>();
        for (int i = 0; i < target; i++)
        {
            fallback.Add(new Vector2(Range(rng, rect.xMin, rect.xMax), Range(rng, rect.yMin, rect.yMax)));
        }
        return fallback;
    }

    /// <summary>Greedy farthest-point sampling: maximises the minimum distance between the picked sites.</summary>
    private static List<Vector2> FarthestPointSubset(List<Vector2> candidates, int count, Vector2 centre)
    {
        var picked = new List<Vector2>();
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            float d = (candidates[i] - centre).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        picked.Add(candidates[best]);

        var minDist = new float[candidates.Count];
        for (int i = 0; i < candidates.Count; i++) minDist[i] = (candidates[i] - picked[0]).sqrMagnitude;

        while (picked.Count < count)
        {
            int next = -1;
            float far = -1f;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (minDist[i] > far) { far = minDist[i]; next = i; }
            }
            if (next < 0) break;
            picked.Add(candidates[next]);
            minDist[next] = -1f;
            for (int i = 0; i < candidates.Count; i++)
            {
                float d = (candidates[i] - candidates[next]).sqrMagnitude;
                if (d < minDist[i]) minDist[i] = d;
            }
        }
        return picked;
    }

    /// <summary>Bridson's Poisson-disk sampling (dart throwing + active list).</summary>
    private static List<Vector2> Bridson(Rect rect, float radius, System.Random rng)
    {
        var points = new List<Vector2>();
        var active = new List<int>();
        float cell = radius / Mathf.Sqrt(2f);
        int gw = Mathf.Max(1, Mathf.CeilToInt(rect.width / cell));
        int gh = Mathf.Max(1, Mathf.CeilToInt(rect.height / cell));
        var grid = new int[gw * gh];
        for (int i = 0; i < grid.Length; i++) grid[i] = -1;

        Vector2 first = new Vector2(Range(rng, rect.xMin, rect.xMax), Range(rng, rect.yMin, rect.yMax));
        points.Add(first);
        active.Add(0);
        Place(grid, gw, gh, rect, cell, points, 0);

        int guard = 0;
        while (active.Count > 0 && guard++ < 4000)
        {
            int ai = rng.Next(active.Count);
            Vector2 origin = points[active[ai]];
            bool placed = false;
            for (int k = 0; k < 24; k++)
            {
                float ang = Range(rng, 0f, Mathf.PI * 2f);
                float dist = Range(rng, radius, radius * 2f);
                Vector2 cand = origin + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                if (!rect.Contains(cand)) continue;
                if (HasNeighbour(grid, gw, gh, rect, cell, points, cand, radius)) continue;

                points.Add(cand);
                active.Add(points.Count - 1);
                Place(grid, gw, gh, rect, cell, points, points.Count - 1);
                placed = true;
                break;
            }
            if (!placed) active.RemoveAt(ai);
        }
        return points;
    }

    private static int GridIndex(int gx, int gy, int gw, int gh) { return (gx < 0 || gy < 0 || gx >= gw || gy >= gh) ? -1 : gy * gw + gx; }

    private static void Place(int[] grid, int gw, int gh, Rect rect, float cell, List<Vector2> pts, int idx)
    {
        int gx = Mathf.Clamp((int)((pts[idx].x - rect.xMin) / cell), 0, gw - 1);
        int gy = Mathf.Clamp((int)((pts[idx].y - rect.yMin) / cell), 0, gh - 1);
        grid[gy * gw + gx] = idx;
    }

    private static bool HasNeighbour(int[] grid, int gw, int gh, Rect rect, float cell, List<Vector2> pts, Vector2 cand, float radius)
    {
        int gx = Mathf.Clamp((int)((cand.x - rect.xMin) / cell), 0, gw - 1);
        int gy = Mathf.Clamp((int)((cand.y - rect.yMin) / cell), 0, gh - 1);
        for (int y = gy - 2; y <= gy + 2; y++)
        {
            for (int x = gx - 2; x <= gx + 2; x++)
            {
                int id = GridIndex(x, y, gw, gh);
                if (id < 0 || grid[id] < 0) continue;
                if ((pts[grid[id]] - cand).sqrMagnitude < radius * radius) return true;
            }
        }
        return false;
    }

    private static float Range(System.Random rng, float a, float b) { return a + (float)rng.NextDouble() * (b - a); }

    // ---------------------------------------------------------------------------------------------
    //  2. Voronoi cell = rectangle clipped by every perpendicular bisector
    // ---------------------------------------------------------------------------------------------
    private static List<Vector2> VoronoiCell(Vector2 site, List<Vector2> sites, Rect rect)
    {
        var poly = new List<Vector2>
        {
            new Vector2(rect.xMin, rect.yMin),
            new Vector2(rect.xMax, rect.yMin),
            new Vector2(rect.xMax, rect.yMax),
            new Vector2(rect.xMin, rect.yMax),
        };

        foreach (Vector2 other in sites)
        {
            Vector2 n = other - site;
            if (n.sqrMagnitude < 1e-9f) continue;
            float c = (other.sqrMagnitude - site.sqrMagnitude) * 0.5f;   // keep dot(p, n) <= c
            poly = ClipHalfPlane(poly, n, c);
            if (poly.Count < 3) return null;
        }
        return poly;
    }

    private static List<Vector2> ClipHalfPlane(List<Vector2> poly, Vector2 n, float c)
    {
        var outPoly = new List<Vector2>();
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Count];
            float da = Vector2.Dot(a, n) - c;
            float db = Vector2.Dot(b, n) - c;
            if (da <= 0f) outPoly.Add(a);
            if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
            {
                float t = da / (da - db);
                outPoly.Add(Vector2.Lerp(a, b, t));
            }
        }
        return outPoly;
    }

    private static float SignedArea(List<Vector2> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }

    private static float Perimeter(List<Vector2> poly)
    {
        float p = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            p += Vector2.Distance(poly[i], poly[(i + 1) % poly.Count]);
        }
        return p;
    }

    // ---------------------------------------------------------------------------------------------
    //  3. inset (the kerf between neighbouring fragments)
    // ---------------------------------------------------------------------------------------------
    private static List<Vector2> InsetPolygon(List<Vector2> poly, float distance)
    {
        var src = new List<Vector2>(poly);
        if (SignedArea(src) < 0f) src.Reverse();          // make it counter-clockwise

        var lines = new List<Vector2[]>();                // each line: point + inward normal
        for (int i = 0; i < src.Count; i++)
        {
            Vector2 a = src[i], b = src[(i + 1) % src.Count];
            Vector2 dir = (b - a).normalized;
            Vector2 inward = new Vector2(-dir.y, dir.x);  // interior of a CCW polygon is to the left
            lines.Add(new[] { a + inward * distance, inward });
        }

        var outPoly = new List<Vector2>();
        for (int i = 0; i < lines.Count; i++)
        {
            Vector2[] l0 = lines[i];
            Vector2[] l1 = lines[(i + 1) % lines.Count];
            Vector2 p = l0[0], n0 = l0[1], q = l1[0], n1 = l1[1];
            float denom = n0.x * n1.y - n0.y * n1.x;
            if (Mathf.Abs(denom) < 1e-6f) return null;    // parallel edges -> degenerate
            float t = ((q.x - p.x) * n1.y - (q.y - p.y) * n1.x) / denom;
            outPoly.Add(p + n0 * t);
        }
        return outPoly.Count >= 3 ? outPoly : null;
    }

    // ---------------------------------------------------------------------------------------------
    //  4. edge subdivision + Perlin relief
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// Splits every edge of <paramref name="inner"/> into coarse segments (a low point count keeps the
    /// break lines angular instead of suspiciously smooth) and applies bounded Perlin noise along the
    /// outward edge normal. The noise is sampled at the matching point of <paramref name="source"/>,
    /// the original Voronoi boundary shared with the neighbouring fragment, so both sides of a crack
    /// receive the same relief mirrored about it.
    /// </summary>
    private static Vector2[] Subdivide(List<Vector2> inner, List<Vector2> source, float step,
                                       float amplitude, float scale, float noiseOffset,
                                       out int[] cornerIndices)
    {
        var pts = new List<Vector2>();
        var corners = new List<int>();
        for (int i = 0; i < inner.Count; i++)
        {
            Vector2 a = inner[i], b = inner[(i + 1) % inner.Count];
            Vector2 dir = (b - a);
            float len = dir.magnitude;
            if (len < 1e-6f) continue;
            dir /= len;
            Vector2 outward = new Vector2(dir.y, -dir.x);   // polygon is CCW, so this points outwards
            corners.Add(pts.Count);                          // this vertex is a polygon corner

            Vector2 sa = source[i], sb = source[(i + 1) % source.Count];
            int n = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.05f, step)));
            for (int k = 0; k < n; k++)
            {
                float t = k / (float)n;
                Vector2 p = Vector2.Lerp(a, b, t);
                if (amplitude > 0f)
                {
                    Vector2 sample = Vector2.Lerp(sa, sb, t);   // same physical point for both neighbours
                    float noise = Mathf.PerlinNoise(sample.x * scale + noiseOffset,
                                                    sample.y * scale + noiseOffset);
                    p += outward * ((noise - 0.5f) * 2f * amplitude);
                }
                pts.Add(p);
            }
        }
        cornerIndices = corners.ToArray();
        return pts.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    //  5. loft: outline (y = 0) -> outline (y = thickness)
    // ---------------------------------------------------------------------------------------------
    private static List<Vector3[]> BuildPrism(Vector2[] bottom, Vector2[] top, float thickness,
                                              float verticalNoise)
    {
        var faces = new List<Vector3[]>();
        int n = bottom.Length;

        var bottomFace = new Vector3[n];
        var topFace = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            bottomFace[i] = new Vector3(bottom[n - 1 - i].x, 0f, bottom[n - 1 - i].y);  // reversed -> faces down
            float y = thickness;
            if (verticalNoise > 0f)
            {
                // Uneven top face ("as cast" concrete): the underside stays flat so slabs still rest.
                float v = Mathf.PerlinNoise(top[i].x * 1.7f + 11f, top[i].y * 1.7f + 5f);
                y = thickness * (1f + (v - 0.5f) * 2f * verticalNoise);
            }
            topFace[i] = new Vector3(top[i].x, y, top[i].y);
        }
        faces.Add(bottomFace);
        faces.Add(topFace);

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            faces.Add(new[]
            {
                new Vector3(bottom[i].x, 0f, bottom[i].y),
                new Vector3(bottom[j].x, 0f, bottom[j].y),
                topFace[j],
                topFace[i],
            });
        }
        OrientOutwards(faces);
        return faces;
    }

    private static void OrientOutwards(List<Vector3[]> faces)
    {
        Vector3 c = Vector3.zero;
        int count = 0;
        foreach (Vector3[] f in faces) foreach (Vector3 v in f) { c += v; count++; }
        if (count == 0) return;
        c /= count;

        for (int i = 0; i < faces.Count; i++)
        {
            Vector3[] f = faces[i];
            if (f.Length < 3) continue;
            Vector3 n = Vector3.Cross(f[1] - f[0], f[2] - f[0]);
            if (Vector3.Dot(n, f[0] - c) < 0f) System.Array.Reverse(f);
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  6. corner breaking: shear sharp corners off with one planar cut
    // ---------------------------------------------------------------------------------------------
    private static int BreakCorners(List<Vector3[]> faces, Vector2[] outline, int[] cornerIdx,
                                    float thickness, Settings s, System.Random rng)
    {
        int cuts = 0;
        float depth = s.cornerDepth > 0f ? s.cornerDepth : thickness * 0.5f;
        var order = new List<int>(cornerIdx);
        order.Sort((a, b) => CornerAngle(outline, a).CompareTo(CornerAngle(outline, b)));

        foreach (int idx in order)
        {
            float angle = CornerAngle(outline, idx);
            if (angle <= 1f || angle >= 90f) continue;                       // only 0..90 degree corners
            // Needle sharp corners are always cut: two triangular slabs balancing tip to tip
            // looked absurd, and a needle tip carries no useful geometry anyway.
            float chance = angle < 50f ? 1f : s.cornerChance * (90f - angle) / 90f;
            if ((float)rng.NextDouble() > chance) continue;

            int n = outline.Length;
            Vector2 prev = outline[(idx - 1 + n) % n];
            Vector2 next = outline[(idx + 1) % n];
            Vector2 corner = outline[idx];
            Vector2 u = (prev - corner).normalized * depth;
            Vector2 v = (next - corner).normalized * depth;

            Vector3 p0 = new Vector3(corner.x + u.x, 0f, corner.y + u.y);
            Vector3 p1 = new Vector3(corner.x + v.x, 0f, corner.y + v.y);
            Vector3 p2 = new Vector3(corner.x, depth, corner.y);
            Vector3 nrm = Vector3.Cross(p1 - p0, p2 - p0).normalized;

            Vector3 inside = Vector3.zero;
            int cnt = 0;
            foreach (Vector3[] f in faces) foreach (Vector3 p in f) { inside += p; cnt++; }
            if (cnt > 0) inside /= cnt;
            if (Vector3.Dot(nrm, p0 - inside) < 0f) nrm = -nrm;              // point outwards

            ClipConvex(faces, p0, nrm);
            cuts++;
        }
        return cuts;
    }

    private static float CornerAngle(Vector2[] outline, int idx)
    {
        int n = outline.Length;
        Vector2 c = outline[idx];
        Vector2 a = (outline[(idx - 1 + n) % n] - c).normalized;
        Vector2 b = (outline[(idx + 1) % n] - c).normalized;
        return Vector2.Angle(a, b);
    }

    /// <summary>Clips every face against the half space dot(n, p - p0) &lt;= 0 and caps the opening.</summary>
    private static void ClipConvex(List<Vector3[]> faces, Vector3 p0, Vector3 n)
    {
        var newFaces = new List<Vector3[]>();
        var cutPoints = new List<Vector3>();
        var cutDirs = new List<Vector3>();

        foreach (Vector3[] face in faces)
        {
            var poly = new List<Vector3>();
            for (int i = 0; i < face.Length; i++)
            {
                Vector3 a = face[i];
                Vector3 b = face[(i + 1) % face.Length];
                float da = Vector3.Dot(n, a - p0);
                float db = Vector3.Dot(n, b - p0);
                if (da <= 0f) poly.Add(a);
                if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
                {
                    float t = da / (da - db);
                    Vector3 hit = Vector3.Lerp(a, b, t);
                    poly.Add(hit);
                    AddUnique(cutPoints, cutDirs, hit, (b - a).normalized);
                }
            }
            if (poly.Count >= 3) newFaces.Add(poly.ToArray());
        }

        if (cutPoints.Count >= 3)
        {
            Vector3 mid = Vector3.zero;
            foreach (Vector3 p in cutPoints) mid += p;
            mid /= cutPoints.Count;

            Vector3 axis = n.normalized;
            Vector3 refDir = Vector3.ProjectOnPlane(cutPoints[0] - mid, axis).normalized;
            Vector3 bitangent = Vector3.Cross(axis, refDir);
            cutPoints.Sort((a, b) =>
            {
                Vector3 da = Vector3.ProjectOnPlane(a - mid, axis).normalized;
                Vector3 db = Vector3.ProjectOnPlane(b - mid, axis).normalized;
                return Mathf.Atan2(Vector3.Dot(da, bitangent), Vector3.Dot(da, refDir))
                    .CompareTo(Mathf.Atan2(Vector3.Dot(db, bitangent), Vector3.Dot(db, refDir)));
            });
            if (Vector3.Dot(Vector3.Cross(cutPoints[1] - cutPoints[0], cutPoints[2] - cutPoints[0]), n) < 0f)
            {
                cutPoints.Reverse();
            }
            newFaces.Add(cutPoints.ToArray());
        }

        faces.Clear();
        faces.AddRange(newFaces);
    }

    private static void AddUnique(List<Vector3> pts, List<Vector3> dirs, Vector3 p, Vector3 d)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if ((pts[i] - p).sqrMagnitude < 1e-8f) return;
        }
        pts.Add(p);
        dirs.Add(d);
    }

    // ---------------------------------------------------------------------------------------------
    //  mesh assembly
    // ---------------------------------------------------------------------------------------------
    private static Slab ToSlab(List<Vector3[]> faces, float thickness, int paletteBlock, float footprint)
    {
        // 1. triangulate every face (ear clipping handles the non convex caps)
        var soup = new List<Vector3[]>();
        foreach (Vector3[] face in faces)
        {
            if (face.Length < 3) continue;
            var local = new List<int>();
            EarClip(new List<Vector3>(face), local);
            for (int i = 0; i + 2 < local.Count; i += 3)
            {
                soup.Add(new[] { face[local[i]], face[local[i + 1]], face[local[i + 2]] });
            }
        }
        if (soup.Count == 0) return default(Slab);

        // 2. interior reference point. A slab is a near convex solid, so every face must face away
        //    from it. Orienting each triangle on its own is robust and, unlike welding the mesh and
        //    letting Unity average the normals, keeps the slab flat shaded - averaging produced the
        //    mottled/gradient surfaces that looked wrong.
        Vector3 interior = Vector3.zero;
        int vertexCount = 0;
        foreach (Vector3[] t in soup)
        {
            interior += t[0]; interior += t[1]; interior += t[2];
            vertexCount += 3;
        }
        interior /= Mathf.Max(1, vertexCount);

        var verts = new List<Vector3>(vertexCount);
        var normals = new List<Vector3>(vertexCount);
        var uvs = new List<Vector2>(vertexCount);
        var tris = new List<int>(vertexCount);
        Rect block = PaletteBlocks[Mathf.Clamp(paletteBlock, 0, PaletteBlocks.Length - 1)];

        Bounds bounds = new Bounds(soup[0][0], Vector3.zero);
        foreach (Vector3[] t in soup)
        {
            bounds.Encapsulate(t[0]); bounds.Encapsulate(t[1]); bounds.Encapsulate(t[2]);
        }
        float spanX = Mathf.Max(1e-4f, bounds.size.x);
        float spanZ = Mathf.Max(1e-4f, bounds.size.z);

        foreach (Vector3[] t in soup)
        {
            Vector3 a = t[0], b = t[1], c = t[2];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-10f) continue;                 // degenerate triangle
            if (Vector3.Dot(n, (a + b + c) / 3f - interior) < 0f)
            {
                Vector3 tmp = b; b = c; c = tmp;                   // face inwards -> flip
                n = Vector3.Cross(b - a, c - a);
            }
            n.Normalize();

            int start = verts.Count;
            AddVertex(verts, normals, uvs, a, n, block, bounds, spanX, spanZ);
            AddVertex(verts, normals, uvs, b, n, block, bounds, spanX, spanZ);
            AddVertex(verts, normals, uvs, c, n, block, bounds, spanX, spanZ);
            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
        }
        if (tris.Count < 3) return default(Slab);

        float volume = SignedVolume(verts, tris);
        if (volume < 0f)                                           // whole mesh inside out: flip all
        {
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int tmp = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = tmp;
            }
            volume = -volume;
        }

        // 3. re-centre so the transform position is the visual centre
        Vector3 centre = bounds.center;
        for (int i = 0; i < verts.Count; i++) verts[i] -= centre;

        var mesh = new Mesh();
        mesh.name = "procedural_slab";
        mesh.indexFormat = verts.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);                                  // flat, per face - no averaging
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        Slab slab;
        slab.mesh = mesh;
        slab.center = centre;
        slab.size = new Vector2(bounds.size.x, bounds.size.z);
        slab.footprint = footprint;
        slab.volume = volume;
        return slab;
    }

    private static void AddVertex(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
                                  Vector3 p, Vector3 n, Rect block, Bounds bounds, float spanX, float spanZ)
    {
        verts.Add(p);
        normals.Add(n);
        uvs.Add(new Vector2(block.xMin + block.width * (p.x - bounds.min.x) / spanX,
                            block.yMin + block.height * (p.z - bounds.min.z) / spanZ));
    }

    private static float SignedVolume(List<Vector3> verts, List<int> tris)
    {
        double vol = 0.0;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            vol += Vector3.Dot(verts[tris[i]], Vector3.Cross(verts[tris[i + 1]], verts[tris[i + 2]])) / 6.0;
        }
        return (float)vol;
    }

    // ---------------------------------------------------------------------------------------------
    //  ear clipping (handles the non convex caps produced by the edge noise and corner cuts)
    // ---------------------------------------------------------------------------------------------
    private static void EarClip(List<Vector3> poly, List<int> outTris)
    {
        int n = poly.Count;
        if (n < 3) return;
        if (n == 3)
        {
            outTris.Add(0); outTris.Add(1); outTris.Add(2);
            return;
        }

        Vector3 normal = PolygonNormal(poly);
        Vector3 u = Vector3.ProjectOnPlane(poly[1] - poly[0], normal);
        if (u.sqrMagnitude < 1e-10f) u = Vector3.ProjectOnPlane(poly[2] - poly[0], normal);
        if (u.sqrMagnitude < 1e-10f) { FanTriangulate(n, outTris); return; }
        u.Normalize();
        Vector3 v = Vector3.Cross(normal, u);

        var p2 = new Vector2[n];
        for (int i = 0; i < n; i++) p2[i] = new Vector2(Vector3.Dot(poly[i], u), Vector3.Dot(poly[i], v));

        bool flip = SignedArea2(p2) < 0f;
        var idx = new List<int>(n);
        if (flip) { for (int i = n - 1; i >= 0; i--) idx.Add(i); }
        else { for (int i = 0; i < n; i++) idx.Add(i); }

        var local = new List<int>();
        int guard = 0;
        while (idx.Count > 3 && guard++ < 4 * n + 16)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int a = idx[(i - 1 + idx.Count) % idx.Count];
                int b = idx[i];
                int c = idx[(i + 1) % idx.Count];
                if (!IsEar(p2, idx, a, b, c)) continue;
                local.Add(a); local.Add(b); local.Add(c);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;                  // degenerate polygon: fall back to a fan below
        }
        for (int i = 1; i + 1 < idx.Count; i++)
        {
            local.Add(idx[0]); local.Add(idx[i]); local.Add(idx[i + 1]);
        }

        if (flip)
        {
            for (int i = 0; i + 2 < local.Count; i += 3)
            {
                int tmp = local[i + 1]; local[i + 1] = local[i + 2]; local[i + 2] = tmp;
            }
        }
        outTris.AddRange(local);
    }

    private static void FanTriangulate(int n, List<int> outTris)
    {
        for (int i = 1; i + 1 < n; i++) { outTris.Add(0); outTris.Add(i); outTris.Add(i + 1); }
    }

    private static bool IsEar(Vector2[] p, List<int> idx, int a, int b, int c)
    {
        float cross = Cross2(p[b] - p[a], p[c] - p[b]);
        if (cross <= 1e-9f) return false;                 // reflex or degenerate corner
        for (int i = 0; i < idx.Count; i++)
        {
            int k = idx[i];
            if (k == a || k == b || k == c) continue;
            if (PointInTriangle(p[k], p[a], p[b], p[c])) return false;
        }
        return true;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross2(b - a, p - a);
        float d2 = Cross2(c - b, p - b);
        float d3 = Cross2(a - c, p - c);
        bool neg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool pos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(neg && pos);
    }

    private static float Cross2(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }

    private static float SignedArea2(Vector2[] p)
    {
        float a = 0f;
        for (int i = 0; i < p.Length; i++)
        {
            Vector2 q = p[i], r = p[(i + 1) % p.Length];
            a += q.x * r.y - r.x * q.y;
        }
        return a * 0.5f;
    }

    private static Vector3 PolygonNormal(List<Vector3> poly)
    {
        Vector3 n = Vector3.zero;                          // Newell's method
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
            n.x += (a.y - b.y) * (a.z + b.z);
            n.y += (a.z - b.z) * (a.x + b.x);
            n.z += (a.x - b.x) * (a.y + b.y);
        }
        return n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
    }

    /// <summary>Signed volume of a closed triangle mesh (divergence theorem).</summary>
    public static float MeshVolume(Mesh mesh)
    {
        Vector3[] v = mesh.vertices;
        int[] t = mesh.triangles;
        double vol = 0.0;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            vol += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
        }
        return (float)System.Math.Abs(vol);
    }
}
