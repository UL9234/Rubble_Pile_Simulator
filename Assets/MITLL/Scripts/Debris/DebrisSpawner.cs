// DISTRIBUTION STATEMENT A. Approved for public release. Distribution is unlimited.
//  
// This material is based upon work supported by the Department of the Air Force under Air Force Contract No. FA8702-15-D-0001. Any opinions, findings, conclusions or recommendations expressed in this material are those of the author(s) and do not necessarily reflect the views of the Department of the Air Force.
//  
// © 2024 Massachusetts Institute of Technology.
// Subject to FAR52.227-11 Patent Rights - Ownership by the contractor (May 2014)
//  
// The software/firmware is provided to you on an As-Is basis
//  
// Delivered to the U.S. Government with Unlimited Rights, as defined in DFARS Part 252.227-7013 or 7014 (Feb 2014). Notwithstanding any copyright notice, U.S. Government rights in this work are defined by DFARS 252.227-7013 or DFARS 252.227-7014 as detailed above. Use of this work other than as specifically authorized by the U.S. Government may violate any copyrights that exist in this work.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DebrisSpawner : MonoBehaviour
{
    private RandomManager random;
    public GameManager gameManager;
    public WeightedItemCollectionSO debrisCollection;
    public WeightedItemCollectionSO smallDebrisCollection;
    public GameObject SpawnVolume;
    public int numToSpawn;
    public float spawnDelay;
    public int numPiles;

    [Header("Pancake collapse scenario")]
    [Tooltip("Victim model placed on the boundary of the spawn area (assigned by Scripts/Debris/Editor/VictimSetupEditor).")]
    public GameObject victimPrefab;
    [Tooltip("Material applied to the victim, e.g. a URP/Lit material using the character colormap.")]
    public Material victimMaterial;

    private Bounds spawnBounds;
    private List<GameObject> objList = new List<GameObject>();
    private CustomArgs customArgs;
    private bool exportSTL;

    // --- pancake collapse settings (command line, see README) ---
    private bool pancakeCollapse;
    private bool placeVictim;
    private GameObject victim;
    private GameObject catchFloor;
    private float pancakeTilt = 20f;
    private float pancakeScaleMin = 0.8f;
    private float pancakeScaleMax = 2.2f;
    private float pancakeLayerGap = 4f;
    private float pancakePerPieceDelay = 0.06f;
    private bool pancakeCatchFloor = true;
    private float victimHeight = 1.7f;
    private float victimYawSpread = 30f;
    private float victimOffset;             // + is outwards along the boundary normal
    private int victimEdge;                 // 0 = +x, 1 = -x, 2 = +z, 3 = -z

    // --- fully procedural slab generation (optional) ---
    private bool procDebris;
    private int procCellsMin = 8;
    private int procCellsMax = 11;
    private float procThicknessMin = 0.12f;
    private float procThicknessMax = 0.18f;
    private float procLayerSpacing = 3f;
    private float procInset;
    private float procEdgeStep = 0.12f;
    private float procNoiseScale = 3f;
    private float procNoiseFraction = 0.015f;   // edge relief as a fraction of the cell size
    private bool rebarEnabled = true;          // exposed reinforcement bars between fragments
    private float rebarGrid = 0.30f;           // spacing of the virtual bar grid (m)
    private float rebarThickness = 1f;         // radius multiplier (real bars are barely visible at 720p)
    private Material rebarMaterial;
    private float procCornerChance = 1f;
    private bool procEdgeNoise = true;
    private bool procBreakCorners = true;
    private Color procSlabColor = new Color(0.78f, 0.77f, 0.74f);
    private float procThickness;            // sampled once per scene: same for every slab and layer
    private Material slabMaterial;

    // --- physical mass of the debris (optional) ---
    // Unity's Rigidbody mass is a fixed serialised value and is NOT recomputed when a piece is scaled,
    // so every piece of the stock library weighs 1 kg no matter its size. Setting a density gives each
    // piece the mass of its (scaled) collision volume instead.
    private float debrisDensity;            // kg/m^3; <= 0 keeps the prefab mass
    private float massMin, massMax, massSum;
    private int massCount;

    // Start is called before the first frame update
    void Start()
    {
        customArgs = CustomArgs.Instance;

        numToSpawn = (int)CustomArgs.GetWithDefault("numobjs", 300);
        numPiles = (int)CustomArgs.GetWithDefault("numlayers", 3);

        exportSTL = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("exportstl", 0));

        Vector3 SPAWN_START_POS = new Vector3(
            CustomArgs.GetWithDefault("spawnposx", 0),
            CustomArgs.GetWithDefault("spawnposy", 15),
            CustomArgs.GetWithDefault("spawnposz", 0)
        );
            
            
        Vector3 SPAWN_BOUNDS_SIZE =  new Vector3(
            CustomArgs.GetWithDefault("spawnboundx", 10),
            CustomArgs.GetWithDefault("spawnboundy", 10),
            CustomArgs.GetWithDefault("spawnboundz", 10)
            );

        random = RandomManager.Instance;
        if (SpawnVolume == null)
        {
            SpawnVolume = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SpawnVolume.name = "SpawnVolume";
            SpawnVolume.transform.position = SPAWN_START_POS;

            var meshbounds = SpawnVolume.GetComponent<MeshRenderer>().bounds;
            meshbounds.size = SPAWN_BOUNDS_SIZE;
            spawnBounds = meshbounds;
            Destroy(SpawnVolume.GetComponent<BoxCollider>());
            SpawnVolume.GetComponent<MeshRenderer>().enabled = false;
        }

        ReadPancakeArgs();
        ReadProceduralArgs();
        // Optional: give every piece the mass of its scaled collision volume (kg/m^3).
        debrisDensity = CustomArgs.GetWithDefault("debrisdensity", 0f);
        if (debrisDensity > 0f)
        {
            Debug.Log("[DebrisSpawner] volumetric debris mass enabled, density " + debrisDensity + " kg/m3");
        }
        if ((pancakeCollapse || procDebris) && pancakeCatchFloor) CreateCatchFloor();
    }

    /// <summary>
    /// Fully procedural generation settings. The slab thickness is drawn once here so that every slab
    /// of every layer shares it (a single "pour" of concrete for the whole scene).
    /// </summary>
    private void ReadProceduralArgs()
    {
        procDebris = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("procdebris", 0));
        if (!procDebris) return;

        procCellsMin = Mathf.Max(1, (int)CustomArgs.GetWithDefault("proccellsmin", 8));
        procCellsMax = Mathf.Max(procCellsMin, (int)CustomArgs.GetWithDefault("proccellsmax", 11));
        procThicknessMin = CustomArgs.GetWithDefault("procthicknessmin", 0.12f);
        procThicknessMax = Mathf.Max(procThicknessMin, CustomArgs.GetWithDefault("procthicknessmax", 0.18f));
        procLayerSpacing = CustomArgs.GetWithDefault("proclayerspacing", 3f);
        procInset = CustomArgs.GetWithDefault("procinset", 0f);
        procEdgeStep = CustomArgs.GetWithDefault("procedgestep", 0.35f);   // coarse: angular break lines
        Vector3 slabRgb = DemoArgsLikeColor();
        procSlabColor = new Color(slabRgb.x, slabRgb.y, slabRgb.z, 1f);
        procNoiseScale = CustomArgs.GetWithDefault("procnoisescale", 3f);
        procNoiseFraction = CustomArgs.GetWithDefault("procnoisefraction", 0.015f);
        rebarEnabled = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("rebar", 1f));
        rebarGrid = Mathf.Max(0.05f, CustomArgs.GetWithDefault("rebargrid", 0.30f));
        rebarThickness = Mathf.Max(0.05f, CustomArgs.GetWithDefault("rebarthickness", 1f));
        procCornerChance = CustomArgs.GetWithDefault("proccornerchance", 1f);
        procEdgeNoise = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("procnoise", 1));
        procBreakCorners = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("proccorners", 1));

        procThickness = random != null
            ? random.GetStaticFloat(procThicknessMin, procThicknessMax)
            : Random.Range(procThicknessMin, procThicknessMax);
    }

    /// <summary>
    /// Optional "pancake collapse" preset: slabs fall near horizontally (bounded tilt instead of free
    /// tumbling), the footprint is shrunk to keep the reduced piece count dense, and a victim model is
    /// laid on the boundary of the spawn area.
    /// </summary>
    private void ReadPancakeArgs()
    {
        pancakeCollapse = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("pancake", 0));
        pancakeTilt = CustomArgs.GetWithDefault("pancaketilt", 20f);
        pancakeScaleMin = CustomArgs.GetWithDefault("pancakescalemin", 0.8f);
        pancakeScaleMax = CustomArgs.GetWithDefault("pancakescalemax", 2.2f);
        pancakeLayerGap = CustomArgs.GetWithDefault("pancakelayergap", 4f);
        pancakePerPieceDelay = CustomArgs.GetWithDefault("pancakespawndelay", 0.06f);
        pancakeCatchFloor = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("pancakecatchfloor", 1));

        placeVictim = CustomArgs.FloatToBool(CustomArgs.GetWithDefault("pancakevictim", 1));
        victimEdge = Mathf.Clamp((int)CustomArgs.GetWithDefault("pancakevictimedge", 0), 0, 3);
        victimHeight = CustomArgs.GetWithDefault("pancakevictimheight", 1.7f);
        victimYawSpread = CustomArgs.GetWithDefault("pancakevictimyawspread", 30f);
        victimOffset = CustomArgs.GetWithDefault("pancakevictimoffset", 0f);
    }

    /// <summary>
    /// The ground plane's collider is essentially zero thick, so fast slabs can tunnel through it and
    /// are then culled by FreezeDebris as out of bounds. This adds a physics-only slab whose top face
    /// sits exactly at y = 0.
    /// </summary>
    private void CreateCatchFloor()
    {
        catchFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        catchFloor.name = "CatchFloor";
        catchFloor.transform.position = new Vector3(spawnBounds.center.x, -1f, spawnBounds.center.z);
        catchFloor.transform.localScale = new Vector3(60f, 2f, 60f);
        Destroy(catchFloor.GetComponent<MeshRenderer>());
    }

    private void OnEnable()
    {
        GameManager.doReset += Reset;
    }

    private void OnDisable()
    {
        GameManager.doReset -= Reset;
    }

    
    public void Reset()
    {
        StopAllCoroutines();
        if (objList.Count > 0)
        {
            foreach (GameObject go in objList)
            {
                GameObject.Destroy(go.gameObject);
            }
            objList.Clear();
        }

        if (victim != null)
        {
            Destroy(victim);
            victim = null;
        }

        massMin = massMax = massSum = 0f;
        massCount = 0;

        StartCoroutine(SetUpScene());


    }
    IEnumerator SetUpScene()
    {
        if (procDebris)
        {
            // Fully procedural slabs: the mesh already carries its own size and orientation.
            Time.timeScale = 1f;
            if (placeVictim) PlaceVictim();
            yield return StartCoroutine(GenerateProceduralLayers());
        }
        else if (pancakeCollapse)
        {
            // Natural fall speed so the collapse reads on camera.
            Time.timeScale = 1f;
            if (placeVictim) PlaceVictim();

            for (int i = 0; i < numPiles; i++)
            {
                yield return StartCoroutine(GeneratePileOverTime(debrisCollection, pancakePerPieceDelay));
                yield return new WaitForSeconds(pancakeLayerGap);
            }
        }
        else
        {
            Time.timeScale = 10f;
            GeneratePile(smallDebrisCollection);
            yield return new WaitForSeconds(spawnDelay);
            for (int i = 0; i < numPiles; i++)
            {
                GeneratePile(debrisCollection);
                yield return new WaitForSeconds(spawnDelay);
            }
        }

        FreezeDebris();
        Time.timeScale = 1f;
        gameManager.Initialize();
    }

    /// <summary>
    /// Procedural generation: one Voronoi layer of floor slabs per requested layer, spaced
    /// procLayerSpacing metres apart, each layer tiling the spawn area.
    /// </summary>
    private struct PendingSlab
    {
        public float radius;        // distance from the spawn centre: the release order is centre first
        public float layerY;
        public ProceduralSlabFactory.Slab slab;
    }

    private IEnumerator GenerateProceduralLayers()
    {
        Material mat = ResolveSlabMaterial();
        float centreX = spawnBounds.center.x;
        float centreZ = spawnBounds.center.z;
        float baseY = CustomArgs.GetWithDefault("spawnposy", 3f);

        var settings = new ProceduralSlabFactory.Settings
        {
            width = spawnBounds.size.x,
            depth = spawnBounds.size.z,
            cellsMin = procCellsMin,
            cellsMax = procCellsMax,
            inset = procInset,
            noiseAmplitude = 0f,
            noiseFraction = procNoiseFraction,
            noiseSeed = random != null ? random.seed : 0,
            edgeStep = procEdgeStep,
            noiseScale = procNoiseScale,
            edgeNoise = procEdgeNoise,
            breakCorners = procBreakCorners,
            cornerChance = procCornerChance,
            cornerDepth = -1f,
        };

        Debug.Log(string.Format(
            "[DebrisSpawner] procedural slabs: {0} layers of {1}x{2} m, {3}-{4} cells each, thickness {5:F0} mm, layer spacing {6:F1} m, edge noise {7:F0} mm",
            numPiles, settings.width, settings.depth, settings.cellsMin, settings.cellsMax,
            procThickness * 1000f, procLayerSpacing,
            (settings.noiseAmplitude > 0f
                ? settings.noiseAmplitude
                : Mathf.Clamp(settings.noiseFraction * Mathf.Sqrt(settings.width * settings.depth / 10f), 0.005f, 0.15f)) * 1000f));

        // 1. build every layer's fragments first (layer k bottom plane sits at baseY + k * spacing)
        var pending = new List<PendingSlab>();
        int totalSlabs = 0;

        // Layer by layer: a layer is built, released, and left to land before the next one is built,
        // so two layers never share the air space.
        for (int layer = 0; layer < numPiles; layer++)
        {
            float layerY = baseY + layer * procLayerSpacing;
            int seed = (random != null ? random.seed : 0) + 1013 * (layer + 1);
            var rng = new System.Random(seed);

            int cornerCuts;
            List<ProceduralSlabFactory.Slab> slabs =
                ProceduralSlabFactory.BuildLayer(settings, procThickness, rng, out cornerCuts);
            if (slabs.Count == 0)
            {
                Debug.LogWarning("[DebrisSpawner] procedural layer " + layer + " produced no slabs");
                continue;
            }

            pending.Clear();
            float covered = 0f, areaMin = float.MaxValue, areaMax = 0f;
            foreach (ProceduralSlabFactory.Slab slab in slabs)
            {
                covered += slab.footprint;
                areaMin = Mathf.Min(areaMin, slab.footprint);
                areaMax = Mathf.Max(areaMax, slab.footprint);
                pending.Add(new PendingSlab
                {
                    radius = Mathf.Sqrt(slab.center.x * slab.center.x + slab.center.z * slab.center.z),
                    layerY = layerY,
                    slab = slab,
                });
            }
            totalSlabs += slabs.Count;

            // Release order: nearest the spawn centre first, rim last.
            pending.Sort((a, b) => a.radius.CompareTo(b.radius));

            // Fixed release policy (A/B tested, see Docs/procedural_debris_generation.md 5.1):
            // one fragment every sqrt(2 t / g), the time a plate needs to fall clear of its own
            // thickness (200 mm -> 0.202 s, this scene's thickness is printed below). Releasing the
            // fragments together instead throws the whole layer 8-10 m wide.
            float interval = Mathf.Sqrt(2f * procThickness / 9.81f);

            // Rebar: a single virtual grid through the slab at mid thickness, strictly parallel and
            // perpendicular to the generation boundary. Where it crosses the crack between two
            // neighbours it marks a pair (bar out of one fragment, into the next). Planned before the
            // first fragment of this layer is released.
            ProceduralRebar.Plan rebarPlan = PlanRebar(pending, layer, centreX, centreZ, settings, rng);

            var spawned = new Transform[pending.Count];
            for (int i = 0; i < pending.Count; i++)
            {
                spawned[i] = SpawnSlab(pending[i], mat).transform;
                if (i + 1 < pending.Count)
                {
                    yield return new WaitForSeconds(interval);
                }
            }

            if (rebarPlan != null)
            {
                ProceduralRebar.BuildStats stats =
                    ProceduralRebar.Build(rebarPlan, spawned, ResolveRebarMaterial());
                Debug.Log(string.Format(
                    "[DebrisSpawner] rebar layer {0}: {1} bars, length {2:F2}-{3:F2} m (random growth, cap 0.20 m); fragments with bars {4}/{5}, bars per fragment {6}-{7}",
                    layer + 1, stats.bars, stats.minLength, stats.maxLength,
                    stats.fragmentsWithBars, stats.fragments, stats.minBars, stats.maxBars));
            }

            Debug.Log(string.Format(
                "[DebrisSpawner] procedural layer {0}: {1} slabs, bottom plane y={2:F2}, cover {3:F1}/{4:F1} m2, piece area {5:F2}-{6:F2} m2, {7} corner cuts; release interval {8:F3} s over {9:F2} s, radius {10:F2}-{11:F2} m",
                layer + 1, slabs.Count, layerY, covered, settings.width * settings.depth,
                areaMin, areaMax, cornerCuts, interval, interval * Mathf.Max(0, slabs.Count - 1),
                pending[0].radius, pending[pending.Count - 1].radius));

            // Let this layer land before the next one is generated.
            yield return new WaitForSeconds(pancakeLayerGap);
        }

        Debug.Log(string.Format(
            "[DebrisSpawner] {0} slabs of {1} layers released layer by layer, centre first; waiting {2:F1} s to settle",
            totalSlabs, numPiles, pancakeLayerGap));

        // Let the collapse finish before freezing (Rigidbodies are removed and the pile is batched).
        yield return new WaitForSeconds(pancakeLayerGap);
    }

    /// <summary>
    /// Plans the exposed reinforcement for one layer: builds the virtual grid crossings, keeps the
    /// ones that straddle a crack as pairs, and creates one visual bar per pair.
    /// </summary>
    private ProceduralRebar.Plan PlanRebar(List<PendingSlab> pending, int layer,
                                           float centreX, float centreZ,
                                           ProceduralSlabFactory.Settings settings, System.Random rng)
    {
        if (!rebarEnabled || pending.Count < 2) return null;

        var fragments = new ProceduralRebar.Fragment[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            fragments[i].outline = pending[i].slab.midOutline;
            fragments[i].plannedPosition = new Vector3(centreX + pending[i].slab.center.x,
                                                       pending[i].layerY + pending[i].slab.center.y,
                                                       centreZ + pending[i].slab.center.z);
        }

        // The virtual bar grid runs through the slab at MID thickness: layerY is the bottom face of
        // the layer, so the plane has to be raised by half a slab. Anchoring it at layerY put every
        // bar on the bottom (or, once a slab landed upside down, the top) face instead.
        float midPlaneY = pending[0].layerY + procThickness * 0.5f;

        ProceduralRebar.Plan plan = ProceduralRebar.Create(fragments,
                               new Vector3(centreX, midPlaneY, centreZ),
                               settings.width, settings.depth,
                               rebarGrid,
                               0.02f, 0.05f,                                    // embedded depth into the concrete
                               0.005f * rebarThickness, 0.009f * rebarThickness, // bar radius
                               0.30f, 0.70f,                                    // sweep fraction per end
                               rng);
        return plan.Count > 0 ? plan : null;
    }

    /// <summary>Rusty steel: dark enough to read against the white slabs.</summary>
    private Material ResolveRebarMaterial()
    {
        if (rebarMaterial != null) return rebarMaterial;

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) return null;

        Material m = new Material(sh);
        m.name = "ProceduralRebarSteel";
        Color rust = new Color(0.34f, 0.15f, 0.09f, 1f);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", rust);
        if (m.HasProperty("_Color")) m.SetColor("_Color", rust);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.32f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.55f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // thin tubes: never cull a back face
        rebarMaterial = m;
        return m;
    }

    private GameObject SpawnSlab(PendingSlab item, Material mat)
    {
        GameObject go = new GameObject("slab");
        go.transform.position = new Vector3(spawnBounds.center.x + item.slab.center.x,
                                            item.layerY + item.slab.center.y,
                                            spawnBounds.center.z + item.slab.center.z);
        go.transform.rotation = Quaternion.identity;          // no random orientation, ever

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = item.slab.mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        if (mat != null) mr.sharedMaterial = mat;

        MeshCollider mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = item.slab.mesh;
        mc.convex = true;

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.useGravity = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        float m = debrisDensity > 0f ? debrisDensity * item.slab.volume : 1f;
        rb.mass = m;
        RegisterMass(m);

        objList.Add(go);
        return go;
    }

    private void RegisterMass(float m)
    {
        if (massCount == 0)
        {
            massMin = massMax = m;
        }
        else
        {
            massMin = Mathf.Min(massMin, m);
            massMax = Mathf.Max(massMax, m);
        }
        massSum += m;
        massCount++;
    }

    /// <summary>
    /// Plain untextured slab material (a "white model"): the shipped palette atlas left some faces
    /// sampling its black area, and a flat material also makes the generated geometry easy to read.
    /// Double sided so a stray back face can never make a slab look see-through.
    /// </summary>
    private Material ResolveSlabMaterial()
    {
        if (slabMaterial != null) return slabMaterial;

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) return null;

        Material m = new Material(sh);
        m.name = "ProceduralSlabPlain";
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", procSlabColor);
        if (m.HasProperty("_Color")) m.SetColor("_Color", procSlabColor);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.12f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        slabMaterial = m;
        return m;
    }

    private Vector3 DemoArgsLikeColor()
    {
        float r = CustomArgs.GetWithDefault("procslabcolorr", procSlabColor.r);
        float g = CustomArgs.GetWithDefault("procslabcolorg", procSlabColor.g);
        float b = CustomArgs.GetWithDefault("procslabcolorb", procSlabColor.b);
        return new Vector3(r, g, b);
    }

    /// <summary>
    /// Lays the victim on the ground at the midpoint of one boundary of the spawn area: the body's
    /// long axis points along the outward normal (head outside, feet inside) with a random deviation
    /// of up to +/- victimYawSpread degrees from that centre line.
    /// </summary>
    private void PlaceVictim()
    {
        if (victimPrefab == null)
        {
            Debug.LogWarning("[DebrisSpawner] pancake victim requested but no victimPrefab is assigned " +
                             "(run Scripts/Debris/Editor/VictimSetupEditor).");
            return;
        }

        victim = Instantiate(victimPrefab);
        victim.name = "victim";

        // Normalise the asset: realistic body height, then lay it flat on its back.
        Bounds upright = CombinedBounds(victim);
        float s = upright.size.y > 0.0001f ? victimHeight / upright.size.y : 1f;
        victim.transform.localScale = Vector3.one * s;

        Quaternion importRot = victim.transform.rotation;
        victim.transform.rotation = Quaternion.Euler(-90f, VictimYaw(), 0f) * importRot;

        // Centre the body on the boundary midpoint (a negative offset insets it towards the spawn
        // centre so the debris covers more of it), then rest it on the ground.
        Vector3 centre = new Vector3(spawnBounds.center.x, 0f, spawnBounds.center.z);
        Vector3 target = centre + EdgeOutward(victimEdge) * (EdgeHalfExtent(victimEdge) + victimOffset);
        victim.transform.position = target;
        Bounds laid = CombinedBounds(victim);
        victim.transform.position += new Vector3(target.x - laid.center.x, -laid.min.y, target.z - laid.center.z);

        if (victimMaterial != null)
        {
            foreach (Renderer r in victim.GetComponentsInChildren<Renderer>()) r.sharedMaterial = victimMaterial;
        }

        // Collide against the body itself, not against its bounding box. A box around a lying human
        // is mostly air: the slabs would rest on that invisible box and leave a large void between
        // themselves and the body. The victim has no Rigidbody, so a non convex MeshCollider is
        // allowed and follows the real surface (limbs, waist, head).
        if (!AddBodyCollider(victim))
        {
            Bounds local = LocalBounds(victim);
            BoxCollider bc = victim.AddComponent<BoxCollider>();
            bc.center = local.center;
            bc.size = local.size;
            Debug.LogWarning("[DebrisSpawner] victim body mesh is not readable; fell back to a box collider");
        }

        Debug.Log(string.Format(
            "[DebrisSpawner] victim placed on edge {0} at {1} (yaw {2:F1} deg, height {3:F2} m, boundary offset {4:F2} m)",
            victimEdge, victim.transform.position.ToString("F2"), victim.transform.eulerAngles.y,
            victimHeight, victimOffset));
    }

    /// <summary>
    /// Gives the victim one non convex MeshCollider per body mesh part (the model is a single body
    /// mesh, but this stays correct if the asset ever ships several). Returns false when no readable
    /// mesh is available, in which case the caller falls back to a box.
    /// </summary>
    private bool AddBodyCollider(GameObject root)
    {
        bool any = false;
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null || !mesh.isReadable) continue;

            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc == null) mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;                 // static victim: an exact surface, no convex hull webbing
            any = true;
        }
        return any;
    }

    /// <summary>Yaw that puts the head along the outward normal, plus the random spread.</summary>
    private float VictimYaw()
    {
        float baseYaw;
        switch (victimEdge)
        {
            case 1: baseYaw = 90f; break;    // -x
            case 2: baseYaw = 180f; break;   // +z
            case 3: baseYaw = 0f; break;     // -z
            default: baseYaw = -90f; break;  // +x
        }
        return baseYaw + random.GetStaticFloat(-victimYawSpread, victimYawSpread);
    }

    private static Vector3 EdgeOutward(int edge)
    {
        switch (edge)
        {
            case 1: return Vector3.left;
            case 2: return Vector3.forward;
            case 3: return Vector3.back;
            default: return Vector3.right;
        }
    }

    private float EdgeHalfExtent(int edge)
    {
        return (edge == 2 || edge == 3) ? spawnBounds.extents.z : spawnBounds.extents.x;
    }

    private static Bounds CombinedBounds(GameObject go)
    {
        Renderer[] rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    /// <summary>Combined renderer bounds expressed in the object's own unscaled local space.</summary>
    private static Bounds LocalBounds(GameObject go)
    {
        Renderer[] rs = go.GetComponentsInChildren<Renderer>();
        Bounds outB = new Bounds();
        bool any = false;
        foreach (Renderer r in rs)
        {
            Bounds lb = r.localBounds;
            Vector3 c = lb.center, e = lb.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                                 (i & 2) == 0 ? -e.y : e.y,
                                                 (i & 4) == 0 ? -e.z : e.z);
                Vector3 local = go.transform.InverseTransformPoint(r.transform.TransformPoint(corner));
                if (!any) { outB = new Bounds(local, Vector3.zero); any = true; }
                else outB.Encapsulate(local);
            }
        }
        if (!any) outB = new Bounds(Vector3.zero, Vector3.one * 0.1f);
        return outB;
    }

    public void GenerateAdditional()
    {
        GeneratePile(debrisCollection);
    }

    private void GeneratePile(WeightedItemCollectionSO itemCollection)
    {
        for (int i = 0; i < numToSpawn; i++)
        {
            SpawnOneDebris(itemCollection, i);
        }
    }

    /// <summary>Same as GeneratePile but spread over time, so pieces do not spawn interpenetrated.</summary>
    private IEnumerator GeneratePileOverTime(WeightedItemCollectionSO itemCollection, float perPieceDelay)
    {
        for (int i = 0; i < numToSpawn; i++)
        {
            SpawnOneDebris(itemCollection, i);
            if (perPieceDelay > 0f) yield return new WaitForSeconds(perPieceDelay);
        }
    }

    private GameObject SpawnOneDebris(WeightedItemCollectionSO itemCollection, int index)
    {
        // -----------------------------------------------------------
        // Adding Debris Piece
        // -----------------------------------------------------------
        // Position
        float widthMax = spawnBounds.max.x;  // x is left-right
        float widthMin = spawnBounds.min.x;  // x is left-right
        float lengthMax = spawnBounds.max.z; 
        float lengthMin = spawnBounds.min.z; 
        float heightMin = spawnBounds.min.y; // y is up
        float heightMax = spawnBounds.max.y; // y is up

        float x = random.GetStaticFloat(widthMin, widthMax);
        float y = random.GetStaticFloat(heightMin, heightMax);
        float z = random.GetStaticFloat(lengthMin, lengthMax);
            
        // Uniform Scale
        float scale = pancakeCollapse
            ? random.GetStaticFloat(pancakeScaleMin, pancakeScaleMax)
            : random.GetStaticFloat(1, 5);
            
        // Rotation
        Quaternion randQuat = pancakeCollapse ? SlabRotation() : TumbleRotation();
            
        // Creating the gameObject, positioning, and scaling it in scene
        GameObject debris = Spawn(itemCollection, new Vector3(x, y, z), randQuat);
            
        debris.transform.localScale = new Vector3(scale, scale, scale);
        debris.name = (pancakeCollapse ? "slab" : "debris") + index;

        ValidateDebrisColliders(debris);

        if (debrisDensity > 0f) ApplyVolumetricMass(debris, scale);

        if (pancakeCollapse)
        {
            // Without continuous collision the fast slabs pass straight through the thin ground.
            Rigidbody rb = debris.GetComponent<Rigidbody>();
            if (rb != null) rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        objList.Add(debris);
        return debris;
    }

    /// <summary>
    /// Give a piece the mass of its collision volume: mass = density * localVolume * scale^3.
    /// Unity keeps the serialised Rigidbody mass when a piece is scaled, so without this every piece of
    /// the stock library weighs 1 kg whether it is a pebble or a 3 m floor slab.
    /// </summary>
    private void ApplyVolumetricMass(GameObject debris, float scale)
    {
        Rigidbody rb = debris.GetComponent<Rigidbody>();
        if (rb == null) return;

        float vol = LocalCollisionVolume(debris) * scale * scale * scale;
        float m = debrisDensity * vol;
        if (m <= 0f) return;

        rb.mass = m;
        RegisterMass(m);
    }

    /// <summary>Volume of the piece in its own local (unscaled) space, from its collider or mesh.</summary>
    private static float LocalCollisionVolume(GameObject go)
    {
        BoxCollider box = go.GetComponent<BoxCollider>();
        if (box != null) return box.size.x * box.size.y * box.size.z;

        MeshCollider mesh = go.GetComponent<MeshCollider>();
        if (mesh != null && mesh.sharedMesh != null)
        {
            Vector3 s = mesh.sharedMesh.bounds.size;
            return s.x * s.y * s.z;
        }

        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            Vector3 s = r.localBounds.size;
            return s.x * s.y * s.z;
        }
        return 0f;
    }

    /// <summary>Near-horizontal slab: identity base orientation with a bounded tilt and a free yaw.</summary>
    private Quaternion SlabRotation()
    {
        Vector3 tilt = Vector3.zero;
        tilt.x = random.GetStaticFloat(-pancakeTilt, pancakeTilt);
        tilt.y = random.GetStaticFloat(0, 360);
        tilt.z = random.GetStaticFloat(-pancakeTilt, pancakeTilt);
        return Quaternion.Euler(tilt);
    }

    /// <summary>The simulator's original fully random tumbling orientation.</summary>
    private Quaternion TumbleRotation()
    {
        Vector3 randRot = Vector3.zero;

        randRot.x = random.GetStaticFloat(-180, 180);
        randRot.y = random.GetStaticFloat(-180, 180);
        randRot.z = random.GetStaticFloat(-180, 180);

        return Quaternion.Euler(randRot);
    }

    private void ValidateDebrisColliders(GameObject debris)
    {
        // -----------------------------------------------------------
        // Catch missing colliders and rigidbodies 
        // ----------------------------------------------------------
        if (!debris.GetComponent<Rigidbody>())
        {
            Rigidbody rb;

            if (!debris.GetComponent<Collider>())
            {
                //Add mesh collider
                MeshCollider mc;
                mc = debris.AddComponent<MeshCollider>();
                mc.convex = true;
            }

            //Configure rigidbody
            rb = debris.AddComponent<Rigidbody>();
            rb.useGravity = true;
            rb.mass = 20;

        }
    }
    public GameObject Spawn(WeightedItemCollectionSO weightedList, Vector3 position, Quaternion rotation)
       {
           float weightMax = 0f;
           int curSpawned = 0;
           int breakout = 99;
           foreach (var item in weightedList.weightedItems)
           {
               weightMax += item.weight;
           }
           
           // Instantiate weighted objects
           while (curSpawned < 1)
           {
               if (breakout == 0)
               {
                   //Catch endless loops
                   break;
               }
               foreach (var item in weightedList.weightedItems)
               {
                   if (item.weight > random.GetStaticFloat(0,weightMax))
                   {
                       GameObject go = Instantiate(item.gameObject, position, rotation);
                       return go;
                   }
               }

               breakout--;
           }

           return weightedList.weightedItems[0].gameObject;
       }

    public void FreezeDebris()
    {
        List<GameObject> outOfBounds = new List<GameObject>();
        foreach (GameObject go in objList)
        {
            if (go.transform.position.y < -0.5f)
            {
                outOfBounds.Add(go);
            }
            
            Rigidbody rb = go.GetComponent<Rigidbody>();
            Destroy(rb);
            go.isStatic = true;
        }

        GameObject root = new GameObject();
        root.name = "root";
        root.gameObject.AddComponent<MeshFilter>();

        objList.RemoveAll(go => outOfBounds.Contains(go));
        for (int i = 0; i < outOfBounds.Count; i++)
        {
            Destroy(outOfBounds[i]);
        }

        if (exportSTL)
        {
            SceneToSTLExporter.ExportSceneToSTL();
        }

        foreach (var go in objList)
        {
            go.transform.parent = root.transform;
        }
        
        StaticBatchingUtility.Combine(objList.ToArray(), root);

        Debug.Log(string.Format("[DebrisSpawner] frozen {0} pieces ({1} discarded below the ground)",
            objList.Count, outOfBounds.Count));

        if (procDebris && objList.Count > 0)
        {
            Bounds b = new Bounds(objList[0].transform.position, Vector3.zero);
            foreach (GameObject go in objList)
            {
                if (go == null) continue;
                Renderer r = go.GetComponent<Renderer>();
                b.Encapsulate(r != null ? r.bounds : new Bounds(go.transform.position, Vector3.zero));
            }
            Debug.Log(string.Format("[DebrisSpawner] debris world bounds after freeze: center={0} size={1}",
                b.center.ToString("F2"), b.size.ToString("F2")));
        }

        if (debrisDensity > 0f && massCount > 0)
        {
            Debug.Log(string.Format(
                "[DebrisSpawner] volumetric mass: {0} pieces, {1:F1}-{2:F1} kg each, {3:F0} kg total (density {4} kg/m3)",
                massCount, massMin, massMax, massSum, debrisDensity));
        }

    }

    public List<GameObject> GetDebrisObj()
    {
        return objList;
    }
}
