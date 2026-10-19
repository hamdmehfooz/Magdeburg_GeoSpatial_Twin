using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;
using UnityEditor;
using CesiumForUnity;
using Unity.Mathematics;
using EasyRoads3Dv3;

/// <summary>
/// Editor window that reads an .osm file, converts road node coordinates
/// to Unity world space via CesiumGeoreference, and creates EasyRoads3D
/// road objects for each OSM highway way.
///
/// How to use:
///   1. Download your area from https://overpass-turbo.eu  (see query below)
///   2. Save the file as  Assets/GTFS/map.osm
///   3. Open  Tools → OSM Road Importer
///   4. Assign the CesiumGeoreference from the scene hierarchy
///   5. Choose which road types to include
///   6. Click  "Import Roads"
///
/// Overpass query for Magdeburg roads only:
///   [out:xml][bbox:51.99,11.50,52.22,11.75];
///   (way["highway"~"motorway|trunk|primary|secondary|tertiary|residential|service"];);
///   (._;>;);out body;
/// </summary>
public class OSMRoadImporter : EditorWindow
{
    // ── file & scene refs ────────────────────────────────────────────────────
    private string osmFilePath = "Assets/GTFS/map.osm";
    private CesiumGeoreference georeference;

    // ── road type toggles ────────────────────────────────────────────────────
    private bool includeMotorway    = true;
    private bool includePrimary     = true;
    private bool includeSecondary   = true;
    private bool includeTertiary    = true;
    private bool includeResidential = true;
    private bool includeService     = false;
    private bool includeFootway     = false;

    // ── import settings ──────────────────────────────────────────────────────
    private float roadHeightOffset  = 0.15f;  // metres above terrain
    private int   maxRoads          = 500;    // safety cap — increase if needed
    private float minSegmentLength  = 1.0f;   // skip duplicate/near-duplicate nodes

    // ── Magdeburg bounding box ───────────────────────────────────────────────
    private double latMin = 51.99, latMax = 52.22;
    private double lonMin = 11.50, lonMax = 11.75;

    // ── scroll position ──────────────────────────────────────────────────────
    private Vector2 scroll;

    // ─────────────────────────────────────────────────────────────────────────

