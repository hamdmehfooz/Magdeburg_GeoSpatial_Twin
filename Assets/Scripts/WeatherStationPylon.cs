using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using CesiumForUnity;
using Unity.Mathematics;

/// <summary>
/// Fetches weather data from the Orion NGSI-v2 API, spawns a Pylon with a
/// world-space display panel at each station's geo:point, and refreshes live.
///
/// Setup:
///   1. Attach this script to any GameObject in the scene.
///   2. Assign pylonPrefab  → drag  Assets/External_Models/Pylon.obj
///   3. Assign georeference → drag  CesiumGeoReference from Hierarchy
///   4. Hit Play — pylons appear at the real-world station coordinates.
/// </summary>
public class WeatherStationPylon : MonoBehaviour
{
    [Header("API")]
    public string apiUrl = "https://imiq-public.et.uni-magdeburg.de/api/orion/entities?type=Weather&limit=1000";
    public float  refreshInterval = 30f;

    [Header("Scene References")]
    public GameObject      pylonPrefab;
    public CesiumGeoreference georeference;

    [Header("Pylon Settings")]
    [Tooltip("Height above ellipsoid to spawn before terrain snap (metres)")]
    public double spawnHeight   = 200.0;
    public float  groundOffset  = 0.1f;
    public float  screenHeight  = 4.5f;   // metres above ground for the display
    public float  screenWidth   = 1.8f;   // world-space canvas width in metres
    public float  screenHeightM = 2.4f;   // world-space canvas height in metres

    // Runtime tracking
    private Dictionary<string, StationVisual> _stations = new Dictionary<string, StationVisual>();

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (pylonPrefab == null)
            Debug.LogWarning("[WeatherStationPylon] pylonPrefab not assigned — assign Pylon.obj in the Inspector.");

        if (georeference == null)
            georeference = FindObjectOfType<CesiumGeoreference>();

        StartCoroutine(PollLoop());
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return FetchAndUpdate();
            yield return new WaitForSeconds(refreshInterval);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fetch + Parse
    // ─────────────────────────────────────────────────────────────────────────

