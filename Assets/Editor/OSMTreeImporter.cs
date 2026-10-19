using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;
using UnityEditor;
using CesiumForUnity;
using Unity.Mathematics;

/// <summary>
/// Reads an .osm file, finds every node tagged natural=tree,
/// and instantiates vegetation prefabs at those positions.
///
/// Overpass query (paste at overpass-turbo.eu, export as OSM):
///   [out:xml][bbox:51.99,11.50,52.22,11.75];
///   node["natural"="tree"];
///   out body;
///
/// Note: tree nodes already contain lat/lon directly —
///   no (._;>;); clause is needed.
///
/// For better tree models: in Unity open
///   Assets/Forst/Conifers [BOTD]/Conifers_URP.unitypackage
/// then assign the resulting prefabs in the Prefabs list below.
/// </summary>
public class OSMTreeImporter : EditorWindow
{
    private string osmFilePath = "Assets/GTFS/map_trees.osm";
    private CesiumGeoreference georeference;

    private float heightOffset   = 0.1f;
    private float scaleMin       = 0.8f;
    private float scaleMax       = 2.0f;
    private float minSpacing     = 3f;    // metres — skip trees closer than this
    private int   maxTrees       = 2000;

    // Terrain snapping. Doubles as the "inside the tiles" test: Cesium only
    // creates a MeshCollider where a tile is actually streamed in, so a ray
    // that hits nothing means we are outside the photogrammetry.
    private bool  snapToTerrain      = true;
    private float raycastStartHeight = 400f;
    private float raycastDistance    = 1500f;

    // Performance
    private bool  addLODGroup         = true;
    private float lodCullRatio        = 0.02f;  // cull below 2% screen height
    private bool  enableGPUInstancing = true;

    private Vector2 scroll;

    private const double LAT_MIN = 51.99;
    private const double LAT_MAX = 52.22;
    private const double LON_MIN = 11.50;
    private const double LON_MAX = 11.75;

    // Default prefab paths from TerrainSampleAssets (fallback if no conifer prefabs installed)
    // A real tree with its own LOD chain + billboard — strongly preferred over
    // the TerrainSampleAssets plants, which are knee-high bushes.
    private static readonly string[] DefaultPrefabPaths = new string[]
    {
        "Assets/Prefabs/PF Conifer Bare BOTD URP.prefab",
    };

    // Used only if no real tree prefab is found.
    private static readonly string[] FallbackPrefabPaths = new string[]
    {
        "Assets/TerrainSampleAssets/Prefabs/Plant_A.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Plant_B.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Plant_C.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Plant_D.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Bush_A.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Bush_B.prefab",
    };

    // User-assignable prefab slots — drag conifer prefabs here once installed
    private List<GameObject> treePrefabs = new List<GameObject>();
    private bool showPrefabList = false;

