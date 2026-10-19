using System;
using System.Collections;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using Newtonsoft.Json.Linq;

public class WeatherDisplayManager : MonoBehaviour
{
    [Header("API")]
    public string apiUrl = "https://imiq-public.et.uni-magdeburg.de/api/orion/entities?type=Weather&limit=1000";
    public float refreshInterval = 5f;

    [Header("Display naming")]
    [Tooltip("Your display objects are named like Display_FacultyCS.")]
    public string displayPrefix = "Display_";

    [Header("GB80 Weather Display")]
    [Tooltip("Name of the scene object that shows GB80 weather.")]
    public string gb80ObjectName = "Display_FacultyGB80";
    [Tooltip("Entity ID in the API for the GB80 sensor.")]
    public string gb80EntityId = "Sensor:Weather:FacultyGB80";
    [Tooltip("How often to refresh the GB80 display (seconds). Default = 3600 (1 hour).")]
    public float gb80RefreshInterval = 3600f;

    //header for displaing mensa menu 
    [Header("Mensa API")]
    public string mensaApiUrl = "https://imiq-public.et.uni-magdeburg.de/api/orion/entities?type=Mensa&limit=1000";
    public string mensaObjectName = "Mensa_Menu";   // scene object name
    public string mensaEntityId = "Rest:MensaUni";  // id in JSON
    public bool showEnglish = true;                 // toggle language
    public int maxItems = 8;

