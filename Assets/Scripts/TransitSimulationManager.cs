using CesiumForUnity;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Mathematics;
using UnityEngine;

using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;

/// <summary>
/// Loads harbour track data + GTFS stops, then spawns
/// bus and tram prefabs that drive along the tracks.
///
/// Setup:
///   1. Attach this script to your CesiumGeoreference GameObject.
///   2. Drag the CesiumGeoreference component into the `georeference` field.
///   3. Assign busPrefab and tramPrefab in Inspector.
///   4. Put your GTFS files in  <Application.dataPath>/GTFS/
///      Required: harbour_tracks_data.txt  stops.txt
/// </summary>
public class TransitSimulationManager : MonoBehaviour
{
    [Header("Cesium")]
    public CesiumGeoreference georeference;

    [Header("Spawn Settings")]
    [Tooltip("Max number of bus vehicles to spawn")]
    public int maxBuses = 6;

    [Tooltip("Max number of tram vehicles to spawn")]
    public int maxTrams = 6;

    [Header("Vehicle Prefabs")]
    public GameObject busPrefab;
    public GameObject tramPrefab;

    [Header("Stop Detection")]
    [Tooltip("World-space radius (metres) within which a track point counts as 'at a stop'")]
    public float stopDetectionRadius = 15f;

    public List<Track> tracks = new();
    public List<Stop> stops = new();

    // Magdeburg bounding box
    const double LAT_MIN = 51.99, LAT_MAX = 52.22;
    const double LON_MIN = 11.50, LON_MAX = 11.75;

    string basePath;

    [Header("Startup")]
    [Tooltip("Seconds to wait before loading data, so Cesium terrain tiles have time to stream in.")]
    public float startupDelay = 8f;

    [Header("Ground Alignment")]
    [Tooltip("Vertical offset above the ground")]
    public float groundOffset = 1.5f;

    [Tooltip("Cast a ray downward to snap onto the Cesium terrain mesh.")]
    public bool snapToTerrain = true;

    [Tooltip("Height above the ellipsoid to start the raycast from")]
    public float raycastStartHeight = 1000f;

    void Start()
    {
        StartCoroutine(DelayedInit());
    }

    IEnumerator DelayedInit()
    {
        yield return new WaitForSeconds(startupDelay);

        basePath = Application.dataPath + "/GTFS/";

        LoadHarbourTracks();
        LoadStops();
        FilterStopsToMagdeburg();
        SpawnVehicles();
    }

    string FindFile(string keyword)
    {
        if (!Directory.Exists(basePath))
        {
            Debug.LogError($"GTFS folder not found: {basePath}");
            return null;
        }

        foreach (var f in Directory.GetFiles(basePath))
        {
            if (Path.GetFileName(f).ToLower().Contains(keyword))
                return f;
        }

        return null;
    }

    static string Clean(string s)
    {
        return s.Replace("\"", "").Trim();
    }

    void LoadHarbourTracks()
    {
        string path = FindFile("harbour_tracks");

        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError("harbour_tracks file not found!");
            return;
        }

        string content = File.ReadAllText(path);

        tracks = new List<Track>();

        var trackBlocks = Regex.Split(content, @"\{\s*""id""\s*:");

        foreach (var block in trackBlocks.Skip(1))
        {
            string vehicleType = "bus";

            var typeMatch = Regex.Match(block, @"""type""\s*:\s*""(\w+)""");

            if (typeMatch.Success)
                vehicleType = typeMatch.Groups[1].Value.ToLower();

            if (vehicleType == "train")
                continue;

            long trackId = 0;

            var idMatch = Regex.Match(block, @"^\s*(\d+)");

            if (idMatch.Success)
                long.TryParse(idMatch.Groups[1].Value, out trackId);

            var numMatches = Regex.Matches(block, @"-?\d+\.\d+");

            var positions = new List<List<double>>();
            var buf = new List<double>();

            foreach (Match m in numMatches)
            {
                if (double.TryParse(
                    m.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v))
                {
                    buf.Add(v);

                    if (buf.Count == 2)
                    {
                        double lat = buf[0];
                        double lon = buf[1];

                        if (lat >= LAT_MIN && lat <= LAT_MAX &&
                            lon >= LON_MIN && lon <= LON_MAX)
                        {
                            positions.Add(new List<double> { lat, lon });
                        }

                        buf.Clear();
                    }
                }
            }

            if (positions.Count > 5)
            {
                tracks.Add(new Track
                {
                    id = trackId,
                    type = vehicleType,
                    positions = positions
                });
            }
        }

