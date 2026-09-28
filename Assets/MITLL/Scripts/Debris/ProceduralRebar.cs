// DISTRIBUTION STATEMENT A. Approved for public release. Distribution is unlimited.
//  
// This material is based upon work supported by the Air Force under Air Force Contract No. FA8702-15-D-0001. Any opinions, findings, conclusions or recommendations expressed in this material are those of the author(s) and do not necessarily reflect the views of the Department of the Air Force.
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
/// Exposed reinforcement bars on the procedural slab fragments.
///
/// A bar is *not* a connector that has to reach the neighbouring fragment. It is a piece of
/// reinforcement that grows out of one broken crack face:
///
///   * One virtual grid per layer lies horizontally through the slab at mid thickness, strictly
///     parallel / perpendicular to the generation boundary. Where a grid line crosses the crack
///     between two neighbouring fragments it marks a *pair*: one bar root on each side.
///   * Every root grows its own bar. The growth length is drawn at random up to
///     <see cref="MaxRebarLength"/> - that cap is the upper bound of the *random growth length* of
///     one bar, never a check on the resulting arc length, and a bar is never discarded for being
///     long or for not reaching the other side.
///   * A bar runs straight out of the concrete for <see cref="StraightRun"/> before it may bend; the
///     bend is a quadratic fillet whose direction turns towards the paired root across the crack.
///     The root direction lives in the slab plane, i.e. parallel to the top and bottom faces.
///   * Each bar is a child of the fragment it grows from, with geometry stored in that fragment's
///     local space: it is part of that fragment, moves with it as one entity, and carries a capsule
///     collider so the steel has real collision volume.
/// </summary>
public static class ProceduralRebar
{
    public struct Fragment
    {
        public Vector2[] outline;        // mid height outline in the fragment's local XZ space (CCW)
        public Vector3 plannedPosition;  // where the fragment is placed (mesh centre, mid plane)
    }

    private struct Crossing
    {
        public int fragment;
        public float t;                  // position along the grid line
        public Vector2 point;
        public Vector2 inward;           // inward normal of the crossed crack face
    }

    internal struct Stub
    {
        public int fragment;             // fragment this bar belongs to
        public Vector3[] localPoints;    // bar centreline in that fragment's local space
        public float radius;
    }

    public sealed class Plan
    {
        private readonly List<Stub> stubs = new List<Stub>();
        internal void Add(Stub stub) { stubs.Add(stub); }
        internal List<Stub> Items { get { return stubs; } }
        public int Count { get { return stubs.Count; } }
    }

    public struct BuildStats
    {
        public int bars;                 // bars turned into geometry
        public int fragments;            // fragments in this layer
        public int fragmentsWithBars;    // fragments that carry at least one bar
        public int minBars, maxBars;     // bars per fragment
        public float minLength, maxLength;
    }

    private const int Sides = 6;                  // tube cross section
    private const int Rings = 8;                  // rings per bar
    private const float PairGapMax = 0.35f;       // two crossings this close belong to the same crack
    private const float MaxRebarLength = 0.20f;   // upper bound of the random growth length of one bar
    private const float MinRebarLength = 0.10f;   // and its lower bound (shorter bars are invisible)
    private const float StraightRun = 0.10f;      // m a bar runs straight out of the concrete

