using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;
using UnityEditor;
using CesiumForUnity;
using Unity.Mathematics;

public class OSMGreenAreaImporter : EditorWindow
{
    private string osmFilePath = "Assets/GTFS/map_green.osm";
    private CesiumGeoreference georeference;

    private bool incPark       = true;
    private bool incGarden     = true;
    private bool incPitch      = true;
    private bool incPlayground = false;
    private bool incGrass      = true;
    private bool incMeadow     = true;
    private bool incRecreation = true;
    private bool incGrassland  = true;

    private float heightOffset = 0.12f;
    private float uvTileSize   = 10f;
    private int   maxAreas     = 300;

    // Terrain draping
    private bool  snapToTerrain      = true;
    private float raycastStartHeight = 400f;   // start ray this far above the point
    private float raycastDistance    = 1500f;  // how far down to search

    // 3D grass scattering
    private bool  scatterGrass     = true;
    private float grassDensity     = 0.3f;   // blades per square metre
    private int   maxGrassPerArea  = 400;    // hard cap per polygon
    private float grassScaleMin    = 0.6f;
    private float grassScaleMax    = 1.2f;

    private static readonly string[] GrassPrefabPaths = new string[]
    {
        "Assets/TerrainSampleAssets/Prefabs/Grass_A.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Grass_B.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Grass_C.prefab",
        "Assets/TerrainSampleAssets/Prefabs/Grass_D.prefab",
    };

    private Material grassMaterial;
    private Vector2  scroll;

    private const double LAT_MIN = 51.99;
    private const double LAT_MAX = 52.22;
    private const double LON_MIN = 11.50;
    private const double LON_MAX = 11.75;

    [MenuItem("Tools/OSM Green Area Importer")]
    public static void ShowWindow()
    {
        OSMGreenAreaImporter w = GetWindow<OSMGreenAreaImporter>("OSM Green Areas");
        w.minSize = new Vector2(370f, 520f);
    }