        Debug.Log(
            $"Tracks loaded: {tracks.Count} " +
            $"(bus:{tracks.Count(t => t.type == "bus")} " +
            $"tram:{tracks.Count(t => t.type == "tram")})");
    }

    void LoadStops()
    {
        string path = FindFile("stops");

        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError("stops file not found!");
            return;
        }

        stops = new List<Stop>();

        var lines = File.ReadAllLines(path);

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsv(lines[i]);

            if (cols.Length < 6)
                continue;

            if (!double.TryParse(
                Clean(cols[4]),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double lat))
                continue;

            if (!double.TryParse(
                Clean(cols[5]),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double lon))
                continue;

            stops.Add(new Stop
            {
                id = Clean(cols[0]),
                name = Clean(cols[2]),
                lat = lat,
                lon = lon
            });
        }

        Debug.Log($"Stops loaded: {stops.Count}");
    }

    static string[] SplitCsv(string line)
    {
        var result = new List<string>();

        bool inQ = false;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQ = !inQ;
                continue;
            }

            if (line[i] == ',' && !inQ)
            {
                result.Add(line.Substring(start, i - start));
                start = i + 1;
            }
        }

        result.Add(line.Substring(start));

        return result.ToArray();
    }

    void FilterStopsToMagdeburg()
    {
        int before = stops.Count;

        stops = stops.Where(s =>
            s.lat >= LAT_MIN && s.lat <= LAT_MAX &&
            s.lon >= LON_MIN && s.lon <= LON_MAX)
            .ToList();

        Debug.Log($"Stops filtered: {before} → {stops.Count}");
    }

    List<Stop> GetStopsAlongTrack(Track track)
    {
        var result = new List<Stop>();

        // Convert track waypoints to Unity space once (reused for all stops).
        // Use lat/lon only — skip the terrain raycast here by passing heightHint=0
        // so we do fast ellipsoid math only; VehicleMover caches the real
        // snapped positions later during Init().
        var trackWorld = track.positions
            .Select(p => LatLonToUnity(p[0], p[1]))
            .ToList();

        foreach (var stop in stops)
        {
            Vector3 sw = LatLonToUnity(stop.lat, stop.lon);

            foreach (var tw in trackWorld)
            {
                // XZ-only: waypoint Y may have been baked before tiles loaded.
                if (XZDistance(sw, tw) < stopDetectionRadius)
                {
                    result.Add(stop);
                    break;
                }
            }
        }

        return result;
    }

    public static float XZDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void SpawnVehicles()
    {
        var busTracks = tracks
            .Where(t => t.type == "bus")
            .Take(maxBuses)
            .ToList();

        var tramTracks = tracks
            .Where(t => t.type == "tram")
            .Take(maxTrams)
            .ToList();

        SpawnGroup(busTracks, false);
        SpawnGroup(tramTracks, true);

        // Hide the original scene-placed prefab instances now that all
        // vehicle clones have been spawned from them.
        if (tramPrefab != null) tramPrefab.SetActive(false);
        if (busPrefab  != null) busPrefab.SetActive(false);

        Debug.Log(
            $"Vehicles spawned: {busTracks.Count} buses, {tramTracks.Count} trams");
    }

    void SpawnGroup(List<Track> group, bool isTram)
    {
        for (int i = 0; i < group.Count; i++)
        {
            var track = group[i];

            if (track.positions == null || track.positions.Count < 2)
                continue;

            // Select correct prefab
            GameObject prefab = isTram ? tramPrefab : busPrefab;

            if (prefab == null)
            {
                Debug.LogError(
                    isTram
                    ? "Tram Prefab not assigned!"
                    : "Bus Prefab not assigned!");

                continue;
            }

            // Spawn prefab instead of cube
            GameObject go = Instantiate(prefab);

            go.name = isTram
                ? $"Tram_{i}"
                : $"Bus_{i}";

            // Remove colliders if present
            Collider[] cols = go.GetComponentsInChildren<Collider>();

            foreach (var c in cols)
                Destroy(c);

            // Keep existing movement system
            var mover = go.GetComponent<VehicleMover>();

            if (mover == null)
                mover = go.AddComponent<VehicleMover>();

            mover.Init(
                track,
                GetStopsAlongTrack(track),
                this,
                isTram
            );
        }
    }

    public Vector3 LatLonToUnity(
        double lat,
        double lon,
        double heightHint = 200.0)
    {
        // WGS84 -> ECEF
        double3 ecef =
            CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, heightHint));

        // ECEF -> Unity
        double3 unityPos =
            georeference
            .TransformEarthCenteredEarthFixedPositionToUnity(ecef);

        Vector3 pos = new Vector3(
            (float)unityPos.x,
            (float)unityPos.y,
            (float)unityPos.z);

        if (!snapToTerrain)
            return pos + Vector3.up * groundOffset;

        // Raycast downward onto terrain
        Ray ray = new Ray(
            pos + Vector3.up * raycastStartHeight,
            Vector3.down);

        if (Physics.Raycast(
            ray,
            out RaycastHit hit,
            raycastStartHeight * 2f,
            ~0,
            QueryTriggerInteraction.Ignore))
        {
            return hit.point + Vector3.up * groundOffset;
        }

        // fallback
        return pos + Vector3.up * groundOffset;
    }
}