    IEnumerator FetchAndUpdate()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(apiUrl))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[WeatherStationPylon] API request failed: " + req.error);
                yield break;
            }

            string json = req.downloadHandler.text;

            // JsonUtility cannot parse root arrays — wrap it
            string wrapped = "{\"items\":" + json + "}";

            EntityWrapper data = null;
            try   { data = JsonUtility.FromJson<EntityWrapper>(wrapped); }
            catch (Exception e) { Debug.LogError("[WeatherStationPylon] JSON parse error: " + e.Message); yield break; }

            if (data == null || data.items == null) yield break;

            foreach (WeatherEntity entity in data.items)
            {
                if (entity == null) continue;
                if (entity.location == null || string.IsNullOrEmpty(entity.location.value)) continue;

                double lat, lon;
                if (!TryParseLatLon(entity.location.value, out lat, out lon)) continue;

                UpsertStation(entity, lat, lon);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Station Visual Management
    // ─────────────────────────────────────────────────────────────────────────

    void UpsertStation(WeatherEntity entity, double lat, double lon)
    {
        StationVisual sv;

        if (!_stations.TryGetValue(entity.id, out sv) || sv.root == null)
        {
            sv = CreateStationVisual(entity.id, lat, lon);
            _stations[entity.id] = sv;
        }

        // Update the display text
        if (sv.displayText != null)
            sv.displayText.text = BuildDisplayText(entity);
    }

    StationVisual CreateStationVisual(string id, double lat, double lon)
    {
        // ── Root GameObject ───────────────────────────────────────────────────
        GameObject root = new GameObject("WeatherStation_" + id);
        root.transform.SetParent(transform, false);

        // ── CesiumGlobeAnchor positions root at geo coords ────────────────────
        CesiumGlobeAnchor anchor = root.AddComponent<CesiumGlobeAnchor>();
        anchor.adjustOrientationForGlobeWhenMoving = true;
        anchor.detectTransformChanges = true;
        anchor.latitude  = lat;
        anchor.longitude = lon;
        anchor.height    = spawnHeight;

        // ── Pylon mesh ────────────────────────────────────────────────────────
        if (pylonPrefab != null)
        {
            GameObject pylon = Instantiate(pylonPrefab, root.transform);
            pylon.transform.localPosition = Vector3.zero;
            pylon.transform.localScale    = Vector3.one * 0.3f;
        }
        else
        {
            // Fallback: simple cylinder pole
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0f, 2.25f, 0f);
            pole.transform.localScale    = new Vector3(0.08f, 2.25f, 0.08f);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", new Color(0.3f, 0.3f, 0.3f));
            pole.GetComponent<Renderer>().material = mat;
            Destroy(pole.GetComponent<Collider>());
        }

        // ── World-space Canvas ────────────────────────────────────────────────
        GameObject canvasGO = new GameObject("WeatherCanvas");
        canvasGO.transform.SetParent(root.transform, false);
        canvasGO.transform.localPosition = new Vector3(0f, screenHeight, 0.05f);

        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode    = RenderMode.WorldSpace;
        canvas.worldCamera   = Camera.main;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(screenWidth * 100f, screenHeightM * 100f);
        canvasRect.localScale = Vector3.one * 0.01f;   // 1 unit = 1 cm  (so 100px = 1m)

        // ── Background panel ──────────────────────────────────────────────────
        GameObject panelGO = new GameObject("Panel");
        panelGO.transform.SetParent(canvasGO.transform, false);

        RectTransform panelRect = panelGO.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        Image bg = panelGO.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.08f, 0.16f, 0.92f);   // dark navy

        // ── Header bar ────────────────────────────────────────────────────────
        GameObject headerGO = new GameObject("Header");
        headerGO.transform.SetParent(panelGO.transform, false);

        RectTransform headerRect = headerGO.AddComponent<RectTransform>();
        headerRect.anchorMin    = new Vector2(0f, 0.88f);
        headerRect.anchorMax    = new Vector2(1f, 1f);
        headerRect.offsetMin    = Vector2.zero;
        headerRect.offsetMax    = Vector2.zero;

        Image headerBg = headerGO.AddComponent<Image>();
        headerBg.color = new Color(0.10f, 0.45f, 0.80f, 1f);  // blue header

        GameObject headerTextGO = new GameObject("HeaderText");
        headerTextGO.transform.SetParent(headerGO.transform, false);

        RectTransform htr = headerTextGO.AddComponent<RectTransform>();
        htr.anchorMin = Vector2.zero;
        htr.anchorMax = Vector2.one;
        htr.offsetMin = new Vector2(10f, 0f);
        htr.offsetMax = new Vector2(-10f, 0f);

        TMP_Text headerText = headerTextGO.AddComponent<TextMeshProUGUI>();
        headerText.text      = "  WEATHER STATION";
        headerText.fontSize  = 18f;
        headerText.fontStyle = FontStyles.Bold;
        headerText.color     = Color.white;
        headerText.alignment = TextAlignmentOptions.MidlineLeft;

        // ── Data text ─────────────────────────────────────────────────────────
        GameObject dataGO = new GameObject("DataText");
        dataGO.transform.SetParent(panelGO.transform, false);

        RectTransform dataRect = dataGO.AddComponent<RectTransform>();
        dataRect.anchorMin = new Vector2(0f, 0f);
        dataRect.anchorMax = new Vector2(1f, 0.87f);
        dataRect.offsetMin = new Vector2(12f,  8f);
        dataRect.offsetMax = new Vector2(-12f, -4f);

        TMP_Text dataText = dataGO.AddComponent<TextMeshProUGUI>();
        dataText.text      = "Loading...";
        dataText.fontSize  = 13f;
        dataText.color     = new Color(0.88f, 0.94f, 1f);
        dataText.alignment = TextAlignmentOptions.TopLeft;
        dataText.lineSpacing = 8f;

        // ── Snap to terrain after spawn ───────────────────────────────────────
        StartCoroutine(SnapAndFaceCamera(root, canvasGO));

        StationVisual sv = new StationVisual();
        sv.root        = root;
        sv.anchor      = anchor;
        sv.displayText = dataText;
        return sv;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Terrain snap + billboard
    // ─────────────────────────────────────────────────────────────────────────

    IEnumerator SnapAndFaceCamera(GameObject root, GameObject canvas)
    {
        // Wait for Cesium to position the object
        yield return new WaitForSeconds(1f);
        yield return null;

        // Terrain snap
        for (int i = 0; i < 8; i++)
        {
            if (root == null) yield break;

            Ray ray = new Ray(root.transform.position + Vector3.up * 500f, Vector3.down);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 1000f, ~0, QueryTriggerInteraction.Ignore))
            {
                root.transform.position = hit.point + Vector3.up * groundOffset;
                break;
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    void LateUpdate()
    {
        // Make all display panels face the camera (billboard)
        Camera cam = Camera.main;
        if (cam == null) return;

        foreach (var kv in _stations)
        {
            if (kv.Value.root == null) continue;

            Transform canvasTrans = kv.Value.root.transform.Find("WeatherCanvas");
            if (canvasTrans == null) continue;

            Vector3 toCam = cam.transform.position - canvasTrans.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude > 0.001f)
                canvasTrans.rotation = Quaternion.LookRotation(toCam);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Build display text
    // ─────────────────────────────────────────────────────────────────────────

    string BuildDisplayText(WeatherEntity e)
    {
        string dt   = Val(e.datetime);
        string time = dt.Length >= 19 ? dt.Substring(11, 8) : dt;

        return
            Row("Temperature",       Val(e.temperature),       "C") +
            Row("Feels Like",         Val(e.feelsLikeTemperature), "C") +
            Row("Humidity",           Val(e.humidity),           "%") +
            Row("Air Pressure",       Val(e.airPressure),        "hPa") +
            Row("Air Press. Abs",     Val(e.airPressureAbsolute),"hPa") +
            Row("Dew Point",          Val(e.dewPointTemperature),"C") +
            Row("Wind Speed",         Val(e.windSpeed),          "m/s") +
            Row("Wind Gust",          Val(e.windGust),           "m/s") +
            Row("Wind Dir",           Val(e.windDirection),      "") +
            Row("UV Index",           Val(e.uvIndex),            "") +
            Row("Light",              Val(e.lightIntensity),     "lux") +
            Row("Rain Today",         Val(e.rainDaily),          "mm") +
            Row("Rain Monthly",       Val(e.rainMonthly),        "mm") +
            "\n<color=#6FA8DC><size=11>Updated: " + time + "</size></color>";
    }

    static string Row(string label, string value, string unit)
    {
        if (string.IsNullOrEmpty(value) || value == "—") return "";
        string display = string.IsNullOrEmpty(unit) ? value : value + " " + unit;
        return string.Format("<color=#A8C8E8>{0,-22}</color><b>{1}</b>\n", label, display);
    }

    static string Val(NGSIField f)
    {
        if (f == null || string.IsNullOrEmpty(f.value)) return "—";
        return f.value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    static bool TryParseLatLon(string s, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        string[] parts = s.Split(',');
        if (parts.Length < 2) return false;
        return double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Data classes
    // ─────────────────────────────────────────────────────────────────────────

    class StationVisual
    {
        public GameObject    root;
        public CesiumGlobeAnchor anchor;
        public TMP_Text      displayText;
    }

    [Serializable]
    class EntityWrapper { public WeatherEntity[] items; }

    [Serializable]
    class WeatherEntity
    {
        public string id;
        public string type;
        public NGSIField airPressure;
        public NGSIField airPressureAbsolute;
        public NGSIField datetime;
        public NGSIField dewPointTemperature;
        public NGSIField feelsLikeTemperature;
        public NGSIField heatIndexTemperature;
        public NGSIField humidity;
        public NGSIField lightIntensity;
        public NGSIField location;
        public NGSIField rain;
        public NGSIField rainDaily;
        public NGSIField rainHourly;
        public NGSIField rainMonthly;
        public NGSIField rainWeekly;
        public NGSIField rainYearly;
        public NGSIField temperature;
        public NGSIField uvIndex;
        public NGSIField windChillTemperature;
        public NGSIField windDirection;
        public NGSIField windGust;
        public NGSIField windSpeed;
        public NGSIField windSpeed10Min;
    }

    [Serializable]
    class NGSIField
    {
        public string type;
        public string value;
    }
}
