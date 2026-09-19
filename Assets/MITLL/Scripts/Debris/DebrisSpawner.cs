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
    private float victimOffset;
    private int victimEdge;                 // 0 = +x, 1 = -x, 2 = +z, 3 = -z

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
        if (pancakeCollapse && pancakeCatchFloor) CreateCatchFloor();
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

        StartCoroutine(SetUpScene());


    }
    IEnumerator SetUpScene()
    {
        if (pancakeCollapse)
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

        // Centre the body on the boundary midpoint, then rest it on the ground.
        Vector3 centre = new Vector3(spawnBounds.center.x, 0f, spawnBounds.center.z);
        Vector3 target = centre + EdgeOutward(victimEdge) * (EdgeHalfExtent(victimEdge) + victimOffset);
        victim.transform.position = target;
        Bounds laid = CombinedBounds(victim);
        victim.transform.position += new Vector3(target.x - laid.center.x, -laid.min.y, target.z - laid.center.z);

        if (victimMaterial != null)
        {
            foreach (Renderer r in victim.GetComponentsInChildren<Renderer>()) r.sharedMaterial = victimMaterial;
        }

        // A box around the body gives the slabs something to pile onto.
        Bounds local = LocalBounds(victim);
        BoxCollider bc = victim.AddComponent<BoxCollider>();
        bc.center = local.center;
        bc.size = local.size;

        Debug.Log(string.Format("[DebrisSpawner] victim placed on edge {0} at {1} (yaw {2:F1} deg, height {3:F2} m)",
            victimEdge, victim.transform.position.ToString("F2"), victim.transform.eulerAngles.y, victimHeight));
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

        if (pancakeCollapse)
        {
            // Without continuous collision the fast slabs pass straight through the thin ground.
            Rigidbody rb = debris.GetComponent<Rigidbody>();
            if (rb != null) rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        objList.Add(debris);
        return debris;
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

    }

    public List<GameObject> GetDebrisObj()
    {
        return objList;
    }
}
