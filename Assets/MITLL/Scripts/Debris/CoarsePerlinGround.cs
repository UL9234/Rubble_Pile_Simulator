using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Sparse Perlin samples joined by planar triangles, shared by rendering and physics.</summary>
public sealed class CoarsePerlinGround : MonoBehaviour
{
    public float MinHeight { get; private set; }
    public float MaxHeight { get; private set; }
    private Mesh mesh;
    private MeshCollider surface;
    private Material material;
    private GameObject original;

    public static CoarsePerlinGround Create(Bounds spawnBounds, int seed)
    {
        if (!CustomArgs.FloatToBool(CustomArgs.GetWithDefault("terrainground", 1))) return null;
        var ground = new GameObject("CoarsePerlinGround").AddComponent<CoarsePerlinGround>();
        ground.Build(spawnBounds, seed);
        return ground;
    }

    private void Build(Bounds spawnBounds, int seed)
    {
        float size = Mathf.Clamp(CustomArgs.GetWithDefault("terrainsize", 80), 8, 1000);
        size = Mathf.Max(size, Mathf.Max(spawnBounds.size.x, spawnBounds.size.z) + 20);
        float step = Mathf.Max(0.25f, CustomArgs.GetWithDefault("terrainstep", 2));
        int cells = Mathf.Clamp(Mathf.CeilToInt(size / step), 2, 256);
        step = size / cells;
        float amplitude = Mathf.Max(0, CustomArgs.GetWithDefault("terrainamplitude", 2.4f));
        float frequency = Mathf.Max(0.0001f, CustomArgs.GetWithDefault("terrainfrequency", 0.12f));
        float baseY = CustomArgs.GetWithDefault("terrainbase", 0);
        // A separate PRNG preserves the existing debris random sequence.
        var rng = new System.Random(seed ^ 0x47a12);
        float ox = 100 + (float)rng.NextDouble() * 1000;
        float oz = 100 + (float)rng.NextDouble() * 1000;
        float reference = Mathf.PerlinNoise(ox, oz);
        var samples = new Vector3[cells + 1, cells + 1];
        MinHeight = float.PositiveInfinity;
        MaxHeight = float.NegativeInfinity;
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            float px = x * step - size * 0.5f;
            float pz = z * step - size * 0.5f;
            float h = baseY + amplitude * (Mathf.PerlinNoise(ox + px * frequency, oz + pz * frequency) - reference);
            samples[x, z] = new Vector3(px + spawnBounds.center.x, h, pz + spawnBounds.center.z);
            MinHeight = Mathf.Min(MinHeight, h);
            MaxHeight = Mathf.Max(MaxHeight, h);
        }

        // Duplicate triangle vertices for flat normals: interpolation is geometric, never smoothed.
        var vertices = new List<Vector3>(cells * cells * 6);
        var uv = new List<Vector2>(cells * cells * 6);
        var triangles = new List<int>(cells * cells * 6);
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            AddTriangle(samples[x, z], samples[x, z + 1], samples[x + 1, z], vertices, uv, triangles);
            AddTriangle(samples[x + 1, z], samples[x, z + 1], samples[x + 1, z + 1], vertices, uv, triangles);
        }
        mesh = new Mesh { name = "SparsePerlinSurface", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = gameObject.AddComponent<MeshRenderer>();
        original = GameObject.Find("GroundPlane");
        var oldRenderer = original != null ? original.GetComponent<Renderer>() : null;
        material = oldRenderer != null ? new Material(oldRenderer.sharedMaterial)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        // The old 2500 m plane used 500 texture repeats. Our UVs are in world metres.
        if (material.HasProperty("_BaseMap")) material.SetTextureScale("_BaseMap", Vector2.one * 0.8f);
        if (material.HasProperty("_MainTex")) material.SetTextureScale("_MainTex", Vector2.one * 0.8f);
        renderer.sharedMaterial = material;
        surface = gameObject.AddComponent<MeshCollider>();
        surface.sharedMesh = mesh;
        // Remove the old flat collision surface as well as its visible plane.
        if (original != null) original.SetActive(false);
        Physics.SyncTransforms();
        Debug.Log($"[CoarsePerlinGround] seed={seed} size={size:F1}m spacing={step:F2}m samples={(cells + 1) * (cells + 1)} triangles={triangles.Count / 3} height=[{MinHeight:F3},{MaxHeight:F3}] amplitude={amplitude:F2} frequency={frequency:F3}");
    }

    private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, List<Vector3> vertices,
        List<Vector2> uv, List<int> indices)
    {
        foreach (Vector3 p in new[] { a, b, c })
        {
            indices.Add(vertices.Count);
            vertices.Add(p);
            uv.Add(new Vector2(p.x, p.z) * 0.25f);
        }
    }

    public bool Sample(Vector3 position, out RaycastHit hit)
    {
        // Query only this collider so debris and the victim never affect ground height queries.
        return surface.Raycast(new Ray(new Vector3(position.x, MaxHeight + 1, position.z), Vector3.down),
            out hit, MaxHeight - MinHeight + 2);
    }

    public void PlaceOnSurface(GameObject body)
    {
        if (Sample(body.transform.position, out RaycastHit hit))
            body.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * body.transform.rotation;
        // Fit the actual body vertices, not its oversized rotated bounding box.
        float lift = float.NegativeInfinity;
        foreach (MeshFilter filter in body.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null || !filter.sharedMesh.isReadable) continue;
            foreach (Vector3 vertex in filter.sharedMesh.vertices)
            {
                Vector3 world = filter.transform.TransformPoint(vertex);
                if (Sample(world, out hit)) lift = Mathf.Max(lift, hit.point.y - world.y);
            }
        }
        if (!float.IsNegativeInfinity(lift)) body.transform.position += Vector3.up * (lift + 0.005f);
        else if (Sample(body.transform.position, out hit)) body.transform.position += Vector3.up * hit.point.y;
    }

    private void OnDestroy()
    {
        if (original != null) original.SetActive(true);
        if (mesh != null) Destroy(mesh);
        if (material != null) Destroy(material);
    }
}