    [MenuItem("Tools/OSM Road Importer")]
    public static void ShowWindow()
    {
        var w = GetWindow<OSMRoadImporter>("OSM Road Importer");
        w.minSize = new Vector2(380, 500);
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        // ── Header ────────────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        GUIStyle header = new GUIStyle(EditorStyles.boldLabel)
            { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        EditorGUILayout.LabelField("OSM Road Importer", header);
        EditorGUILayout.Space(4);
        DrawSeparator();

        // ── OSM file ──────────────────────────────────────────────────────────
        EditorGUILayout.LabelField("OSM File", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        osmFilePath = EditorGUILayout.TextField(osmFilePath);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            string picked = EditorUtility.OpenFilePanel(
                "Select .osm file", Application.dataPath, "osm");
            if (!string.IsNullOrEmpty(picked))
                osmFilePath = picked;
        }
        EditorGUILayout.EndHorizontal();

        bool fileExists = File.Exists(osmFilePath);
        if (!fileExists)
        {
            EditorGUILayout.HelpBox(
                "File not found. Download your area from overpass-turbo.eu " +
                "and save it as  Assets/GTFS/map.osm\n\n" +
                "Overpass query (paste at overpass-turbo.eu):\n" +
                "[out:xml][bbox:51.99,11.50,52.22,11.75];\n" +
                "(way[\"highway\"~\"motorway|trunk|primary|secondary|tertiary|residential|service\"];);\n" +
                "(._;>;);out body;",
                MessageType.Warning);
        }

        // ── Cesium GeoReference ───────────────────────────────────────────────
        EditorGUILayout.Space(6);
        DrawSeparator();
        EditorGUILayout.LabelField("Scene Reference", EditorStyles.boldLabel);
        georeference = (CesiumGeoreference)EditorGUILayout.ObjectField(
            "Cesium GeoReference", georeference,
            typeof(CesiumGeoreference), true);

        if (georeference == null)
        {
            EditorGUILayout.HelpBox(
                "Drag the CesiumGeoreference object from the scene hierarchy.",
                MessageType.Warning);
        }

        // ── Road type filters ─────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        DrawSeparator();
        EditorGUILayout.LabelField("Highway Types to Import", EditorStyles.boldLabel);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        includeMotorway    = EditorGUILayout.Toggle("Motorway / Trunk        (~12 m)", includeMotorway);
        includePrimary     = EditorGUILayout.Toggle("Primary                  (~9 m)",  includePrimary);
        includeSecondary   = EditorGUILayout.Toggle("Secondary                (~7 m)",  includeSecondary);
        includeTertiary    = EditorGUILayout.Toggle("Tertiary / Unclassified  (~5 m)",  includeTertiary);
        includeResidential = EditorGUILayout.Toggle("Residential / Living St  (~4 m)",  includeResidential);
        includeService     = EditorGUILayout.Toggle("Service                  (~3 m)",  includeService);
        includeFootway     = EditorGUILayout.Toggle("Footway / Path           (~2 m)",  includeFootway);
        EditorGUILayout.EndVertical();

        // ── Settings ──────────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        DrawSeparator();
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        roadHeightOffset = EditorGUILayout.Slider(
            "Height Offset (m)", roadHeightOffset, 0f, 2f);
        maxRoads = EditorGUILayout.IntSlider(
            "Max Roads", maxRoads, 10, 2000);

        EditorGUILayout.HelpBox(
            "Start with Max Roads = 100 to test, then increase. " +
            "Large imports can take several minutes.",
            MessageType.Info);

        // ── Import button ─────────────────────────────────────────────────────
        EditorGUILayout.Space(8);
        GUI.enabled = fileExists && georeference != null;
        if (GUILayout.Button("Import Roads", GUILayout.Height(36)))
            ImportRoads();
        GUI.enabled = true;

        EditorGUILayout.Space(6);
        EditorGUILayout.EndScrollView();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core import logic
    // ─────────────────────────────────────────────────────────────────────────

    void ImportRoads()
    {
        // ── Parse .osm ────────────────────────────────────────────────────────
        EditorUtility.DisplayProgressBar("OSM Road Importer", "Parsing OSM file…", 0f);

        var nodes = new Dictionary<long, (double lat, double lon)>();
        var ways  = new List<OsmWay>();

        try
        {
            ParseOSM(osmFilePath, nodes, ways);
        }
        catch (System.Exception ex)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Parse Error",
                $"Failed to parse OSM file:\n{ex.Message}", "OK");
            Debug.LogError($"[OSMRoadImporter] {ex}");
            return;
        }

        Debug.Log($"[OSMRoadImporter] Parsed {nodes.Count} nodes, " +
                  $"{ways.Count} matching ways.");

        // ── Guard: no nodes means the query is missing (._;>;); ───────────────
        if (nodes.Count == 0)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("No Node Coordinates in File",
                "The file contains road ways but NO node coordinates.\n\n" +
                "Your Overpass query is missing the node-expansion clause.\n\n" +
                "Re-download using this exact query at overpass-turbo.eu:\n\n" +
                "[out:xml][bbox:51.99,11.50,52.22,11.75];\n" +
                "(way[\"highway\"~\"motorway|trunk|primary|secondary|tertiary|residential|service\"];);\n" +
                "(._;>;);\n" +
                "out body;",
                "OK");
            return;
        }

        // ── Get EasyRoads3D road network ──────────────────────────────────────
        ERRoadNetwork roadNetwork = new ERRoadNetwork();

        // GetRoadTypes() returns null/empty in the FREE version — road types
        // are a Pro-only feature.  We fall back to default(ERRoadType) so
        // EasyRoads3D uses its built-in defaults (width set per road below).
        ERRoadType[] roadTypes  = roadNetwork.GetRoadTypes();
        bool         hasTypes   = roadTypes != null && roadTypes.Length > 0;

        Debug.Log($"[OSMRoadImporter] Road types found: {roadTypes?.Length ?? 0}" +
                  (!hasTypes ? " — free version, using default ERRoadType" : ""));

