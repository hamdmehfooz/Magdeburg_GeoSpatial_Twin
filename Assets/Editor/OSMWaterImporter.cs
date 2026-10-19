using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;
using UnityEditor;
using CesiumForUnity;
using Unity.Mathematics;

/// <summary>
/// Imports water bodies from an OSM file as flat Unity meshes:
///   - Polygons  : natural=water, waterway=riverbank, landuse=reservoir, natural=bay
///   - River lines: waterway=river/canal/stream/drain (extruded as ribbon mesh)
///
/// Overpass query (overpass-turbo.eu, export as OSM):
///   [out:xml][bbox:51.99,11.50,52.22,11.75];
///   (
///     way["natural"="water"];
///     way["waterway"~"river|canal|stream|riverbank|drain"];
///     way["landuse"="reservoir"];
///   );
///   (._;>;);out body;
/// </summary>
public class OSMWaterImporter : EditorWindow
{
    private string osmFilePath = "Assets/GTFS/map_water.osm";
    private CesiumGeoreference georeference;

    // Area types
    private bool incWaterNatural = true;
    private bool incRiverbank    = true;
    private bool incReservoir    = true;

    // Line types
    private bool incRiver  = true;
    private bool incCanal  = true;
    private bool incStream = true;
    private bool incDrain  = false;

    // River widths (metres)
    private float riverWidth  = 20f;
    private float canalWidth  = 10f;
    private float streamWidth = 4f;
    private float drainWidth  = 2f;

    private float heightOffset = -0.05f;
    private float uvTileSize   = 20f;
    private int   maxFeatures  = 300;

    // Terrain draping. Water is placed on the ground the same way green areas
    // are, except the surface is kept planar instead of following the terrain.
    private bool  snapToTerrain      = true;
    private float raycastStartHeight = 400f;   // start ray this far above the point
    private float raycastDistance    = 1500f;  // how far down to search

    // Restrict output to the photogrammetry footprint. The parse bbox below
    // covers the whole Magdeburg region (~25 x 17 km), which produced 178
    // polygons where only a handful are inside the modelled campus. Everything
    // outside is invisible geometry that still costs draw calls.
    private bool   limitToFootprint = true;
    private double footLatMin = 52.1360;
    private double footLatMax = 52.1450;
    private double footLonMin = 11.6350;
    private double footLonMax = 11.6610;

    private Material waterMaterial;
    private Vector2  scroll;

    private const double LAT_MIN = 51.99;
    private const double LAT_MAX = 52.22;
    private const double LON_MIN = 11.50;
    private const double LON_MAX = 11.75;

    [MenuItem("Tools/OSM Water Importer")]
    public static void ShowWindow()
    {
        OSMWaterImporter w = GetWindow<OSMWaterImporter>("OSM Water");
        w.minSize = new Vector2(370f, 580f);
    }

    void OnEnable()
    {
        waterMaterial = GetOrCreateWaterMaterial();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("OSM Water Importer", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);

        // File
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
                "(way[\"natural\"=\"water\"];\n" +
                "way[\"waterway\"~\"river|canal|stream|riverbank\"];\n" +
                "way[\"landuse\"=\"reservoir\"];);\n" +
                "(._;>;);out body;",
                MessageType.Warning);

        // Scene ref
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Scene Reference", EditorStyles.boldLabel);
        georeference = (CesiumGeoreference)EditorGUILayout.ObjectField(
            "Cesium GeoReference", georeference, typeof(CesiumGeoreference), true);
        if (georeference == null)
            EditorGUILayout.HelpBox("Drag CesiumGeoReference from the Hierarchy.", MessageType.Warning);

        // Water area types
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Water Areas (polygons)", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        incWaterNatural = EditorGUILayout.Toggle("natural = water (lakes/ponds)", incWaterNatural);
        incRiverbank    = EditorGUILayout.Toggle("waterway = riverbank",           incRiverbank);
        incReservoir    = EditorGUILayout.Toggle("landuse = reservoir",            incReservoir);
        EditorGUILayout.EndVertical();

