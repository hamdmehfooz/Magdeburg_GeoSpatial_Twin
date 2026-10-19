using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using CesiumForUnity;

public class WeatherDataDisplay : MonoBehaviour
{
    [Header("Surface Snap")]
    [Tooltip("Initial height above ground before raycast.")]
    public double spawnHeightMeters = 200.0;

    [Tooltip("Raycast distance downward (meters).")]
    public float raycastDistance = 1000f;

    [Tooltip("Start ray this much above the cube.")]
    public float rayStartAbove = 100f;

    [Tooltip("Small offset above surface.")]
    public float surfaceOffset = 0.2f;

    [Tooltip("How many times to retry snapping (tiles may load late).")]
    public int snapRetries = 6;

    [Tooltip("Delay between retries.")]
    public float snapRetryDelay = 0.5f;

    [Header("API")]
    [Tooltip("Endpoint that returns the JSON array you posted.")]
    public string apiUrl = "https://YOUR_API_URL_HERE";
    [Tooltip("How often to refresh values (seconds).")]
    public float refreshInterval = 5f;

    [Header("Prefabs")]
    public GameObject cubePrefab;     // must have CesiumGlobeAnchor (or script will add it)
    public TMP_Text labelPrefab;      // world-space TMP_Text prefab (recommended)

    [Header("Placement")]
    [Tooltip("If API doesn't provide height, use this height in meters.")]
    public double defaultHeightMeters = 2.0;
    [Tooltip("Offset label above cube in Unity units.")]
    public float labelYOffset = 1.2f;

    [Header("UI")]
    [Tooltip("Make labels face the main camera.")]
    public bool billboardLabels = true;

    // Keep spawned sensors by id so we update instead of respawning
    private readonly Dictionary<string, SensorVisual> _spawned = new Dictionary<string, SensorVisual>();

    [Serializable]
    private class Wrapper { public WeatherItem[] items; }

    [Serializable]
    private class WeatherItem
    {
        public string id;
        public string type;
        public ValueField humidity;
        public ValueField temperature;
        public LocationField location;
        // other fields may exist; JsonUtility ignores unknown fields
    }

    [Serializable]
    private class ValueField
    {
        public string type;
        public string value; // may come as number or string -> we parse safely
    }

    [Serializable]
    private class LocationField
    {
        public string type;
        public string value; // "lat, lon"
    }

    private class SensorVisual
    {
        public GameObject go;
        public CesiumGlobeAnchor anchor;
        public TMP_Text label;
        public bool snapped;
    }

    private void Start()
    {
        if (cubePrefab == null)
        {
            Debug.LogError("Assign cubePrefab in inspector.");
            return;
        }

        StartCoroutine(PollLoop());
    }

    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return FetchAndUpdate();
            yield return new WaitForSeconds(refreshInterval);
        }
    }

    private IEnumerator FetchAndUpdate()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(apiUrl))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"API request failed: {req.error}");
                yield break;
            }

            string json = req.downloadHandler.text;

            // JsonUtility cannot parse a root array -> wrap it
            string wrapped = "{\"items\":" + json + "}";
            Wrapper data;
            try
            {
                data = JsonUtility.FromJson<Wrapper>(wrapped);
            }
            catch (Exception e)
            {
                Debug.LogError("JSON parse failed: " + e.Message);
                yield break;
            }

            if (data?.items == null) yield break;

            foreach (var item in data.items)
            {
                if (item?.location?.value == null) continue;

                // Parse Lat/Lon
                if (!TryParseLatLon(item.location.value, out double lat, out double lon))
                    continue;

                // Parse values
                float temp = ParseFloat(item.temperature?.value);
                float hum = ParseFloat(item.humidity?.value);

                // Create or update visual
                UpsertSensor(item.id, lat, lon, defaultHeightMeters, temp, hum);
            }
        }
    }

    private void UpsertSensor(string id, double lat, double lon, double height, float temp, float hum)
    {
        if (!_spawned.TryGetValue(id, out SensorVisual visual) || visual.go == null)
        {
            // Spawn new
            var go = Instantiate(cubePrefab, Vector3.zero, Quaternion.identity, transform);
            go.name = id;

            var anchor = go.GetComponent<CesiumGlobeAnchor>();
            if (anchor == null) anchor = go.AddComponent<CesiumGlobeAnchor>();

            // Optional: keep upright with the globe
            anchor.adjustOrientationForGlobeWhenMoving = true;

            TMP_Text label = null;
            if (labelPrefab != null)
            {
                label = Instantiate(labelPrefab, go.transform);
                label.transform.localPosition = new Vector3(0f, labelYOffset, 0f);
            }

            visual = new SensorVisual { go = go, anchor = anchor, label = label };
            _spawned[id] = visual;
        }

        visual.anchor.latitude = lat;
        visual.anchor.longitude = lon;

        // First time: place high and snap
        if (!visual.snapped)
        {
            visual.anchor.height = spawnHeightMeters;
            StartCoroutine(SnapToSurface(visual));
        }

        // Update label text
        if (visual.label != null)
        {
            visual.label.text =
                $"{id}\n" +
                $"T: {temp:0.##} °C\n" +
                $"H: {hum:0.#} %";
        }
    }

    private void LateUpdate()
    {
        if (!billboardLabels) return;

        var cam = Camera.main;
        if (cam == null) return;

        foreach (var kv in _spawned)
        {
            var label = kv.Value.label;
            if (label == null) continue;

            // Face camera
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
        }
    }

    private IEnumerator SnapToSurface(SensorVisual visual)
    {
        // Wait for Cesium to position the object from lat/lon/height
        yield return null;
        yield return null;

        for (int i = 0; i < snapRetries; i++)
        {
            if (visual == null || visual.go == null) yield break;

            Vector3 start = visual.go.transform.position + Vector3.up * rayStartAbove;

            if (Physics.Raycast(start, Vector3.down, out RaycastHit hit, raycastDistance, ~0, QueryTriggerInteraction.Ignore))
            {
                // Move to surface in Unity space
                Vector3 snappedPos = hit.point + hit.normal * surfaceOffset;

                // Allow anchor to update from transform
                SetDetectTransformChanges(visual.anchor, true);

                // Apply snapped position
                visual.go.transform.position = snappedPos;

                // Give Cesium time to sync and compute a consistent height
                yield return null;
                yield return null;

                // Lock it again
                SetDetectTransformChanges(visual.anchor, false);

                // Now your anchor.height should reflect the snapped position (more stable)
                visual.snapped = true;
                yield break;
            }

            yield return new WaitForSeconds(snapRetryDelay);
        }

        Debug.LogWarning("Surface snap failed for " + visual.go.name);
    }


    private void SetDetectTransformChanges(CesiumGlobeAnchor anchor, bool enabled)
    {
        if (anchor == null) return;

        var prop = typeof(CesiumGlobeAnchor).GetProperty("detectTransformChanges");
        if (prop != null)
            prop.SetValue(anchor, enabled);
    }

    private static bool TryParseLatLon(string s, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        var parts = s.Split(',');
        if (parts.Length < 2) return false;

        bool okLat = double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out lat);
        bool okLon = double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out lon);
        return okLat && okLon;
    }

    private static float ParseFloat(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return float.NaN;
        float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out float f);
        return f;
    }
}