        // ── Build roads ───────────────────────────────────────────────────────
        int created = 0;
        int skipped = 0;
        int toProcess = Mathf.Min(ways.Count, maxRoads);

        for (int wi = 0; wi < toProcess; wi++)
        {
            OsmWay way = ways[wi];

            EditorUtility.DisplayProgressBar(
                "OSM Road Importer",
                $"Road {wi + 1}/{toProcess}  —  {way.name}",
                (float)wi / toProcess);

            // Convert nodes to Unity world positions
            var waypoints = new List<Vector3>();

            foreach (long nodeId in way.nodeRefs)
            {
                if (!nodes.TryGetValue(nodeId, out var coord)) continue;

                // Skip nodes outside the city bounding box
                if (coord.lat < latMin || coord.lat > latMax ||
                    coord.lon < lonMin || coord.lon > lonMax)
                    continue;

                Vector3 pos = LatLonToUnity(coord.lat, coord.lon);
                pos.y += roadHeightOffset;
                waypoints.Add(pos);
            }

            if (waypoints.Count < 2) { skipped++; continue; }

            // Remove near-duplicate consecutive points
            for (int i = waypoints.Count - 1; i > 0; i--)
            {
                if (Vector3.Distance(waypoints[i], waypoints[i - 1]) < minSegmentLength)
                    waypoints.RemoveAt(i);
            }

            if (waypoints.Count < 2) { skipped++; continue; }

            float  width    = WidthForHighway(way.highway);
            string roadName = string.IsNullOrEmpty(way.name)
                ? $"{way.highway}_{created:D4}"
                : $"{way.name}_{created:D4}";

            // Pro: pick the best matching road type.
            // Free: default(ERRoadType) — EasyRoads3D uses its built-in defaults.
            ERRoadType roadType = hasTypes
                                   ? PickRoadType(roadTypes, way.highway)
                                   : default(ERRoadType);

            try
            {
                ERRoad road = roadNetwork.CreateRoad(
                    roadName, roadType, waypoints.ToArray());

                if (road != null)
                {
                    road.SetWidth(width);
                    created++;
                }
                else
                {
                    Debug.LogWarning(
                        $"[OSMRoadImporter] CreateRoad returned null for '{roadName}'");
                    skipped++;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[OSMRoadImporter] Skipped '{roadName}': {ex.Message}");
                skipped++;
            }
        }

        // ── Build network ─────────────────────────────────────────────────────
        EditorUtility.DisplayProgressBar("OSM Road Importer", "Building road network…", 0.99f);
        roadNetwork.BuildRoadNetwork();

        EditorUtility.ClearProgressBar();

        string msg = $"Import complete!\n\n" +
                     $"✓  {created} roads created\n" +
                     $"✗  {skipped} skipped (too short / outside bbox)\n" +
                     $"\nTotal ways in file: {ways.Count}" +
                     (ways.Count > maxRoads
                         ? $"\n⚠ Only first {maxRoads} processed (raise Max Roads to get more)"
                         : "");

        Debug.Log($"[OSMRoadImporter] {msg.Replace("\n", "  |  ")}");
        EditorUtility.DisplayDialog("OSM Road Importer", msg, "OK");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OSM XML parser
    // ─────────────────────────────────────────────────────────────────────────

    void ParseOSM(string path,
                  Dictionary<long, (double lat, double lon)> nodes,
                  List<OsmWay> ways)
    {
        var doc = new XmlDocument();
        doc.Load(path);

        // ── Nodes ─────────────────────────────────────────────────────────────
        foreach (XmlNode n in doc.SelectNodes("//node"))
        {
            if (n.Attributes["id"]  == null ||
                n.Attributes["lat"] == null ||
                n.Attributes["lon"] == null) continue;

            if (!long.TryParse(n.Attributes["id"].Value, out long id)) continue;

            if (!double.TryParse(n.Attributes["lat"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double lat)) continue;

            if (!double.TryParse(n.Attributes["lon"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double lon)) continue;

            nodes[id] = (lat, lon);
        }

        // ── Ways ──────────────────────────────────────────────────────────────
        foreach (XmlNode w in doc.SelectNodes("//way"))
        {
            string highway = null;
            string name    = "";

            foreach (XmlNode tag in w.SelectNodes("tag"))
            {
                if (tag.Attributes["k"] == null || tag.Attributes["v"] == null)
                    continue;
                string k = tag.Attributes["k"].Value;
                string v = tag.Attributes["v"].Value;

                if (k == "highway") highway = v;
                if (k == "name")    name    = v;
            }

            if (highway == null || !IsIncluded(highway)) continue;

            var nodeRefs = new List<long>();
            foreach (XmlNode nd in w.SelectNodes("nd"))
            {
                if (nd.Attributes["ref"] == null) continue;
                if (long.TryParse(nd.Attributes["ref"].Value, out long refId))
                    nodeRefs.Add(refId);
            }

            if (nodeRefs.Count >= 2)
                ways.Add(new OsmWay { name = name, highway = highway, nodeRefs = nodeRefs });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Coordinate conversion  (same logic as TransitSimulationManager)
    // ─────────────────────────────────────────────────────────────────────────

    Vector3 LatLonToUnity(double lat, double lon)
    {
        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, 0));

        double3 unityPos = georeference
            .TransformEarthCenteredEarthFixedPositionToUnity(ecef);

        return new Vector3(
            (float)unityPos.x,
            (float)unityPos.y,
            (float)unityPos.z);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Road type / width helpers
    // ─────────────────────────────────────────────────────────────────────────

    bool IsIncluded(string highway)
    {
        switch (highway)
        {
            case "motorway":
            case "trunk":
            case "motorway_link":
            case "trunk_link":
                return includeMotorway;

            case "primary":
            case "primary_link":
                return includePrimary;

            case "secondary":
            case "secondary_link":
                return includeSecondary;

            case "tertiary":
            case "tertiary_link":
            case "unclassified":
                return includeTertiary;

            case "residential":
            case "living_street":
                return includeResidential;

            case "service":
                return includeService;

            case "footway":
            case "path":
            case "cycleway":
            case "pedestrian":
                return includeFootway;

            default:
                return false;
        }
    }

    /// <summary>
    /// Picks the best matching EasyRoads3D road type by name.
    /// Falls back to the first available type if no match found.
    /// Road type names in this scene: "Default Road" (5 m wide).
    /// </summary>
    ERRoadType PickRoadType(ERRoadType[] types, string highway)
    {
        // Try to find a road type whose name contains the highway category
        string category = HighwayCategory(highway);
        foreach (var t in types)
            if (t.roadTypeName != null &&
                t.roadTypeName.ToLower().Contains(category))
                return t;

        // Fallback: first road type in the list
        return types[0];
    }

    string HighwayCategory(string highway)
    {
        switch (highway)
        {
            case "motorway": case "trunk":
            case "motorway_link": case "trunk_link":
                return "motorway";
            case "primary": case "primary_link":
                return "primary";
            case "secondary": case "secondary_link":
                return "secondary";
            case "tertiary": case "tertiary_link": case "unclassified":
                return "tertiary";
            default:
                return "road";
        }
    }

    /// <summary>Road width in metres per OSM highway type.</summary>
    float WidthForHighway(string highway)
    {
        switch (highway)
        {
            case "motorway":
            case "trunk":
            case "motorway_link":
            case "trunk_link":
                return 12f;

            case "primary":
            case "primary_link":
                return 9f;

            case "secondary":
            case "secondary_link":
                return 7f;

            case "tertiary":
            case "tertiary_link":
            case "unclassified":
                return 5f;

            case "residential":
            case "living_street":
                return 4f;

            case "service":
                return 3f;

            case "footway":
            case "path":
            case "cycleway":
            case "pedestrian":
                return 2f;

            default:
                return 5f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI helpers
    // ─────────────────────────────────────────────────────────────────────────

    static void DrawSeparator()
    {
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.4f, 0.4f, 0.4f, 1f));
        EditorGUILayout.Space(4);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Data classes
    // ─────────────────────────────────────────────────────────────────────────

    class OsmWay
    {
        public string      name;
        public string      highway;
        public List<long>  nodeRefs;
    }
}