        // River line types
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("River Lines (ribbon mesh)", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        incRiver  = EditorGUILayout.Toggle("waterway = river",  incRiver);
        incCanal  = EditorGUILayout.Toggle("waterway = canal",  incCanal);
        incStream = EditorGUILayout.Toggle("waterway = stream", incStream);
        incDrain  = EditorGUILayout.Toggle("waterway = drain",  incDrain);
        EditorGUILayout.Space(4f);
        riverWidth  = EditorGUILayout.FloatField("River width (m)",  riverWidth);
        canalWidth  = EditorGUILayout.FloatField("Canal width (m)",  canalWidth);
        streamWidth = EditorGUILayout.FloatField("Stream width (m)", streamWidth);
        drainWidth  = EditorGUILayout.FloatField("Drain width (m)",  drainWidth);
        EditorGUILayout.EndVertical();

        // Settings
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        waterMaterial = (Material)EditorGUILayout.ObjectField(
            "Water Material", waterMaterial, typeof(Material), false);
        heightOffset = EditorGUILayout.Slider("Height Offset (m)", heightOffset, -2f, 1f);
        EditorGUILayout.LabelField(" ", "negative sinks the surface below the bank",
            EditorStyles.miniLabel);
        uvTileSize   = EditorGUILayout.FloatField("UV Tile Size (m)", uvTileSize);
        maxFeatures  = EditorGUILayout.IntSlider("Max Features", maxFeatures, 10, 2000);
        EditorGUILayout.HelpBox("Start with Max Features = 100 to test first.", MessageType.Info);

        // Terrain draping
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Ground Placement", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        snapToTerrain = EditorGUILayout.Toggle("Snap Onto Terrain", snapToTerrain);
        if (snapToTerrain)
        {
            raycastStartHeight = EditorGUILayout.FloatField("Ray Start Height (m)", raycastStartHeight);
            raycastDistance    = EditorGUILayout.FloatField("Ray Distance (m)", raycastDistance);
            EditorGUILayout.HelpBox(
                "Every boundary vertex is raycast down onto the loaded Cesium tiles " +
                "and the median hit height becomes the water plane, so the surface " +
                "stays flat but sits on the real ground.\n\n" +
                "IMPORTANT: fly the Scene view over the water first so Cesium streams " +
                "the tiles in — the raycast can only hit tiles that are already loaded.",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "With snapping off, water is placed at a fixed ellipsoidal height of " +
                "200 m — roughly 100 m above this campus. Leave it on.",
                MessageType.Error);
        }
        EditorGUILayout.EndVertical();

        // Footprint culling
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Footprint Filter", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        limitToFootprint = EditorGUILayout.Toggle("Limit To Model Footprint", limitToFootprint);
        if (limitToFootprint)
        {
            footLatMin = EditorGUILayout.DoubleField("Lat Min", footLatMin);
            footLatMax = EditorGUILayout.DoubleField("Lat Max", footLatMax);
            footLonMin = EditorGUILayout.DoubleField("Lon Min", footLonMin);
            footLonMax = EditorGUILayout.DoubleField("Lon Max", footLonMax);
            EditorGUILayout.HelpBox(
                "Defaults cover the photogrammetry extent. Ways with no vertex inside " +
                "this box are skipped, so distant water bodies stop costing draw calls.",
                MessageType.Info);
        }
        EditorGUILayout.EndVertical();

        // Button
        EditorGUILayout.Space(8f);
        GUI.enabled = fileExists && georeference != null && waterMaterial != null;
        if (GUILayout.Button("Import Water", GUILayout.Height(36f)))
            ImportWater();
        GUI.enabled = true;

        EditorGUILayout.Space(6f);
        EditorGUILayout.EndScrollView();
    }