    void Start()
    {
        StartCoroutine(PollLoop());
        StartCoroutine(MensaPollLoop());
        StartCoroutine(GB80PollLoop());
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return FetchAndUpdateAllDisplays();
            yield return new WaitForSeconds(refreshInterval);
        }
    }

    IEnumerator FetchAndUpdateAllDisplays()
    {
        using var req = UnityWebRequest.Get(apiUrl);
        req.timeout = 20;
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("API request failed: " + req.error);
            yield break;
        }

        // Parse response array
        JArray arr;
        try
        {
            arr = JArray.Parse(req.downloadHandler.text);
        }
        catch (Exception e)
        {
            Debug.LogError("JSON parse failed: " + e.Message);
            yield break;
        }

        // Build lookup: id temperature and humidity to display
        var map = new Dictionary<string, (float temp, float hum)>();

        foreach (var t in arr)
        {
            string id = t.Value<string>("id");
            if (string.IsNullOrEmpty(id)) continue;

            float temp = ParseFloatFlexible(t["temperature"]?["value"]);
            float hum = ParseFloatFlexible(t["humidity"]?["value"]);

            map[id] = (temp, hum);
        }

        // Find all display objects in scene
        // (If you have many objects, we can cache this instead of finding every refresh.)
        GameObject[] all = FindObjectsOfType<GameObject>(true);

        foreach (var go in all)
        {
            if (go == null) continue;
            if (!go.name.StartsWith(displayPrefix, StringComparison.Ordinal)) continue;

            // Display_FacultyCS -> FacultyCS
            string suffix = go.name.Substring(displayPrefix.Length);
            if (string.IsNullOrEmpty(suffix)) continue;

            // FacultyCS -> Sensor:Weather:FacultyCS
            string apiId = $"Sensor:Weather:{suffix}";

            // Find TMP text inside this display
            TMP_Text text = go.GetComponentInChildren<TMP_Text>(true);
            if (text == null)
            {
                Debug.LogWarning($"No TMP_Text found inside {go.name}");
                continue;
            }

            if (map.TryGetValue(apiId, out var data))
            {
                text.text =
                    $"{suffix}\n" +
                    $"Temperature: {data.temp:0.##} �C\n" +
                    $"Humidity: {data.hum:0.#} %";
            }
            else
            {
                // No matching sensor in API
                text.text = $"{suffix}\nNo data";
            }
        }
    }

    // ── GB80 hourly weather display ───────────────────────────────────────────

    IEnumerator GB80PollLoop()
    {
        // Fetch immediately on start, then every gb80RefreshInterval
        while (true)
        {
            yield return FetchAndUpdateGB80();
            yield return new WaitForSeconds(gb80RefreshInterval);
        }
    }

    IEnumerator FetchAndUpdateGB80()
    {
        // Find the display object — warn once if missing, keep retrying each cycle
        GameObject displayGO = GameObject.Find(gb80ObjectName);
        if (displayGO == null)
        {
            Debug.LogWarning($"[WeatherDisplayManager] '{gb80ObjectName}' not found in scene. " +
                             "Add a Display prefab instance named exactly that.");
            yield break;
        }

        TMP_Text text = displayGO.GetComponentInChildren<TMP_Text>(true);
        if (text == null)
        {
            Debug.LogWarning($"[WeatherDisplayManager] No TMP_Text found under '{gb80ObjectName}'.");
            yield break;
        }

        // Show loading state on first fetch
        if (text.text.Length == 0)
            text.text = "Loading weather...";

        using var req = UnityWebRequest.Get(apiUrl);
        req.timeout = 20;
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[WeatherDisplayManager] GB80 API request failed: {req.error}");
            yield break;
        }

        JArray arr;
        try   { arr = JArray.Parse(req.downloadHandler.text); }
        catch (Exception e)
        {
            Debug.LogError($"[WeatherDisplayManager] GB80 JSON parse failed: {e.Message}");
            yield break;
        }

        // Find the matching entity
        JToken entity = null;
        foreach (var t in arr)
        {
            if (t?.Value<string>("id") == gb80EntityId)
            {
                entity = t;
                break;
            }
        }

        if (entity == null)
        {
            Debug.LogWarning($"[WeatherDisplayManager] Entity '{gb80EntityId}' not found in API response.");
            text.text = "GB80 Weather\nNo data available.";
            yield break;
        }

        // Parse the 5 requested fields
        float airPressure         = ParseFloatFlexible(entity["airPressure"]?["value"]);
        float feelsLikeTemp       = ParseFloatFlexible(entity["feelsLikeTemperature"]?["value"]);
        float humidity            = ParseFloatFlexible(entity["humidity"]?["value"]);
        float temperature         = ParseFloatFlexible(entity["temperature"]?["value"]);
        float windGust            = ParseFloatFlexible(entity["windGust"]?["value"]);

        // Timestamp from API
        string datetime = entity["datetime"]?["value"]?.ToString() ?? "";
        string timeStr  = datetime.Length >= 19 ? datetime.Substring(11, 5) : datetime;

        // Format display text
        var sb = new StringBuilder();
        sb.AppendLine("<b>GB80  WEATHER</b>");
        sb.AppendLine();
        sb.AppendLine(FormatRow("Temperature",    temperature,  "°C"));
        sb.AppendLine(FormatRow("Feels Like",      feelsLikeTemp, "°C"));
        sb.AppendLine(FormatRow("Humidity",        humidity,     "%"));
        sb.AppendLine(FormatRow("Air Pressure",    airPressure,  "hPa"));
        sb.AppendLine(FormatRow("Wind Gust",       windGust,     "m/s"));
        if (!string.IsNullOrEmpty(timeStr))
            sb.AppendLine($"<size=80%><color=#888888>Updated {timeStr}</color></size>");

        text.text = sb.ToString();
    }

    static string FormatRow(string label, float value, string unit)
    {
        if (float.IsNaN(value)) return $"{label}: --";
        return $"{label}:  <b>{value:0.#} {unit}</b>";
    }

    static float ParseFloatFlexible(JToken token)
    {
        if (token == null) return float.NaN;

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            return token.Value<float>();

        if (token.Type == JTokenType.String &&
            float.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float f))
            return f;

        return float.NaN;
    }

    private IEnumerator MensaPollLoop()
    {
        while (true)
        {
            yield return FetchAndUpdateMensaMenu();
            yield return new WaitForSeconds(60f); // menu changes less frequently --- cahnge it later to daily
        }
    }

    private IEnumerator FetchAndUpdateMensaMenu()
    {
        using var req = UnityEngine.Networking.UnityWebRequest.Get(mensaApiUrl);
        req.timeout = 20;
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            Debug.LogError("Mensa API request failed: " + req.error);
            yield break;
        }

        JArray arr;
        try { arr = JArray.Parse(req.downloadHandler.text); }
        catch (System.Exception e)
        {
            Debug.LogError("Mensa JSON parse failed: " + e.Message);
            yield break;
        }

        // Find the mensa entity
        JObject mensa = null;
        foreach (var t in arr)
        {
            if (t?["id"]?.ToString() == mensaEntityId)
            {
                mensa = (JObject)t;
                break;
            }
        }
        if (mensa == null)
        {
            Debug.LogWarning($"Mensa entity '{mensaEntityId}' not found.");
            yield break;
        }

        // Find the screen object in the scene
        var mensaGo = GameObject.Find(mensaObjectName);
        if (mensaGo == null)
        {
            Debug.LogWarning($"GameObject '{mensaObjectName}' not found in scene.");
            yield break;
        }

        // Find TMP text inside it (e.g. Mensa_Menu/.../Text (TMP))
        TMP_Text text = mensaGo.GetComponentInChildren<TMP_Text>(true);
        if (text == null)
        {
            Debug.LogWarning($"No TMP_Text found under '{mensaObjectName}'.");
            yield break;
        }

        // Read todaysMenu.value (array)
        JArray menuArr = mensa["todaysMenu"]?["value"] as JArray;
        if (menuArr == null)
        {
            text.text = "No menu available.";
            yield break;
        }

        // Build nice output
        string mensaName = mensa["name"]?["value"]?.ToString() ?? "Mensa";
        var sb = new StringBuilder();
        sb.AppendLine(mensaName);
        sb.AppendLine("Today's Menu");
        sb.AppendLine();

        int count = 0;
        foreach (var item in menuArr)
        {
            if (count >= maxItems) break;

            string category = item["category"]?.ToString() ?? "";
            string price = item["price"]?.ToString() ?? "";
            string dish = showEnglish
                ? (item["name_english"]?.ToString() ?? "")
                : (item["name_german"]?.ToString() ?? "");

            dish = CleanDishText(dish);

            sb.Append("� ");
            sb.Append(dish);

            if (!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(price))
            {
                sb.AppendLine();
                sb.Append("  ");
                if (!string.IsNullOrWhiteSpace(category)) sb.Append($"[{category}] ");
                if (!string.IsNullOrWhiteSpace(price)) sb.Append($"{price}");
            }

            sb.AppendLine();
            sb.AppendLine();

            count++;
        }

        text.text = sb.ToString();
    }

    private string CleanDishText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // Your german field sometimes contains english appended. This keeps it readable.
        s = s.Replace("Special offer:", "Special offer:");
        s = s.Replace("Today s", "Today's");
        s = s.Replace("  ", " ");
        return s.Trim();
    }

}