    [MenuItem("Tools/OSM Tree Importer")]
    public static void ShowWindow()
    {
        OSMTreeImporter w = GetWindow<OSMTreeImporter>("OSM Trees");
        w.minSize = new Vector2(370f, 540f);
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("OSM Tree Importer", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);

        // OSM file
        EditorGUILayout.LabelField("OSM File", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        osmFilePath = EditorGUILayout.TextField(osmFilePath);
        if (GUILayout.Button("Browse", GUILayout.Width(60f)))
        {
            string p = EditorUtility.OpenFilePanel("Select .osm file", Application.dataPath, "osm");
            if (!string.IsNullOrEmpty(p)) osmFilePath = p;
        }
        EditorGUILayout.EndHorizontal();

        bool fileExists = File.Exists(osmFilePath);
        if (!fileExists)
            EditorGUILayout.HelpBox(
                "File not found. Download from overpass-turbo.eu:\n\n" +
                "[out:xml][bbox:51.99,11.50,52.22,11.75];\n" +
                "node[\"natural\"=\"tree\"];\n" +
                "out body;\n\n" +
                "Save as Assets/GTFS/map_trees.osm\n\n" +
                "Tip: you can also reuse map.osm if it already\n" +
                "contains natural=tree nodes.",
                MessageType.Warning);

        // Scene reference
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Scene Reference", EditorStyles.boldLabel);
        georeference = (CesiumGeoreference)EditorGUILayout.ObjectField(
            "Cesium GeoReference", georeference, typeof(CesiumGeoreference), true);
        if (georeference == null)
            EditorGUILayout.HelpBox("Drag CesiumGeoReference from the Hierarchy.", MessageType.Warning);

        // Prefabs
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Tree Prefabs", EditorStyles.boldLabel);
        showPrefabList = EditorGUILayout.Foldout(showPrefabList, "Custom Prefabs (optional)");
        if (showPrefabList)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                "Leave empty to use TerrainSampleAssets plants as placeholders.\n" +
                "For real trees: install Assets/Forst/Conifers [BOTD]/Conifers_URP.unitypackage " +
                "then drag the conifer prefabs here.",
                MessageType.Info);

            // Resize list
            int newCount = EditorGUILayout.IntField("Count", treePrefabs.Count);
            while (treePrefabs.Count < newCount) treePrefabs.Add(null);
            while (treePrefabs.Count > newCount) treePrefabs.RemoveAt(treePrefabs.Count - 1);

            for (int i = 0; i < treePrefabs.Count; i++)
            {
                treePrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                    "Prefab " + i, treePrefabs[i], typeof(GameObject), false);
            }
            EditorGUILayout.EndVertical();
        }

        // Settings
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        heightOffset = EditorGUILayout.Slider("Height Offset (m)", heightOffset, 0f, 2f);
        scaleMin     = EditorGUILayout.Slider("Scale Min", scaleMin, 0.1f, 2f);
        scaleMax     = EditorGUILayout.Slider("Scale Max", scaleMax, 0.5f, 5f);
        minSpacing   = EditorGUILayout.Slider("Min Spacing (m)", minSpacing, 0f, 20f);
        maxTrees     = EditorGUILayout.IntSlider("Max Trees", maxTrees, 10, 8000);
        EditorGUILayout.EndVertical();

        // Terrain / boundary
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Terrain & Boundary", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        snapToTerrain = EditorGUILayout.Toggle("Snap To Cesium Tiles", snapToTerrain);
        if (snapToTerrain)
        {
            raycastStartHeight = EditorGUILayout.FloatField("Ray Start Height (m)", raycastStartHeight);
            raycastDistance    = EditorGUILayout.FloatField("Ray Distance (m)", raycastDistance);
            EditorGUILayout.HelpBox(
                "Trees are placed ONLY where a Cesium tile is loaded — the raycast " +
                "doubles as the boundary test. Anything outside the photogrammetry " +
                "is skipped.\n\n" +
                "Fly the Scene view over the whole area first so the tiles stream in.",
                MessageType.Warning);
        }
        EditorGUILayout.EndVertical();

        // Performance
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Performance (VR)", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        addLODGroup = EditorGUILayout.Toggle("Add LOD Group", addLODGroup);
        if (addLODGroup)
            lodCullRatio = EditorGUILayout.Slider("Cull Below (screen %)", lodCullRatio, 0.005f, 0.15f);
        enableGPUInstancing = EditorGUILayout.Toggle("Enable GPU Instancing", enableGPUInstancing);
        EditorGUILayout.EndVertical();

        EditorGUILayout.HelpBox(
            "Test with Max Trees = 200 first, then raise it.\n" +
            "LOD culling + GPU instancing are what make ~2000 trees viable in VR.",
            MessageType.Info);

        // Import button
        EditorGUILayout.Space(8f);
        GUI.enabled = fileExists && georeference != null;
        if (GUILayout.Button("Import Trees", GUILayout.Height(36f)))
            ImportTrees();
        GUI.enabled = true;

        EditorGUILayout.Space(6f);
        EditorGUILayout.EndScrollView();
    }

    void ImportTrees()
    {
        EditorUtility.DisplayProgressBar("OSM Tree Importer", "Parsing OSM file...", 0f);

        // Load prefabs
        List<GameObject> prefabs = BuildPrefabList();
        if (prefabs.Count == 0)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("No Prefabs",
                "No tree prefabs could be loaded.\n\n" +
                "Make sure TerrainSampleAssets is imported, or assign custom prefabs " +
                "in the Custom Prefabs section.",
                "OK");
            return;
        }

        // Parse tree positions
        List<TreeNode> trees = new List<TreeNode>();
        try
        {
            ParseTrees(osmFilePath, trees);
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Parse Error", ex.Message, "OK");
            Debug.LogError("[OSMTrees] " + ex);
            return;
        }

        if (trees.Count == 0)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("No Trees Found",
                "No natural=tree nodes found in the file.\n\n" +
                "Make sure your Overpass query includes node[\"natural\"=\"tree\"]",
                "OK");
            return;
        }

        Debug.Log("[OSMTrees] Found " + trees.Count + " tree nodes in OSM file.");

        // Turn on GPU instancing for every material these prefabs use, so
        // thousands of copies of the same mesh collapse into few draw calls.
        int instancedMats = 0;
        if (enableGPUInstancing)
            instancedMats = EnableInstancingOn(prefabs);

        // FIX: vertices/positions from LatLonToUnity are in georeference-LOCAL
        // space, so the parent must live under the georeference with a zeroed
        // transform. At the scene root Unity treats them as world coordinates.
        GameObject parent = GameObject.Find("Trees");
        if (parent == null) parent = new GameObject("Trees");

        parent.transform.SetParent(georeference.transform, false);
        parent.transform.localPosition = Vector3.zero;
        parent.transform.localRotation = Quaternion.identity;
        parent.transform.localScale    = Vector3.one;

        // Clear a previous run so re-importing replaces instead of stacking.
        for (int c = parent.transform.childCount - 1; c >= 0; c--)
            DestroyImmediate(parent.transform.GetChild(c).gameObject);

        System.Random rng = new System.Random(42);
        List<Vector3> placed = new List<Vector3>();
        int created   = 0;
        int tooClose  = 0;
        int outside   = 0;
        int total     = Mathf.Min(trees.Count, maxTrees);

        for (int i = 0; i < trees.Count && created < total; i++)
        {
            TreeNode t = trees[i];

            if ((i & 63) == 0)
                EditorUtility.DisplayProgressBar("OSM Tree Importer",
                    "Scanned " + i + "/" + trees.Count + "   placed: " + created,
                    (float)i / (float)trees.Count);

            Vector3 pos = LatLonToUnity(t.lat, t.lon);

            // Snap onto the Cesium tile. A miss means there is no photogrammetry
            // here, so the tree is outside the boundary and gets dropped.
            if (snapToTerrain)
            {
                Vector3 snapped;
                if (!TrySnapLocalToGround(pos, out snapped)) { outside++; continue; }
                pos = snapped;
            }
            pos.y += heightOffset;

            // Minimum spacing check
            if (minSpacing > 0f && IsTooClose(pos, placed, minSpacing))
            {
                tooClose++;
                continue;
            }

            GameObject prefab = PickPrefab(prefabs, t.species, rng);

            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.transform);
            // localPosition — the hierarchy sits in georeference space now.
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, (float)(rng.NextDouble() * 360.0), 0f);

            float s = scaleMin + (float)(rng.NextDouble() * (scaleMax - scaleMin));
            // Scale further by OSM height if available (normalised around 10 m average)
            if (t.height > 0f) s *= Mathf.Clamp(t.height / 10f, 0.3f, 3f);
            go.transform.localScale = Vector3.one * s;

            go.name = string.IsNullOrEmpty(t.species)
                ? "Tree_" + created.ToString("D4")
                : t.species + "_" + created.ToString("D4");

            if (addLODGroup)
                AddCullingLOD(go, lodCullRatio);

            placed.Add(pos);
            created++;
        }

        EditorUtility.ClearProgressBar();

        string msg = created + " trees placed.\n\n"
                   + outside  + " skipped — outside the Cesium tiles\n"
                   + tooClose + " skipped — closer than " + minSpacing + " m\n"
                   + "Total OSM nodes: " + trees.Count;

        if (enableGPUInstancing)
            msg += "\n\nGPU instancing enabled on " + instancedMats + " material(s).";

        if (created == 0 && outside > 0)
            msg += "\n\nNothing was placed because no Cesium tiles are loaded. " +
                   "Fly the Scene view over the photogrammetry area, then re-run.";

        Debug.Log("[OSMTrees] " + msg.Replace("\n", " | "));
        EditorUtility.DisplayDialog("OSM Tree Importer", msg, "OK");
    }

    // ── Terrain snap / boundary test ──────────────────────────────────────────

    /// <summary>
    /// Takes a point in georeference-LOCAL space, raycasts down onto the loaded
    /// Cesium tiles in world space, and returns the ground point back in local
    /// space. Returns false when nothing was hit — which is also how we know the
    /// tree lies outside the photogrammetry.
    ///
    /// Only MeshCollider hits count. The Cesium3DTileset root carries a huge
    /// BoxCollider proxy that sits far above the terrain; accepting it would
    /// flatten every tree onto that slab.
    /// </summary>
    bool TrySnapLocalToGround(Vector3 local, out Vector3 snappedLocal)
    {
        snappedLocal = local;
        if (georeference == null) return false;

        Transform gt    = georeference.transform;
        Vector3   world = gt.TransformPoint(local);

        Ray ray = new Ray(world + Vector3.up * raycastStartHeight, Vector3.down);

        RaycastHit[] all = Physics.RaycastAll(ray,
                                              raycastStartHeight + raycastDistance,
                                              ~0, QueryTriggerInteraction.Ignore);
        if (all == null || all.Length == 0) return false;

        bool    found   = false;
        float   bestDst = float.MaxValue;
        Vector3 bestPt  = Vector3.zero;

        for (int i = 0; i < all.Length; i++)
        {
            if (!(all[i].collider is MeshCollider)) continue;
            if (all[i].distance >= bestDst) continue;
            bestDst = all[i].distance;
            bestPt  = all[i].point;
            found   = true;
        }

        if (!found) return false;

        snappedLocal = gt.InverseTransformPoint(bestPt);
        return true;
    }

    // ── Performance helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Flips enableInstancing on every material used by the tree prefabs.
    /// Unity can then batch all copies of one mesh+material into a single
    /// draw call, which is the difference between 2000 trees being fine
    /// and 2000 trees tanking the framerate.
    /// </summary>
    static int EnableInstancingOn(List<GameObject> prefabs)
    {
        HashSet<Material> seen = new HashSet<Material>();
        int changed = 0;

        foreach (GameObject p in prefabs)
        {
            if (p == null) continue;

            Renderer[] rends = p.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rends)
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || seen.Contains(m)) continue;
                    seen.Add(m);

                    if (!m.enableInstancing)
                    {
                        m.enableInstancing = true;
                        EditorUtility.SetDirty(m);
                        changed++;
                    }
                }
            }
        }

        if (changed > 0) AssetDatabase.SaveAssets();
        return seen.Count;
    }

    /// <summary>
    /// Adds a single-level LODGroup that culls the tree once it shrinks below
    /// cullRatio of screen height. The prefabs have no reduced-detail meshes,
    /// so there is nothing to swap down to — but distance culling alone
    /// removes most of the cost, since far trees stop rendering entirely.
    /// </summary>
    static void AddCullingLOD(GameObject go, float cullRatio)
    {
        // If the prefab already ships an LOD chain (the BOTD conifer does —
        // real reduced meshes plus a billboard), leave it alone. Replacing it
        // with a single cull-only level would throw away better work.
        LODGroup existing = go.GetComponentInChildren<LODGroup>(true);
        if (existing != null) return;

        Renderer[] rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends == null || rends.Length == 0) return;

        LODGroup grp = go.AddComponent<LODGroup>();
        grp.SetLODs(new LOD[] { new LOD(cullRatio, rends) });
        grp.RecalculateBounds();
    }

    void ParseTrees(string path, List<TreeNode> trees)
    {
        XmlDocument doc = new XmlDocument();
        doc.Load(path);

        foreach (XmlNode n in doc.SelectNodes("//node"))
        {
            if (n.Attributes["lat"] == null || n.Attributes["lon"] == null) continue;

            double lat;
            double lon;
            if (!double.TryParse(n.Attributes["lat"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat)) continue;
            if (!double.TryParse(n.Attributes["lon"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lon)) continue;

            // Bbox filter
            if (lat < LAT_MIN || lat > LAT_MAX || lon < LON_MIN || lon > LON_MAX) continue;

            // Check for natural=tree tag
            bool isTree = false;
            string species = "";
            float height = 0f;

            foreach (XmlNode tag in n.SelectNodes("tag"))
            {
                if (tag.Attributes["k"] == null || tag.Attributes["v"] == null) continue;
                string k = tag.Attributes["k"].Value;
                string v = tag.Attributes["v"].Value;

                if (k == "natural" && v == "tree") isTree = true;
                if (k == "species" || k == "taxon") species = v;
                if (k == "height")
                {
                    // Height can be "15", "15 m", "15m" — strip non-numeric
                    string hStr = "";
                    foreach (char c in v)
                        if (char.IsDigit(c) || c == '.') hStr += c;
                    float.TryParse(hStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out height);
                }
            }

            if (!isTree) continue;

            TreeNode t = new TreeNode();
            t.lat     = lat;
            t.lon     = lon;
            t.species = species;
            t.height  = height;
            trees.Add(t);
        }
    }

    // ── Prefab helpers ────────────────────────────────────────────────────────

    List<GameObject> BuildPrefabList()
    {
        // User-assigned prefabs take priority
        List<GameObject> result = new List<GameObject>();
        foreach (GameObject p in treePrefabs)
            if (p != null) result.Add(p);

        if (result.Count > 0) return result;

        // Prefer real tree prefabs
        foreach (string path in DefaultPrefabPaths)
        {
            GameObject p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p != null) result.Add(p);
        }

        if (result.Count > 0) return result;

        // Nothing tree-shaped available — fall back to the sample plants.
        Debug.LogWarning("[OSMTrees] No tree prefab found, falling back to " +
                         "TerrainSampleAssets plants (these are bushes, not trees).");
        foreach (string path in FallbackPrefabPaths)
        {
            GameObject p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p != null) result.Add(p);
        }
        return result;
    }

    GameObject PickPrefab(List<GameObject> prefabs, string species, System.Random rng)
    {
        // Simple species-based routing — pick a taller prefab for known tall species
        if (!string.IsNullOrEmpty(species))
        {
            string s = species.ToLower();
            // Tall species → second half of list, small species → first half
            bool isTall = s.Contains("quercus") || s.Contains("oak")  ||
                          s.Contains("tilia")   || s.Contains("lime")  ||
                          s.Contains("platanus") || s.Contains("plane") ||
                          s.Contains("fagus")   || s.Contains("beech");
            if (isTall && prefabs.Count > 2)
                return prefabs[rng.Next(prefabs.Count / 2, prefabs.Count)];
        }
        return prefabs[rng.Next(prefabs.Count)];
    }

    // ── Spacing check ─────────────────────────────────────────────────────────

    static bool IsTooClose(Vector3 pos, List<Vector3> placed, float minDist)
    {
        float minDistSq = minDist * minDist;
        foreach (Vector3 p in placed)
        {
            float dx = pos.x - p.x;
            float dz = pos.z - p.z;
            if (dx * dx + dz * dz < minDistSq) return true;
        }
        return false;
    }

    // ── Coordinate conversion ─────────────────────────────────────────────────

    Vector3 LatLonToUnity(double lat, double lon)
    {
        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, 200.0));
        double3 u = georeference
            .TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)u.x, (float)u.y, (float)u.z);
    }

    // ── Data class ────────────────────────────────────────────────────────────

    class TreeNode
    {
        public double lat;
        public double lon;
        public string species;
        public float  height;
    }
}
