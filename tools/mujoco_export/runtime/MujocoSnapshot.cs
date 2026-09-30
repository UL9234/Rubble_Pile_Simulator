// Authoritative source: tools/mujoco_export. Activated only by -mjexport <directory>.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class MujocoSnapshot : MonoBehaviour
{
    [Serializable] public class Body
    {
        public string id, path;
        public Vector3 position, scale;
        public Quaternion rotation;
        public bool active, hasRigidbody, isKinematic, useGravity, detectCollisions, sleeping;
        public float mass, drag, angularDrag, maxAngularVelocity, maxLinearVelocity, maxDepenetrationVelocity, sleepThreshold;
        public Vector3 worldCenterOfMass, inertiaTensor, velocity, angularVelocity, accumulatedForce, accumulatedTorque;
        public int includeLayers, excludeLayers;
        public bool automaticCenterOfMass, automaticInertiaTensor, accumulatedForcesValid;
        public Quaternion inertiaTensorRotation;
        public int constraints, collisionDetectionMode, interpolation, solverIterations, solverVelocityIterations;
    }
    [Serializable] public class Geometry
    {
        public string id, path, body, kind, mesh;
        public bool enabled, active, trigger, convex;
        public int layer, direction, includeLayers, excludeLayers, layerOverridePriority;
        public Vector3 center, size, scale;
        public Quaternion rotation;
        public float radius, height, contactOffset, staticFriction, dynamicFriction, bounciness;
        public int frictionCombine, bounceCombine;
        public string physicsMaterial;
    }
    [Serializable] public class MeshRecord
    {
        public string id;
        public Vector3[] vertices;
        public int[] triangles;
        public Vector2[] uv;
    }
    [Serializable] public class Visual
    {
        public string id, path, body, mesh, material, texture;
        public bool enabled, active;
        public Color color;
    }
    [Serializable] public class CameraRecord
    {
        public string id, path, body;
        public Vector3 position;
        public Quaternion rotation;
        public float fov, near, far;
        public bool orthographic, enabled;
    }
    [Serializable] public class Node
    {
        public string path;
        public bool active;
        public Vector3 position, scale;
        public Quaternion rotation;
        public string[] components;
    }
    [Serializable] public class IgnoredPair { public string first, second; }
    [Serializable] public class Snapshot
    {
        public int schemaVersion = 1, seed;
        public string unityVersion, scene, capturePhase = "before_debris_freeze", coordinateSystem = "Unity left-handed: X right, Y up, Z forward";
        public string[] arguments;
        public float time, fixedDeltaTime, captureDeltaTime, timeScale, defaultContactOffset, bounceThreshold;
        public Vector3 gravity, generationCenter, generationSize;
        public int[] layerCollisionMasks;
        public List<Body> bodies = new List<Body>();
        public List<Geometry> colliders = new List<Geometry>();
        public List<MeshRecord> meshes = new List<MeshRecord>();
        public List<Visual> visuals = new List<Visual>();
        public List<CameraRecord> cameras = new List<CameraRecord>();
        public List<Node> nodes = new List<Node>();
        public List<IgnoredPair> ignoredPairs = new List<IgnoredPair>();
        public List<string> unsupported = new List<string>();
    }

    private string output;
    private bool captured;
    private readonly Dictionary<Camera, bool> cameraEnabled = new Dictionary<Camera, bool>();
    private Snapshot snapshot;
    private readonly Dictionary<Transform, Body> owners = new Dictionary<Transform, Body>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string output = Argument("-mjexport");
        if (output == null) return;
        var go = new GameObject("MujocoSnapshotExporter");
        var exporter = go.AddComponent<MujocoSnapshot>();
        exporter.output = Path.GetFullPath(output);
        DebrisSpawner.BeforeFreeze += exporter.Capture;
        foreach (Camera camera in SceneComponents<Camera>())
        {
            exporter.cameraEnabled[camera] = camera.enabled;
            camera.enabled = false;
        }
        foreach (MonoBehaviour component in SceneComponents<MonoBehaviour>())
            if (component.GetType().Name == "RosSensorOutput" || component.GetType().Name == "RosPs5") component.enabled = false;
        // Deterministic simulation clock, no capture camera or graphical display required.
        Time.captureDeltaTime = 1f / 30f;
        Application.runInBackground = true;
    }

    private static string Argument(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static string PathOf(Transform t)
    {
        string result = t.name + "[" + t.GetSiblingIndex() + "]";
        while (t.parent != null) { t = t.parent; result = t.name + "[" + t.GetSiblingIndex() + "]/" + result; }
        return result;
    }
    private static T[] SceneComponents<T>() where T : Component
    {
        return Resources.FindObjectsOfTypeAll<T>().Where(c => c.gameObject.scene.IsValid())
            .OrderBy(c => PathOf(c.transform), StringComparer.Ordinal).ToArray();
    }
    private Body Owner(Transform t)
    {
        var rb = t.GetComponentInParent<Rigidbody>();
        Transform root = rb != null ? rb.transform : t;
        if (owners.TryGetValue(root, out Body result)) return result;
        result = new Body { id = "b" + owners.Count.ToString("D4"), path = PathOf(root),
            position = root.position, rotation = root.rotation, scale = root.lossyScale, active = root.gameObject.activeInHierarchy, hasRigidbody = rb != null };
        if (rb != null)
        {
            result.mass = rb.mass; result.drag = rb.drag; result.angularDrag = rb.angularDrag;
            result.isKinematic = rb.isKinematic; result.useGravity = rb.useGravity;
            result.detectCollisions = rb.detectCollisions; result.sleeping = rb.IsSleeping();
            result.worldCenterOfMass = rb.worldCenterOfMass;
            result.inertiaTensor = rb.inertiaTensor; result.inertiaTensorRotation = rb.inertiaTensorRotation;
            result.velocity = rb.velocity; result.angularVelocity = rb.angularVelocity;
            // PhysX uses a different internal state for kinematic bodies; accumulated
            // force getters are meaningful only for dynamic bodies (kinematics ignore forces).
            result.accumulatedForcesValid = !rb.isKinematic;
            if (result.accumulatedForcesValid)
            {
                result.accumulatedForce = rb.GetAccumulatedForce(Time.fixedDeltaTime);
                result.accumulatedTorque = rb.GetAccumulatedTorque(Time.fixedDeltaTime);
            }
            result.includeLayers = rb.includeLayers.value; result.excludeLayers = rb.excludeLayers.value;
            result.automaticCenterOfMass = rb.automaticCenterOfMass; result.automaticInertiaTensor = rb.automaticInertiaTensor;
            result.maxLinearVelocity = rb.maxLinearVelocity;
            result.constraints = (int)rb.constraints; result.collisionDetectionMode = (int)rb.collisionDetectionMode;
            result.interpolation = (int)rb.interpolation; result.maxAngularVelocity = rb.maxAngularVelocity;
            result.maxDepenetrationVelocity = rb.maxDepenetrationVelocity; result.sleepThreshold = rb.sleepThreshold;
            result.solverIterations = rb.solverIterations; result.solverVelocityIterations = rb.solverVelocityIterations;
        }
        owners.Add(root, result); snapshot.bodies.Add(result);
        return result;
    }
    private Vector3 LocalPoint(Body body, Vector3 point) => Quaternion.Inverse(body.rotation) * (point - body.position);

    private string AddMesh(Mesh mesh, Transform t, Body body, int submesh = -1)
    {
        if (mesh == null) throw new InvalidOperationException("Missing mesh: " + PathOf(t));
        // Access the read-only MeshData API: built-in primitives need not have Read/Write enabled.
        using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
        {
            var md = data[0];
            var positions = new Unity.Collections.NativeArray<Vector3>(md.vertexCount, Unity.Collections.Allocator.Temp);
            md.GetVertices(positions);
            var vertices = positions.ToArray(); positions.Dispose();
            for (int i = 0; i < vertices.Length; i++) vertices[i] = LocalPoint(body, t.TransformPoint(vertices[i]));
            var indices = new List<int>();
            int first = submesh < 0 ? 0 : submesh, last = submesh < 0 ? md.subMeshCount : submesh + 1;
            for (int s = first; s < last; s++)
            {
                var desc = md.GetSubMesh(s);
                if (desc.topology != UnityEngine.MeshTopology.Triangles)
                    throw new InvalidOperationException("Non-triangle mesh: " + PathOf(t));
                var ix = new Unity.Collections.NativeArray<int>(desc.indexCount, Unity.Collections.Allocator.Temp);
                md.GetIndices(ix, s); indices.AddRange(ix.ToArray()); ix.Dispose();
            }
            var uv = new Vector2[0];
            if (md.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
            {
                var tex = new Unity.Collections.NativeArray<Vector2>(md.vertexCount, Unity.Collections.Allocator.Temp);
                md.GetUVs(0, tex); uv = tex.ToArray(); tex.Dispose();
            }
            var record = new MeshRecord { id = "mesh" + snapshot.meshes.Count.ToString("D4"),
                vertices = vertices, triangles = indices.ToArray(), uv = uv };
            snapshot.meshes.Add(record); return record.id;
        }
    }

    private void Capture(DebrisSpawner spawner)
    {
        if (captured) return;
        captured = true;
        try
        {
            Physics.SyncTransforms();
            snapshot = new Snapshot { unityVersion = Application.unityVersion, scene = SceneManager.GetActiveScene().name,
                seed = RandomManager.Instance.seed, arguments = Environment.GetCommandLineArgs(),
                time = Time.time, fixedDeltaTime = Time.fixedDeltaTime, captureDeltaTime = Time.captureDeltaTime, timeScale = Time.timeScale,
                gravity = Physics.gravity, defaultContactOffset = Physics.defaultContactOffset,
                bounceThreshold = Physics.bounceThreshold, generationCenter = spawner.GenerationBounds.center,
                generationSize = spawner.GenerationBounds.size, layerCollisionMasks = new int[32] };
            for (int i = 0; i < 32; i++) for (int j = 0; j < 32; j++)
                if (!Physics.GetIgnoreLayerCollision(i, j)) snapshot.layerCollisionMasks[i] |= 1 << j;
            foreach (Transform t in SceneComponents<Transform>())
                snapshot.nodes.Add(new Node { path = PathOf(t), active = t.gameObject.activeInHierarchy,
                    position = t.position, rotation = t.rotation, scale = t.lossyScale,
                    components = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray() });
            foreach (Rigidbody rb in SceneComponents<Rigidbody>()) if (rb.gameObject.activeInHierarchy) Owner(rb.transform);
            var colliders = SceneComponents<Collider>();
            var colliderIds = new Dictionary<Collider, string>();
            foreach (Collider c in colliders)
            {
                Body body = Owner(c.transform);
                var g = new Geometry { id = "c" + snapshot.colliders.Count.ToString("D4"), path = PathOf(c.transform),
                    body = body.id, kind = c.GetType().Name, enabled = c.enabled, active = c.gameObject.activeInHierarchy,
                    trigger = c.isTrigger, layer = c.gameObject.layer, scale = c.transform.lossyScale,
                    includeLayers = c.includeLayers.value, excludeLayers = c.excludeLayers.value, layerOverridePriority = c.layerOverridePriority,
                    rotation = Quaternion.Inverse(body.rotation) * c.transform.rotation, contactOffset = c.contactOffset };
                var mat = c.sharedMaterial;
                g.physicsMaterial = mat != null ? mat.name : "Unity default";
                g.staticFriction = mat != null ? mat.staticFriction : 0.6f;
                g.dynamicFriction = mat != null ? mat.dynamicFriction : 0.6f;
                g.bounciness = mat != null ? mat.bounciness : 0;
                g.frictionCombine = mat != null ? (int)mat.frictionCombine : 0;
                g.bounceCombine = mat != null ? (int)mat.bounceCombine : 0;
                if (c is BoxCollider box) { g.center = LocalPoint(body, c.transform.TransformPoint(box.center)); g.size = Vector3.Scale(box.size, Abs(g.scale)); }
                else if (c is SphereCollider sphere) { g.center = LocalPoint(body, c.transform.TransformPoint(sphere.center)); g.radius = sphere.radius * Max(Abs(g.scale)); }
                else if (c is CapsuleCollider cap)
                {
                    g.direction = cap.direction; g.center = LocalPoint(body, c.transform.TransformPoint(cap.center));
                    var scale = Abs(g.scale); g.radius = cap.radius * Mathf.Max(scale[(cap.direction + 1) % 3], scale[(cap.direction + 2) % 3]);
                    g.height = Mathf.Max(2 * g.radius, cap.height * scale[cap.direction]);
                }
                else if (c is MeshCollider mc) { g.convex = mc.convex; g.mesh = AddMesh(mc.sharedMesh, c.transform, body); }
                else snapshot.unsupported.Add("Collider: " + g.kind + " at " + g.path);
                snapshot.colliders.Add(g); colliderIds.Add(c, g.id);
            }
            for (int i = 0; i < colliders.Length; i++) for (int j = i + 1; j < colliders.Length; j++)
                if (colliders[i].enabled && colliders[j].enabled && colliders[i].gameObject.activeInHierarchy && colliders[j].gameObject.activeInHierarchy && Physics.GetIgnoreCollision(colliders[i], colliders[j]))
                    snapshot.ignoredPairs.Add(new IgnoredPair { first = colliderIds[colliders[i]], second = colliderIds[colliders[j]] });
            foreach (MeshRenderer renderer in SceneComponents<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>(); if (filter == null || filter.sharedMesh == null) continue;
                Body body = Owner(renderer.transform);
                for (int s = 0; s < filter.sharedMesh.subMeshCount; s++)
                {
                    var materials = renderer.sharedMaterials;
                    Material mat = materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
                    snapshot.visuals.Add(new Visual { id = "v" + snapshot.visuals.Count.ToString("D4"), path = PathOf(renderer.transform),
                        body = body.id, mesh = AddMesh(filter.sharedMesh, renderer.transform, body, s), enabled = true, active = true,
                        color = mat != null && mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : mat != null && mat.HasProperty("_Color") ? mat.color : Color.gray,
                        material = mat != null ? mat.name : "", texture = mat != null && mat.mainTexture != null ? mat.mainTexture.name : "" });
                }
            }
            foreach (Camera c in SceneComponents<Camera>()) if (c.gameObject.activeInHierarchy)
            {
                Body body = Owner(c.transform);
                snapshot.cameras.Add(new CameraRecord { id = "camera" + snapshot.cameras.Count, path = PathOf(c.transform), body = body.id,
                    position = LocalPoint(body, c.transform.position), rotation = Quaternion.Inverse(body.rotation) * c.transform.rotation,
                    fov = c.fieldOfView, near = c.nearClipPlane, far = c.farClipPlane, orthographic = c.orthographic, enabled = cameraEnabled.TryGetValue(c, out bool wasEnabled) ? wasEnabled : c.enabled });
            }
            foreach (Joint j in SceneComponents<Joint>()) if (j.gameObject.activeInHierarchy) snapshot.unsupported.Add("Joint: " + j.GetType().Name + " at " + PathOf(j.transform));
            foreach (ArticulationBody a in SceneComponents<ArticulationBody>()) if (a.gameObject.activeInHierarchy) snapshot.unsupported.Add("ArticulationBody at " + PathOf(a.transform));
            foreach (SkinnedMeshRenderer r in SceneComponents<SkinnedMeshRenderer>()) if (r.enabled && r.gameObject.activeInHierarchy) snapshot.unsupported.Add("SkinnedMeshRenderer at " + PathOf(r.transform));
            Directory.CreateDirectory(output);
            string path = Path.Combine(output, "unity_snapshot.json");
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(snapshot, false));
            File.Move(path + ".tmp", path);
            Debug.Log($"[MujocoSnapshot] captured {snapshot.bodies.Count} bodies, {snapshot.colliders.Count} colliders, {snapshot.visuals.Count} visuals -> {path}");
            Application.Quit(0);
        }
        catch (Exception e) { Debug.LogException(e); Application.Quit(2); }
    }
    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    private static float Max(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));
    private void Update() { if (!captured && Time.unscaledTime > 300) { Debug.LogError("[MujocoSnapshot] generation timeout"); Application.Quit(3); } }
    private void OnDestroy() { DebrisSpawner.BeforeFreeze -= Capture; }
}