    /// <summary>
    /// Marks the grid/crack crossing pairs and grows one bar from each side of every pair. Called
    /// *before* the fragments are released, using the positions they are about to be placed at, so
    /// the crack geometry is exactly the intact-layer geometry.
    /// </summary>
    public static Plan Create(Fragment[] fragments, Vector3 planeCentre, float planeSizeX,
                              float planeSizeZ, float grid, float embedMin, float embedMax,
                              float radiusMin, float radiusMax, float sweepMin, float sweepMax,
                              System.Random rng)
    {
        var plan = new Plan();
        if (fragments == null || fragments.Length < 2) return plan;

        var world = new Vector2[fragments.Length][];
        for (int i = 0; i < fragments.Length; i++)
        {
            Vector2[] src = fragments[i].outline;
            if (src == null || src.Length < 3) { world[i] = new Vector2[0]; continue; }
            var dst = new Vector2[src.Length];
            Vector3 p = fragments[i].plannedPosition;
            for (int k = 0; k < src.Length; k++) dst[k] = new Vector2(p.x + src[k].x, p.z + src[k].y);
            world[i] = dst;
        }

        float x0 = planeCentre.x - planeSizeX * 0.5f;
        float x1 = planeCentre.x + planeSizeX * 0.5f;
        float z0 = planeCentre.z - planeSizeZ * 0.5f;
        float z1 = planeCentre.z + planeSizeZ * 0.5f;

        var crossings = new List<Crossing>();
        for (int axis = 0; axis < 2; axis++)
        {
            float lo = axis == 0 ? x0 : z0;
            float hi = axis == 0 ? x1 : z1;
            float phase = (float)rng.NextDouble() * grid;      // each layer gets its own bar layout
            for (float c = lo + phase; c <= hi; c += grid)
            {
                crossings.Clear();
                for (int f = 0; f < fragments.Length; f++) AddCrossings(world[f], f, axis, c, crossings);
                crossings.Sort((p, q) => p.t.CompareTo(q.t));

                for (int i = 0; i + 1 < crossings.Count; i++)
                {
                    Crossing ca = crossings[i];
                    Crossing cb = crossings[i + 1];
                    if (ca.fragment == cb.fragment) continue;
                    if (Vector2.Distance(ca.point, cb.point) > PairGapMax) continue;
                    GrowBar(plan, fragments, ca, cb, planeCentre.y, embedMin, embedMax,
                            radiusMin, radiusMax, rng);
                    i++;                                       // both crossings are used up
                }
            }
        }
        return plan;
    }

    /// <summary>Grows one bar from each side of a crack crossing pair.</summary>
    private static void GrowBar(Plan plan, Fragment[] frags, Crossing ca, Crossing cb, float planeY,
                                float embedMin, float embedMax, float radiusMin, float radiusMax,
                                System.Random rng)
    {
        Fragment fa = frags[ca.fragment];
        Fragment fb = frags[cb.fragment];

        float embedA = Mathf.Lerp(embedMin, embedMax, (float)rng.NextDouble());
        float embedB = Mathf.Lerp(embedMin, embedMax, (float)rng.NextDouble());

        Vector3 rootA = new Vector3(ca.point.x + ca.inward.x * embedA, planeY,
                                    ca.point.y + ca.inward.y * embedA);
        Vector3 rootB = new Vector3(cb.point.x + cb.inward.x * embedB, planeY,
                                    cb.point.y + cb.inward.y * embedB);

        // Root direction: out of the crack face and inside the slab plane, i.e. parallel to the top
        // and bottom faces. In the fragment's local frame the slab plane is horizontal.
        Vector3 dirA = new Vector3(-ca.inward.x, 0f, -ca.inward.y);
        Vector3 dirB = new Vector3(-cb.inward.x, 0f, -cb.inward.y);
        dirA = dirA.sqrMagnitude > 1e-8f ? dirA.normalized : Vector3.forward;
        dirB = dirB.sqrMagnitude > 1e-8f ? dirB.normalized : Vector3.forward;

        plan.Add(BuildStub(ca.fragment, fa.plannedPosition, rootA, dirA, rootB,
                           Mathf.Lerp(radiusMin, radiusMax, (float)rng.NextDouble()),
                           RandomLength(rng)));
        plan.Add(BuildStub(cb.fragment, fb.plannedPosition, rootB, dirB, rootA,
                           Mathf.Lerp(radiusMin, radiusMax, (float)rng.NextDouble()),
                           RandomLength(rng)));
    }

    private static float RandomLength(System.Random rng)
    {
        return Mathf.Lerp(MinRebarLength, MaxRebarLength, (float)rng.NextDouble());
    }

