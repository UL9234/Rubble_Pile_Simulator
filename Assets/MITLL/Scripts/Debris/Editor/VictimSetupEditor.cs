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

using UnityEditor;
using UnityEngine;

/// <summary>
/// One-time project wiring for the pancake-collapse victim model.
///
/// The victim is a plain model asset under Assets/MITLL/Models/Human/, so it has to be referenced by
/// the DebrisSpawner (which lives inside Assets/MITLL/Prefabs/Managers.prefab). Doing that from an
/// editor script keeps the reference correct without hand-editing prefab YAML: Unity resolves the
/// model's internal fileIDs and writes the serialized reference itself.
///
/// Run once (and again after swapping the model):
///   unity2022 -batchmode -quit -projectPath . -executeMethod VictimSetupEditor.Wire
/// or use the menu: RubbleSim ▸ Wire Victim Into Debris Spawner
/// </summary>
public static class VictimSetupEditor
{
    private const string ModelPath = "Assets/MITLL/Models/Human/human-neutral.obj";
    private const string TexturePath = "";   // MakeHuman base mesh has UVs but no albedo texture
    private const string MaterialPath = "Assets/MITLL/Models/Human/VictimMaterial.mat";
    private const string ManagersPrefabPath = "Assets/MITLL/Prefabs/Managers.prefab";
    private static readonly Color VictimColor = new Color(0.62f, 0.29f, 0.21f, 1f);   // muted clothing red, reads against grey slabs

    [MenuItem("RubbleSim/Wire Victim Into Debris Spawner")]
    public static void Wire()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
        {
            Debug.LogError("[VictimSetup] model not found: " + ModelPath);
            return;
        }

        // Let the project own the material. The importer would otherwise create built-in-pipeline
        // materials, which render magenta under URP.
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer != null && importer.materialImportMode != ModelImporterMaterialImportMode.None)
        {
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
        }

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[VictimSetup] URP/Lit shader not found; is the URP package present?");
                return;
            }
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
            Debug.Log("[VictimSetup] created " + MaterialPath);
        }

        // Applied every run so the script is idempotent when colours or the model change.
        if (!string.IsNullOrEmpty(TexturePath))
        {
            Texture2D albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (albedo != null) mat.SetTexture("_BaseMap", albedo);
        }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", VictimColor);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", VictimColor);
        mat.SetFloat("_Smoothness", 0.15f);
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);

        GameObject contents = PrefabUtility.LoadPrefabContents(ManagersPrefabPath);
        DebrisSpawner spawner = contents.GetComponentInChildren<DebrisSpawner>(true);
        if (spawner == null)
        {
            Debug.LogError("[VictimSetup] no DebrisSpawner inside " + ManagersPrefabPath);
            PrefabUtility.UnloadPrefabContents(contents);
            return;
        }

        var so = new SerializedObject(spawner);
        so.FindProperty("victimPrefab").objectReferenceValue = model;
        so.FindProperty("victimMaterial").objectReferenceValue = mat;
        so.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(contents, ManagersPrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();

        Debug.Log("[VictimSetup] wired " + ModelPath + " + " + MaterialPath + " into " + ManagersPrefabPath);
    }
}