    void OnEnable()
    {
        grassMaterial = GetOrCreateGrassMaterial();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("OSM Green Area Importer", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);

        // OSM file
        EditorGUILayout.LabelField("OSM File", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        osmFilePath = EditorGUILayout.TextField(osmFilePath);
        if (GUILayout.Button("Browse", GUILayout.Width(60f)))
        {
            string p = EditorUtility.OpenFilePanel("Select .osm file", Application.dataPath, "osm");
            if (!string.IsNullOrEmpty(p))
                osmFilePath = p;
        }
        EditorGUILayout.EndHorizontal();

        bool fileExists = File.Exists(osmFilePath);
        if (!fileExists)
        {
            EditorGUILayout.HelpBox(
                "File not found. Download from overpass-turbo.eu and save as Assets/GTFS/map_green.osm\n\n" +
                "Use this query:\n" +
                "[out:xml][bbox:51.99,11.50,52.22,11.75];\n" +
                "(way[\"leisure\"~\"park|garden|pitch\"];\n" +
                "way[\"landuse\"~\"grass|meadow|recreation_ground\"];\n" +
                "way[\"natural\"~\"grassland\"];);\n" +
                "(._;>;);out body;",
                MessageType.Warning);
        }

        // Scene reference
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Scene Reference", EditorStyles.boldLabel);
        georeference = (CesiumGeoreference)EditorGUILayout.ObjectField(
            "Cesium GeoReference", georeference, typeof(CesiumGeoreference), true);
        if (georeference == null)
            EditorGUILayout.HelpBox("Drag CesiumGeoReference from the Hierarchy.", MessageType.Warning);

        // Toggles
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Area Types to Import", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        incPark       = EditorGUILayout.Toggle("leisure = park",              incPark);
        incGarden     = EditorGUILayout.Toggle("leisure = garden",            incGarden);
        incPitch      = EditorGUILayout.Toggle("leisure = pitch",             incPitch);
        incPlayground = EditorGUILayout.Toggle("leisure = playground",        incPlayground);
        incGrass      = EditorGUILayout.Toggle("landuse = grass",             incGrass);
        incMeadow     = EditorGUILayout.Toggle("landuse = meadow",            incMeadow);
        incRecreation = EditorGUILayout.Toggle("landuse = recreation_ground", incRecreation);
        incGrassland  = EditorGUILayout.Toggle("natural = grassland",         incGrassland);
        EditorGUILayout.EndVertical();

        // Settings
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        grassMaterial = (Material)EditorGUILayout.ObjectField(
            "Grass Material", grassMaterial, typeof(Material), false);
        heightOffset = EditorGUILayout.Slider("Height Offset (m)", heightOffset, 0f, 1f);
        uvTileSize   = EditorGUILayout.FloatField("UV Tile Size (m)", uvTileSize);
        maxAreas     = EditorGUILayout.IntSlider("Max Areas", maxAreas, 10, 2000);

        EditorGUILayout.Space(4f);
        snapToTerrain = EditorGUILayout.Toggle("Drape Onto Terrain", snapToTerrain);
        if (snapToTerrain)
        {
            raycastStartHeight = EditorGUILayout.FloatField("Ray Start Height (m)", raycastStartHeight);
            raycastDistance    = EditorGUILayout.FloatField("Ray Distance (m)", raycastDistance);
            EditorGUILayout.HelpBox(
                "Each vertex is raycast down onto the loaded Cesium tiles, so the mesh " +
                "follows the real ground.\n\n" +
                "IMPORTANT: fly the Scene view over the areas first so Cesium streams in " +
                "the tiles — the raycast can only hit tiles that are already loaded.",
                MessageType.Warning);
        }

        EditorGUILayout.HelpBox("Start with Max Areas = 100 to test first.", MessageType.Info);

        // 3D grass scattering
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("3D Grass Blades", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        scatterGrass    = EditorGUILayout.Toggle("Scatter 3D Grass Prefabs", scatterGrass);
        if (scatterGrass)
        {
            grassDensity    = EditorGUILayout.Slider("Density (blades/m2)", grassDensity, 0.05f, 2f);
            maxGrassPerArea = EditorGUILayout.IntSlider("Max Blades Per Area", maxGrassPerArea, 50, 1000);
            grassScaleMin   = EditorGUILayout.Slider("Scale Min", grassScaleMin, 0.2f, 1f);
            grassScaleMax   = EditorGUILayout.Slider("Scale Max", grassScaleMax, 0.5f, 2f);
            EditorGUILayout.HelpBox(
                "Uses Grass_A/B/C/D from TerrainSampleAssets.\n" +
                "Keep Max Blades low (200-400) for performance.\n" +
                "Increase density for a thicker look.", MessageType.Info);
        }
        EditorGUILayout.EndVertical();

        // Button
        EditorGUILayout.Space(8f);
        GUI.enabled = fileExists && georeference != null && grassMaterial != null;
        if (GUILayout.Button("Import Green Areas", GUILayout.Height(36f)))
            ImportGreenAreas();
        GUI.enabled = true;

        EditorGUILayout.Space(6f);
        EditorGUILayout.EndScrollView();
    }

    void ImportGreenAreas()
    {
        EditorUtility.DisplayProgressBar("OSM Green Areas", "Parsing OSM file...", 0f);

        Dictionary<long, GreenNode> nodes = new Dictionary<long, GreenNode>();
        List<GreenPoly> polys = new List<GreenPoly>();

        try
        {
            ParseOSM(osmFilePath, nodes, polys);
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Parse Error", ex.Message, "OK");
            Debug.LogError("[OSMGreen] " + ex);
            return;
        }

        if (nodes.Count == 0)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("No Node Coordinates",
                "The file has no node coordinates.\n\n" +
                "Make sure your query includes (._;>;); before out body;",
                "OK");
            return;
        }

        Debug.Log("[OSMGreen] " + nodes.Count + " nodes, " + polys.Count + " polygons.");

        // FIX: the mesh vertices produced by LatLonToUnity are in the
        // georeference's LOCAL space, so the parent must sit under the
        // georeference with a zeroed transform. Parenting it at the scene
        // root made Unity read those local values as world coordinates,
        // which is what pushed every area off the ground.
        GameObject parent = GameObject.Find("Green Areas");
        if (parent == null)
            parent = new GameObject("Green Areas");

        parent.transform.SetParent(georeference.transform, false);
        parent.transform.localPosition = Vector3.zero;
        parent.transform.localRotation = Quaternion.identity;
        parent.transform.localScale    = Vector3.one;

        // Clear anything from a previous run, otherwise re-importing stacks a
        // second copy of every area on top of the first.
        for (int c = parent.transform.childCount - 1; c >= 0; c--)
            DestroyImmediate(parent.transform.GetChild(c).gameObject);

        int created  = 0;
        int skipped  = 0;
        int noGround = 0;
        int total    = Mathf.Min(polys.Count, maxAreas);

        for (int i = 0; i < total; i++)
        {
            GreenPoly poly = polys[i];
            EditorUtility.DisplayProgressBar("OSM Green Areas",
                "Area " + (i + 1) + "/" + total, (float)i / (float)total);

            // Convert each node, draping it onto the terrain as we go.
            List<Vector3> pts  = new List<Vector3>();
            List<bool>    hits = new List<bool>();

            for (int k = 0; k < poly.nodeRefs.Count; k++)
            {
                GreenNode nd;
                if (!nodes.TryGetValue(poly.nodeRefs[k], out nd)) continue;
                if (nd.lat < LAT_MIN || nd.lat > LAT_MAX) continue;
                if (nd.lon < LON_MIN || nd.lon > LON_MAX) continue;

                Vector3 p  = LatLonToUnity(nd.lat, nd.lon);
                bool    ok = false;

                if (snapToTerrain)
                {
                    Vector3 snapped;
                    if (TrySnapLocalToGround(p, out snapped)) { p = snapped; ok = true; }
                }

                pts.Add(p);
                hits.Add(ok);
            }

            // Drop the duplicated closing vertex of the ring
            if (pts.Count > 1 && Vector3.Distance(pts[0], pts[pts.Count - 1]) < 0.5f)
            {
                pts.RemoveAt(pts.Count - 1);
                hits.RemoveAt(hits.Count - 1);
            }

            if (pts.Count < 3) { skipped++; continue; }

            // Any vertex whose ray missed (tile not streamed in yet) inherits
            // the average height of the vertices that did hit, so a single
            // miss can't spike the mesh 100 m into the air.
            float avgY;
            if (snapToTerrain)
            {
                float sum = 0f; int n = 0;
                for (int k = 0; k < pts.Count; k++)
                    if (hits[k]) { sum += pts[k].y; n++; }

                if (n == 0) { noGround++; skipped++; continue; }

                avgY = sum / n;
                for (int k = 0; k < pts.Count; k++)
                    if (!hits[k]) { Vector3 p = pts[k]; p.y = avgY; pts[k] = p; }
            }
            else
            {
                float sum = 0f;
                for (int k = 0; k < pts.Count; k++) sum += pts[k].y;
                avgY = sum / pts.Count;
            }

            List<Vector2> flat = new List<Vector2>(pts.Count);
            for (int k = 0; k < pts.Count; k++)
                flat.Add(new Vector2(pts[k].x, pts[k].z));

            // Reverse BOTH lists together — reversing only `flat` used to
            // desync it from the per-vertex heights in `pts`.
            if (SignedArea(flat) < 0f)
            {
                flat.Reverse();
                pts.Reverse();
            }

            int[] tris = EarClip(flat);
            if (tris == null || tris.Length < 3) { skipped++; continue; }

            Vector3[] verts = new Vector3[pts.Count];
            Vector2[] uvs   = new Vector2[pts.Count];
            for (int v = 0; v < pts.Count; v++)
            {
                // Per-vertex height, so the mesh follows sloping ground
                // instead of being flattened to one average plane.
                verts[v] = new Vector3(pts[v].x, pts[v].y + heightOffset, pts[v].z);
                uvs[v]   = new Vector2(pts[v].x / uvTileSize, pts[v].z / uvTileSize);
            }

            Mesh mesh      = new Mesh();
            mesh.name      = string.IsNullOrEmpty(poly.name) ? "area" : poly.name;
            mesh.vertices  = verts;
            mesh.triangles = tris;
            mesh.uv        = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            string goName = string.IsNullOrEmpty(poly.name)
                ? "GreenArea_" + created.ToString("D4")
                : poly.name;

            GameObject go = new GameObject(goName);
            // worldPositionStays: false — keep the child at local (0,0,0) so the
            // vertex coordinates stay in georeference space.
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = grassMaterial;

            if (scatterGrass)
                ScatterGrass(flat, avgY, go);

            created++;
        }

        EditorUtility.ClearProgressBar();
        string msg = created + " areas created, " + skipped + " skipped.";
        if (noGround > 0)
            msg += "\n\n" + noGround + " skipped because no Cesium tile was loaded " +
                   "beneath them. Fly the Scene view over those areas so the tiles " +
                   "stream in, then run the import again.";
        Debug.Log("[OSMGreen] " + msg);
        EditorUtility.DisplayDialog("OSM Green Areas", msg, "OK");
    }

    void ParseOSM(string path, Dictionary<long, GreenNode> nodes, List<GreenPoly> polys)
    {
        XmlDocument doc = new XmlDocument();
        doc.Load(path);

        foreach (XmlNode n in doc.SelectNodes("//node"))
        {
            if (n.Attributes["id"]  == null) continue;
            if (n.Attributes["lat"] == null) continue;
            if (n.Attributes["lon"] == null) continue;

            long id;
            if (!long.TryParse(n.Attributes["id"].Value, out id)) continue;

            double lat;
            double lon;
            if (!double.TryParse(n.Attributes["lat"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat)) continue;
            if (!double.TryParse(n.Attributes["lon"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lon)) continue;

            GreenNode node = new GreenNode();
            node.lat = lat;
            node.lon = lon;
            nodes[id] = node;
        }

        foreach (XmlNode w in doc.SelectNodes("//way"))
        {
            string leisure = null;
            string landuse = null;
            string natural = null;
            string name    = "";

            foreach (XmlNode tag in w.SelectNodes("tag"))
            {
                if (tag.Attributes["k"] == null || tag.Attributes["v"] == null) continue;
                string k = tag.Attributes["k"].Value;
                string v = tag.Attributes["v"].Value;
                if      (k == "leisure") leisure = v;
                else if (k == "landuse") landuse = v;
                else if (k == "natural") natural = v;
                else if (k == "name")    name    = v;
            }

            if (!IsGreenArea(leisure, landuse, natural)) continue;

            List<long> refs = new List<long>();
            foreach (XmlNode nd in w.SelectNodes("nd"))
            {
                if (nd.Attributes["ref"] == null) continue;
                long r;
                if (long.TryParse(nd.Attributes["ref"].Value, out r))
                    refs.Add(r);
            }

            if (refs.Count >= 3)
            {
                GreenPoly p = new GreenPoly();
                p.name     = name;
                p.nodeRefs = refs;
                polys.Add(p);
            }
        }
    }

    bool IsGreenArea(string leisure, string landuse, string natural)
    {
        if (leisure != null)
        {
            if (leisure == "park"       && incPark)       return true;
            if (leisure == "garden"     && incGarden)     return true;
            if (leisure == "pitch"      && incPitch)      return true;
            if (leisure == "playground" && incPlayground) return true;
        }
        if (landuse != null)
        {
            if (landuse == "grass"             && incGrass)      return true;
            if (landuse == "meadow"            && incMeadow)     return true;
            if (landuse == "recreation_ground" && incRecreation) return true;
            if (landuse == "village_green"     && incGrass)      return true;
        }
        if (natural != null)
        {
            if (natural == "grassland" && incGrassland) return true;
            if (natural == "scrub"     && incGrassland) return true;
        }
        return false;
    }

    static int[] EarClip(List<Vector2> poly)
    {
        int n = poly.Count;
        if (n < 3) return null;

        List<int> tris = new List<int>();
        List<int> rem  = new List<int>(n);
        for (int i = 0; i < n; i++) rem.Add(i);

        int safety = n * n + 10;
        int iter   = 0;

        while (rem.Count > 3 && iter < safety)
        {
            iter++;
            bool found = false;
            for (int i = 0; i < rem.Count; i++)
            {
                int prev = rem[(i - 1 + rem.Count) % rem.Count];
                int curr = rem[i];
                int next = rem[(i + 1) % rem.Count];

                Vector2 a = poly[prev];
                Vector2 b = poly[curr];
                Vector2 c = poly[next];

                if (Cross(a, b, c) <= 0f) continue;

                bool ear = true;
                for (int j = 0; j < rem.Count; j++)
                {
                    int idx = rem[j];
                    if (idx == prev || idx == curr || idx == next) continue;
                    if (InTri(poly[idx], a, b, c)) { ear = false; break; }
                }

                if (!ear) continue;

                tris.Add(prev);
                tris.Add(curr);
                tris.Add(next);
                rem.RemoveAt(i);
                found = true;
                break;
            }
            if (!found) break;
        }

        if (rem.Count == 3)
        {
            tris.Add(rem[0]);
            tris.Add(rem[1]);
            tris.Add(rem[2]);
        }

        return tris.ToArray();
    }

    static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    static float SignedArea(List<Vector2> p)
    {
        float area = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            int j = (i + 1) % p.Count;
            area += p[i].x * p[j].y - p[j].x * p[i].y;
        }
        return area * 0.5f;
    }

    static bool InTri(Vector2 pt, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(a, b, pt);
        float d2 = Cross(b, c, pt);
        float d3 = Cross(c, a, pt);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    // ── 3D grass scattering ───────────────────────────────────────────────────

    void ScatterGrass(List<Vector2> flat, float y, GameObject parent)
    {
        // Load prefabs
        List<GameObject> prefabs = new List<GameObject>();
        foreach (string path in GrassPrefabPaths)
        {
            GameObject p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p != null) prefabs.Add(p);
        }
        if (prefabs.Count == 0)
        {
            Debug.LogWarning("[OSMGreen] No grass prefabs found in TerrainSampleAssets.");
            return;
        }

        // Bounding box of the polygon
        float minX = flat[0].x, maxX = flat[0].x;
        float minZ = flat[0].y, maxZ = flat[0].y;
        for (int i = 1; i < flat.Count; i++)
        {
            if (flat[i].x < minX) minX = flat[i].x;
            if (flat[i].x > maxX) maxX = flat[i].x;
            if (flat[i].y < minZ) minZ = flat[i].y;
            if (flat[i].y > maxZ) maxZ = flat[i].y;
        }

        float bboxArea = (maxX - minX) * (maxZ - minZ);
        int   target   = Mathf.Min((int)(bboxArea * grassDensity), maxGrassPerArea);
        if (target < 1) return;

        // Create a single parent for all blades to keep hierarchy clean
        GameObject bladeParent = new GameObject("GrassBlades");
        bladeParent.transform.SetParent(parent.transform, false);

        System.Random rng     = new System.Random(parent.GetInstanceID());
        int placed  = 0;
        int maxTry  = target * 4;

        for (int attempt = 0; attempt < maxTry && placed < target; attempt++)
        {
            float rx = minX + (float)rng.NextDouble() * (maxX - minX);
            float rz = minZ + (float)rng.NextDouble() * (maxZ - minZ);

            if (!PointInPolygon(new Vector2(rx, rz), flat)) continue;

            // Drape each blade individually so it sits on the ground rather
            // than on the polygon's average plane.
            Vector3 local = new Vector3(rx, y, rz);
            if (snapToTerrain)
            {
                Vector3 snapped;
                if (TrySnapLocalToGround(local, out snapped)) local = snapped;
            }
            local.y += heightOffset;

            GameObject prefab = prefabs[rng.Next(prefabs.Count)];
            GameObject blade  = (GameObject)PrefabUtility.InstantiatePrefab(prefab, bladeParent.transform);
            // localPosition, not position — the whole hierarchy lives in
            // georeference space now.
            blade.transform.localPosition = local;
            blade.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            float s = grassScaleMin + (float)rng.NextDouble() * (grassScaleMax - grassScaleMin);
            blade.transform.localScale = new Vector3(s, s, s);
            placed++;
        }
    }

    /// <summary>Ray-casting point-in-polygon test (XZ plane).</summary>
    static bool PointInPolygon(Vector2 pt, List<Vector2> poly)
    {
        bool inside = false;
        int  n      = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = poly[i];
            Vector2 pj = poly[j];
            if ((pi.y > pt.y) != (pj.y > pt.y) &&
                pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y) + pi.x)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Takes a point in georeference-LOCAL space, converts it to world space,
    /// raycasts straight down onto whatever Cesium physics meshes are loaded,
    /// and hands the ground position back in georeference-local space.
    /// Returns false if the ray hit nothing (tile not streamed in yet).
    /// </summary>
    bool TrySnapLocalToGround(Vector3 local, out Vector3 snappedLocal)
    {
        snappedLocal = local;
        if (georeference == null) return false;

        Transform gt    = georeference.transform;
        Vector3   world = gt.TransformPoint(local);

        Ray ray = new Ray(world + Vector3.up * raycastStartHeight, Vector3.down);

        // Only accept MeshCollider hits. Cesium generates a MeshCollider per
        // streamed tile, so that is the real ground. Plain BoxColliders in this
        // scene are proxies that would otherwise swallow the ray far above the
        // terrain — the Cesium3DTileset root carries one spanning the whole
        // photogrammetry area, and the boundary walls carry more.
        RaycastHit[] all = Physics.RaycastAll(ray,
                                              raycastStartHeight + raycastDistance,
                                              ~0, QueryTriggerInteraction.Ignore);
        if (all == null || all.Length == 0) return false;

        bool  found   = false;
        float bestDst = float.MaxValue;
        Vector3 bestPt = Vector3.zero;

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

    Vector3 LatLonToUnity(double lat, double lon)
    {
        // Use 200 m above the ellipsoid as the height hint — same value used
        // by TransitSimulationManager.  At height = 0 the point is underground
        // in this Cesium scene because the georeference origin sits well above
        // the WGS84 ellipsoid surface.
        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, 200.0));
        double3 u = georeference
            .TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)u.x, (float)u.y, (float)u.z);
    }

    static Material GetOrCreateGrassMaterial()
    {
        const string matPath = "Assets/Materials/GrassMaterial.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        Material mat = new Material(shader);
        mat.name = "GrassMaterial";
        mat.SetColor("_BaseColor", new Color(0.28f, 0.50f, 0.20f, 1f));
        mat.SetColor("_Color",     new Color(0.28f, 0.50f, 0.20f, 1f));
        mat.SetFloat("_Smoothness", 0.05f);
        mat.SetFloat("_Metallic",   0f);
        // Double-sided so the mesh is visible regardless of winding order
        mat.SetFloat("_Cull", 0f);

        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[OSMGreen] Created GrassMaterial at " + matPath);
        return mat;
    }

    class GreenNode
    {
        public double lat;
        public double lon;
    }

    class GreenPoly
    {
        public string     name;
        public List<long> nodeRefs;
    }
}