    /// <summary>
    /// Builds one bar centreline in its fragment's local space: a straight run of
    /// <see cref="StraightRun"/> along the root direction, then a quadratic fillet that turns towards
    /// the paired root. The drawn growth length is the bar's own length budget.
    /// </summary>
    private static Stub BuildStub(int fragmentIndex, Vector3 fragmentPosition, Vector3 root,
                                  Vector3 rootDir, Vector3 partnerRoot, float radius, float length)
    {
        Vector3 localRoot = root - fragmentPosition;
        Vector3 localPartner = partnerRoot - fragmentPosition;

        Vector3 target = localPartner - localRoot;
        target.y = 0f;                                     // both roots sit in the same cast plane
        target = target.sqrMagnitude > 1e-8f ? target.normalized : rootDir;

        float run = Mathf.Min(StraightRun, length);
        Vector3 bendStart = localRoot + rootDir * run;
        float remaining = length - run;

        Vector3 aim = Vector3.Slerp(rootDir, target, 0.85f).normalized;

        var points = new Vector3[Rings];
        // sample the composite (straight + fillet) generously, then resample by arc length
        const int coarse = 24;
        var raw = new List<Vector3>(coarse + 1);
        raw.Add(localRoot);
        raw.Add(bendStart);
        for (int i = 1; i <= coarse; i++)
        {
            float t = i / (float)coarse;
            Vector3 control = bendStart + rootDir * (remaining * 0.5f);
            Vector3 end = bendStart + aim * remaining;
            raw.Add(Quadratic(bendStart, control, end, t));
        }

        // keep the bar close to its drawn length
        float measured = PolylineLength(raw);
        if (measured > 1e-4f)
        {
            float k = length / measured;
            for (int i = 2; i < raw.Count; i++) raw[i] = bendStart + (raw[i] - bendStart) * k;
        }

        Resample(raw, points);

        Stub stub;
        stub.fragment = fragmentIndex;
        stub.localPoints = points;
        stub.radius = radius;
        return stub;
    }

    private static Vector3 Quadratic(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    private static float PolylineLength(List<Vector3> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
        return total;
    }

    /// <summary>Resamples a polyline into exactly <see cref="Rings"/> equally spaced points.</summary>
    private static void Resample(List<Vector3> source, Vector3[] destination)
    {
        int count = destination.Length;
        if (source.Count < 2)
        {
            for (int i = 0; i < count; i++) destination[i] = source[0];
            return;
        }

        var cumulative = new float[source.Count];
        for (int i = 1; i < source.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector3.Distance(source[i - 1], source[i]);
        }
        float total = cumulative[source.Count - 1];

        destination[0] = source[0];
        destination[count - 1] = source[source.Count - 1];
        int cursor = 1;
        for (int i = 1; i < count - 1; i++)
        {
            float want = total * i / (float)(count - 1);
            while (cursor < source.Count - 1 && cumulative[cursor] < want) cursor++;
            float span = cumulative[cursor] - cumulative[cursor - 1];
            float k = span > 1e-6f ? (want - cumulative[cursor - 1]) / span : 0f;
            destination[i] = Vector3.Lerp(source[cursor - 1], source[cursor], k);
        }
    }

    /// <summary>
    /// Attaches the planned bars: one child per bar under its own fragment, with a tube mesh in the
    /// fragment's local space and a capsule collider along it.
    /// </summary>
    public static BuildStats Build(Plan plan, Transform[] transforms, Material material)
    {
        var stats = new BuildStats();
        if (plan == null || transforms == null) return stats;

        stats.fragments = transforms.Length;
        stats.minBars = int.MaxValue;
        stats.minLength = float.MaxValue;
        var perFragment = new int[transforms.Length];

        foreach (Stub stub in plan.Items)
        {
            if (stub.fragment >= transforms.Length) continue;
            Transform parent = transforms[stub.fragment];
            if (parent == null) continue;

            CreateBar(parent, stub, material);
            stats.bars++;
            perFragment[stub.fragment]++;
            float length = PolylineLength(new List<Vector3>(stub.localPoints));
            stats.minLength = Mathf.Min(stats.minLength, length);
            stats.maxLength = Mathf.Max(stats.maxLength, length);
        }

        for (int i = 0; i < perFragment.Length; i++)
        {
            if (perFragment[i] > 0) stats.fragmentsWithBars++;
            stats.minBars = Mathf.Min(stats.minBars, perFragment[i]);
            stats.maxBars = Mathf.Max(stats.maxBars, perFragment[i]);
        }
        if (stats.minBars == int.MaxValue) { stats.minBars = 0; stats.maxBars = 0; }
        if (stats.minLength == float.MaxValue) { stats.minLength = 0f; stats.maxLength = 0f; }
        return stats;
    }

    private static void CreateBar(Transform parent, Stub stub, Material material)
    {
        Vector3[] local = stub.localPoints;

        GameObject go = new GameObject("rebar");
        go.transform.SetParent(parent, false);
        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = BuildTube(local, stub.radius);
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        if (material != null) renderer.sharedMaterial = material;

        Vector3 dir = local[Rings - 1] - local[0];
        float length = dir.magnitude;
        if (length < 1e-4f) return;

        GameObject colliderObject = new GameObject("rebar_collider");
        colliderObject.transform.SetParent(parent, false);
        colliderObject.transform.localPosition = (local[0] + local[Rings - 1]) * 0.5f;
        colliderObject.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir / length);
        CapsuleCollider capsule = colliderObject.AddComponent<CapsuleCollider>();
        capsule.direction = 1;                                        // local Y
        capsule.radius = Mathf.Max(stub.radius, 0.005f);
        capsule.height = Mathf.Max(length, capsule.radius * 2f + 0.001f);
    }