    void ImportWater()
    {
        EditorUtility.DisplayProgressBar("OSM Water Importer", "Parsing OSM file...", 0f);

        Dictionary<long, WaterNode> nodes = new Dictionary<long, WaterNode>();
        List<WaterWay> ways = new List<WaterWay>();

        try { ParseOSM(osmFilePath, nodes, ways); }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Parse Error", ex.Message, "OK");
            Debug.LogError("[OSMWater] " + ex);
            return;
        }

        if (nodes.Count == 0)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("No Node Coordinates",
                "File has no node coordinates.\n\nMake sure your query includes (._;>;); before out body;",
                "OK");
            return;
        }

        Debug.Log("[OSMWater] " + nodes.Count + " nodes, " + ways.Count + " water ways.");

        // The vertices produced by LatLonToUnity are in the georeference's LOCAL
        // space, so both parents must sit under the georeference with a zeroed
        // transform. Leaving them at the scene root made Unity read those local
        // values as world coordinates, which is half of why the water floated.
        GameObject parentAreas  = PrepareParent("Water Areas");
        GameObject parentRivers = PrepareParent("Rivers");

        int created  = 0;
        int skipped  = 0;
        int outside  = 0;
        int noGround = 0;
        int total    = Mathf.Min(ways.Count, maxFeatures);

        for (int i = 0; i < total; i++)
        {
            WaterWay way = ways[i];
            EditorUtility.DisplayProgressBar("OSM Water Importer",
                "Feature " + (i + 1) + "/" + total + " - " + way.name,
                (float)i / (float)total);

            // Convert each node, snapping it onto the terrain as we go.
            List<Vector3> pts     = new List<Vector3>();
            List<bool>    hits    = new List<bool>();
            bool          inScene = !limitToFootprint;

            for (int k = 0; k < way.nodeRefs.Count; k++)
            {
                WaterNode nd;
                if (!nodes.TryGetValue(way.nodeRefs[k], out nd)) continue;
                if (nd.lat < LAT_MIN || nd.lat > LAT_MAX) continue;
                if (nd.lon < LON_MIN || nd.lon > LON_MAX) continue;

                if (limitToFootprint &&
                    nd.lat >= footLatMin && nd.lat <= footLatMax &&
                    nd.lon >= footLonMin && nd.lon <= footLonMax)
                    inScene = true;

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

            // A way with no vertex inside the modelled area is invisible
            // geometry — skip it before doing any mesh work.
            if (!inScene) { outside++; continue; }

            if (pts.Count < 2) { skipped++; continue; }

            // Unlike grass, a water surface has to stay planar, so the whole
            // polygon gets one height. The median is used rather than the mean
            // because a ray that slips past the bank and hits a wall or a roof
            // would drag an average upwards.
            float waterY;
            if (!TryResolveWaterPlane(pts, hits, out waterY)) { noGround++; skipped++; continue; }
            waterY += heightOffset;

            bool built = way.isArea
                ? BuildAreaMesh(pts, waterY, way, parentAreas)
                : BuildRiverMesh(pts, waterY, way, parentRivers);

            if (built) created++;
            else       skipped++;
        }

        // Don't leave an empty "Rivers" object cluttering the hierarchy when the
        // extract contained no waterway lines.
        if (parentRivers.transform.childCount == 0)
            DestroyImmediate(parentRivers);

        EditorUtility.ClearProgressBar();
        string msg = created + " water features created, " + skipped + " skipped.";
        if (outside > 0)
            msg += "\n\n" + outside + " skipped as outside the model footprint.";
        if (noGround > 0)
            msg += "\n\n" + noGround + " skipped because no Cesium tile was loaded " +
                   "beneath them. Fly the Scene view over those areas so the tiles " +
                   "stream in, then run the import again.";
        Debug.Log("[OSMWater] " + msg);
        EditorUtility.DisplayDialog("OSM Water Importer", msg, "OK");
    }

    /// <summary>
    /// Finds or creates a container and re-seats it under the georeference with
    /// an identity transform, clearing anything left over from a previous run so
    /// re-importing doesn't stack a second copy of every feature on the first.
    /// </summary>
    GameObject PrepareParent(string name)
    {
        GameObject parent = GameObject.Find(name);
        if (parent == null) parent = new GameObject(name);

        parent.transform.SetParent(georeference.transform, false);
        parent.transform.localPosition = Vector3.zero;
        parent.transform.localRotation = Quaternion.identity;
        parent.transform.localScale    = Vector3.one;

        for (int c = parent.transform.childCount - 1; c >= 0; c--)
            DestroyImmediate(parent.transform.GetChild(c).gameObject);

        return parent;
    }

    /// <summary>
    /// Picks the single height for a water surface: the median of the vertices
    /// whose downward ray found ground. Returns false when nothing hit, which
    /// means the tiles under this feature have not streamed in.
    /// </summary>
    bool TryResolveWaterPlane(List<Vector3> pts, List<bool> hits, out float waterY)
    {
        waterY = 0f;

        List<float> ys = new List<float>(pts.Count);
        for (int k = 0; k < pts.Count; k++)
            if (!snapToTerrain || hits[k]) ys.Add(pts[k].y);

        if (ys.Count == 0) return false;

        ys.Sort();
        waterY = ys.Count % 2 == 1
            ? ys[ys.Count / 2]
            : (ys[ys.Count / 2 - 1] + ys[ys.Count / 2]) * 0.5f;
        return true;
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

    // ── Area mesh (same ear-clipping approach as green areas) ─────────────────

    bool BuildAreaMesh(List<Vector3> pts, float waterY, WaterWay way, GameObject parent)
    {
        if (pts.Count > 1 && Vector3.Distance(pts[0], pts[pts.Count - 1]) < 0.5f)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return false;

        List<Vector2> flat = new List<Vector2>(pts.Count);
        for (int k = 0; k < pts.Count; k++)
            flat.Add(new Vector2(pts[k].x, pts[k].z));

        if (SignedArea(flat) < 0f) flat.Reverse();

        int[] tris = EarClip(flat);
        if (tris == null || tris.Length < 3) return false;

        Vector3[] verts = new Vector3[flat.Count];
        Vector2[] uvs   = new Vector2[flat.Count];
        Vector3[] norms = new Vector3[flat.Count];
        for (int v = 0; v < flat.Count; v++)
        {
            // One height for every vertex — a lake surface is planar.
            verts[v] = new Vector3(flat[v].x, waterY, flat[v].y);
            uvs[v]   = new Vector2(flat[v].x / uvTileSize, flat[v].y / uvTileSize);
            norms[v] = Vector3.up;
        }

        Mesh mesh      = new Mesh();
        mesh.name      = string.IsNullOrEmpty(way.name) ? "water_area" : way.name;
        mesh.vertices  = verts;
        mesh.triangles = tris;
        mesh.uv        = uvs;
        mesh.normals   = norms;
        mesh.RecalculateBounds();

        string goName = string.IsNullOrEmpty(way.name)
            ? "WaterArea_" + parent.transform.childCount.ToString("D4")
            : way.name;

        GameObject go = new GameObject(goName);
        // worldPositionStays: false — keep the child at local (0,0,0) so the
        // vertex coordinates stay in georeference space.
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = waterMaterial;
        return true;
    }

    // ── River ribbon mesh ─────────────────────────────────────────────────────

    bool BuildRiverMesh(List<Vector3> pts, float waterY, WaterWay way, GameObject parent)
    {
        if (pts.Count < 2) return false;

        float halfW = GetHalfWidth(way.waterway);

        // Build ribbon: for each segment midpoint, extrude perpendicular by halfW
        List<Vector3> verts = new List<Vector3>();
        List<int>     tris  = new List<int>();
        List<Vector2> uvs   = new List<Vector2>();
        List<Vector3> norms = new List<Vector3>();

        float uvY = 0f;

        for (int i = 0; i < pts.Count; i++)
        {
            // Direction at this point
            Vector3 fwd;
            if (i == 0)
                fwd = (pts[1] - pts[0]);
            else if (i == pts.Count - 1)
                fwd = (pts[i] - pts[i - 1]);
            else
                fwd = (pts[i + 1] - pts[i - 1]);

            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x); // perpendicular in XZ

            Vector3 center = new Vector3(pts[i].x, waterY, pts[i].z);
            Vector3 left3  = center - right * halfW;
            Vector3 right3 = center + right * halfW;

            verts.Add(left3);
            verts.Add(right3);
            uvs.Add(new Vector2(0f, uvY));
            uvs.Add(new Vector2(1f, uvY));
            norms.Add(Vector3.up);
            norms.Add(Vector3.up);

            if (i > 0)
            {
                int b = (i - 1) * 2;
                // Left tri
                tris.Add(b);     tris.Add(b + 2); tris.Add(b + 1);
                // Right tri
                tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
            }

            if (i < pts.Count - 1)
                uvY += Vector3.Distance(pts[i], pts[i + 1]) / uvTileSize;
        }

        if (verts.Count < 4) return false;

        Mesh mesh      = new Mesh();
        mesh.name      = string.IsNullOrEmpty(way.name) ? way.waterway : way.name;
        mesh.vertices  = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.uv        = uvs.ToArray();
        mesh.normals   = norms.ToArray();
        mesh.RecalculateBounds();

        string goName = string.IsNullOrEmpty(way.name)
            ? way.waterway + "_" + parent.transform.childCount.ToString("D4")
            : way.name;

        GameObject go = new GameObject(goName);
        // worldPositionStays: false — keep the child at local (0,0,0) so the
        // vertex coordinates stay in georeference space.
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = waterMaterial;
        return true;
    }

    float GetHalfWidth(string waterway)
    {
        if (waterway == "river")  return riverWidth  * 0.5f;
        if (waterway == "canal")  return canalWidth  * 0.5f;
        if (waterway == "stream") return streamWidth * 0.5f;
        if (waterway == "drain")  return drainWidth  * 0.5f;
        return riverWidth * 0.5f;
    }

    // ── OSM Parser ────────────────────────────────────────────────────────────

    void ParseOSM(string path, Dictionary<long, WaterNode> nodes, List<WaterWay> ways)
    {
        XmlDocument doc = new XmlDocument();
        doc.Load(path);

        foreach (XmlNode n in doc.SelectNodes("//node"))
        {
            if (n.Attributes["id"] == null || n.Attributes["lat"] == null || n.Attributes["lon"] == null) continue;
            long id;
            if (!long.TryParse(n.Attributes["id"].Value, out id)) continue;
            double lat, lon;
            if (!double.TryParse(n.Attributes["lat"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat)) continue;
            if (!double.TryParse(n.Attributes["lon"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lon)) continue;
            WaterNode wn = new WaterNode(); wn.lat = lat; wn.lon = lon;
            nodes[id] = wn;
        }

        foreach (XmlNode w in doc.SelectNodes("//way"))
        {
            string natural  = null;
            string waterway = null;
            string landuse  = null;
            string name     = "";

            foreach (XmlNode tag in w.SelectNodes("tag"))
            {
                if (tag.Attributes["k"] == null || tag.Attributes["v"] == null) continue;
                string k = tag.Attributes["k"].Value;
                string v = tag.Attributes["v"].Value;
                if      (k == "natural")  natural  = v;
                else if (k == "waterway") waterway = v;
                else if (k == "landuse")  landuse  = v;
                else if (k == "name")     name     = v;
            }

            bool isArea = false;
            bool isLine = false;

            // Area water
            if (natural  == "water"     && incWaterNatural) isArea = true;
            if (waterway == "riverbank" && incRiverbank)    isArea = true;
            if (landuse  == "reservoir" && incReservoir)    isArea = true;

            // Line water
            if (waterway == "river"  && incRiver)  isLine = true;
            if (waterway == "canal"  && incCanal)  isLine = true;
            if (waterway == "stream" && incStream) isLine = true;
            if (waterway == "drain"  && incDrain)  isLine = true;

            if (!isArea && !isLine) continue;

            List<long> refs = new List<long>();
            foreach (XmlNode nd in w.SelectNodes("nd"))
            {
                if (nd.Attributes["ref"] == null) continue;
                long r; if (long.TryParse(nd.Attributes["ref"].Value, out r)) refs.Add(r);
            }

            int minPts = isArea ? 3 : 2;
            if (refs.Count < minPts) continue;

            WaterWay ww = new WaterWay();
            ww.name     = name;
            ww.waterway = waterway ?? "";
            ww.isArea   = isArea;
            ww.nodeRefs = refs;
            ways.Add(ww);
        }
    }

    // ── Ear-clipping triangulator (same as green area importer) ──────────────

    static int[] EarClip(List<Vector2> poly)
    {
        int n = poly.Count;
        if (n < 3) return null;
        List<int> tris = new List<int>();
        List<int> rem  = new List<int>(n);
        for (int i = 0; i < n; i++) rem.Add(i);
        int safety = n * n + 10, iter = 0;
        while (rem.Count > 3 && iter < safety)
        {
            iter++;
            bool found = false;
            for (int i = 0; i < rem.Count; i++)
            {
                int prev = rem[(i - 1 + rem.Count) % rem.Count];
                int curr = rem[i];
                int next = rem[(i + 1) % rem.Count];
                Vector2 a = poly[prev], b = poly[curr], c = poly[next];
                if (Cross(a, b, c) <= 0f) continue;
                bool ear = true;
                for (int j = 0; j < rem.Count; j++)
                {
                    int idx = rem[j];
                    if (idx == prev || idx == curr || idx == next) continue;
                    if (InTri(poly[idx], a, b, c)) { ear = false; break; }
                }
                if (!ear) continue;
                tris.Add(prev); tris.Add(curr); tris.Add(next);
                rem.RemoveAt(i); found = true; break;
            }
            if (!found) break;
        }
        if (rem.Count == 3) { tris.Add(rem[0]); tris.Add(rem[1]); tris.Add(rem[2]); }
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
        float d1 = Cross(a, b, pt), d2 = Cross(b, c, pt), d3 = Cross(c, a, pt);
        return !((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f));
    }

    // ── Coordinate conversion ─────────────────────────────────────────────────

    Vector3 LatLonToUnity(double lat, double lon)
    {
        // 200 m above the ellipsoid is only a starting hint — it puts the point
        // safely above the campus (which sits near 95 m) so the downward ray in
        // TrySnapLocalToGround has somewhere to fall from. The real height comes
        // from that raycast. With snapping disabled this value survives into the
        // mesh, which is why the water used to float ~100 m up.
        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, 200.0));
        double3 u = georeference
            .TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)u.x, (float)u.y, (float)u.z);
    }

    // ── Water material ────────────────────────────────────────────────────────

    static Material GetOrCreateWaterMaterial()
    {
        const string matPath = "Assets/Materials/WaterMaterial.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        Material mat = new Material(shader);
        mat.name = "WaterMaterial";

        // Deep blue water colour
        mat.SetColor("_BaseColor", new Color(0.10f, 0.35f, 0.65f, 1f));
        mat.SetColor("_Color",     new Color(0.10f, 0.35f, 0.65f, 1f));
        mat.SetFloat("_Smoothness", 0.85f);   // shiny — looks like water
        mat.SetFloat("_Metallic",   0f);
        mat.SetFloat("_Cull",       0f);       // double-sided

        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[OSMWater] Created WaterMaterial at " + matPath);
        return mat;
    }

    // ── Data classes ──────────────────────────────────────────────────────────

    class WaterNode { public double lat; public double lon; }

    class WaterWay
    {
        public string     name;
        public string     waterway;
        public bool       isArea;
        public List<long> nodeRefs;
    }
}