    private static Mesh BuildTube(Vector3[] centres, float radius)
    {
        int sides = Sides;
        var verts = new List<Vector3>(Rings * sides + 2);
        var normals = new List<Vector3>(Rings * sides + 2);
        var tris = new List<int>((Rings - 1) * sides * 6 + sides * 6);

        Vector3 tangent0 = (centres[1] - centres[0]).normalized;
        Vector3 frameNormal = Vector3.ProjectOnPlane(Vector3.up, tangent0);
        if (frameNormal.sqrMagnitude < 1e-8f) frameNormal = Vector3.ProjectOnPlane(Vector3.right, tangent0);
        frameNormal.Normalize();

        for (int i = 0; i < Rings; i++)
        {
            Vector3 tangent;
            if (i == 0) tangent = (centres[1] - centres[0]).normalized;
            else if (i == Rings - 1) tangent = (centres[Rings - 1] - centres[Rings - 2]).normalized;
            else tangent = (centres[i + 1] - centres[i - 1]).normalized;

            Vector3 projected = Vector3.ProjectOnPlane(frameNormal, tangent);
            if (projected.sqrMagnitude > 1e-8f) frameNormal = projected.normalized;
            Vector3 binormal = Vector3.Cross(tangent, frameNormal);

            for (int s = 0; s < sides; s++)
            {
                float ang = (s / (float)sides) * Mathf.PI * 2f;
                Vector3 dir = frameNormal * Mathf.Cos(ang) + binormal * Mathf.Sin(ang);
                verts.Add(centres[i] + dir * radius);
                normals.Add(dir);
            }
        }

        int capA = verts.Count;
        verts.Add(centres[0]);
        normals.Add(-(centres[1] - centres[0]).normalized);
        int capB = verts.Count;
        verts.Add(centres[Rings - 1]);
        normals.Add((centres[Rings - 1] - centres[Rings - 2]).normalized);

        for (int ring = 0; ring + 1 < Rings; ring++)
        {
            for (int s = 0; s < sides; s++)
            {
                int i0 = ring * sides + s;
                int i1 = ring * sides + (s + 1) % sides;
                int i2 = (ring + 1) * sides + (s + 1) % sides;
                int i3 = (ring + 1) * sides + s;
                tris.Add(i0); tris.Add(i2); tris.Add(i1);
                tris.Add(i0); tris.Add(i3); tris.Add(i2);
            }
        }
        for (int s = 0; s < sides; s++)
        {
            int a0 = s, a1 = (s + 1) % sides;
            tris.Add(capA); tris.Add(a1); tris.Add(a0);
            int b0 = (Rings - 1) * sides + s, b1 = (Rings - 1) * sides + (s + 1) % sides;
            tris.Add(capB); tris.Add(b0); tris.Add(b1);
        }

        var mesh = new Mesh();
        mesh.name = "rebar";
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddCrossings(Vector2[] poly, int index, int axis, float c, List<Crossing> list)
    {
        if (poly == null || poly.Length < 3) return;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Length];
            float av = axis == 0 ? a.x : a.y;
            float bv = axis == 0 ? b.x : b.y;
            if ((av - c) * (bv - c) > 0f) continue;
            float denom = bv - av;
            if (Mathf.Abs(denom) < 1e-9f) continue;
            float u = (c - av) / denom;
            if (u < 0f || u > 1f) continue;

            Vector2 point = Vector2.Lerp(a, b, u);
            Vector2 dir = (b - a).normalized;
            Vector2 outward = new Vector2(dir.y, -dir.x);      // outlines are CCW
            Crossing crossing;
            crossing.fragment = index;
            crossing.t = axis == 0 ? point.y : point.x;
            crossing.point = point;
            crossing.inward = -outward;
            list.Add(crossing);
        }
    }
}
