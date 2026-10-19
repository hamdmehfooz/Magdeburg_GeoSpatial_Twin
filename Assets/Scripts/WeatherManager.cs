using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using System.Globalization;
using System;

// ✅ Fix ambiguous references
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;
using Application = UnityEngine.Application;

namespace CTI
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(WindZone))]
    public class CTI_WeatherWindManager : MonoBehaviour
    {
        // ================= WIND =================
        private WindZone m_WindZone;
        private Transform trans;

        public float MaxApiWindSpeed = 20f;

        private float ApiWindSpeed;
        private float ApiWindDirection;

        // ================= WEATHER =================
        [Header("Location")]
        public float Latitude = 52.1205f;
        public float Longitude = 11.6276f;
        public float UpdateInterval = 900f;

        [Header("Scene References")]
        public Light sunLight;

        [Header("Shadow Settings")]
        public float shadowRayDistance = 2000f;

        private float currentTemperature;

        void Awake()
        {
            m_WindZone = GetComponent<WindZone>();
            trans = transform;
        }

        void Start()
        {
            // 🔥 Default fallback (so system always works)
            ApiWindSpeed = 5f;
            ApiWindDirection = 180f;
            currentTemperature = 25f;

            if (Application.isPlaying)
                StartCoroutine(UpdateWeatherRoutine());
        }

        void Update()
        {
            ApplyWind();
            UpdateSunPosition();
        }

        // ================= WEATHER API =================

        IEnumerator UpdateWeatherRoutine()
        {
            while (true)
            {
                yield return FetchWeather();
                yield return new WaitForSeconds(UpdateInterval);
            }
        }

        IEnumerator FetchWeather()
        {
            string url =
                "https://api.open-meteo.com/v1/forecast" +
                "?latitude=" + Latitude.ToString(CultureInfo.InvariantCulture) +
                "&longitude=" + Longitude.ToString(CultureInfo.InvariantCulture) +
                "&current=temperature_2m,wind_speed_10m,wind_direction_10m";

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    ProcessWeather(request.downloadHandler.text);
                }
                else
                {
                    // 🔥 SAFE FALLBACK
                    Debug.LogWarning("Weather API Failed → Using fallback values");

                    ApiWindSpeed = 5f;
                    ApiWindDirection = 180f;
                    currentTemperature = 25f;
                }
            }
        }

        void ProcessWeather(string json)
        {
            ApiResponse data = JsonUtility.FromJson<ApiResponse>(json);

            if (data == null || data.current == null)
                return;

            ApiWindSpeed = data.current.wind_speed_10m;
            ApiWindDirection = data.current.wind_direction_10m;
            currentTemperature = data.current.temperature_2m;
        }

        // ================= WIND =================

        void ApplyWind()
        {
            if (m_WindZone == null)
                return;

            m_WindZone.mode = WindZoneMode.Directional;

            float unityDirection = (ApiWindDirection + 180f) % 360f;

            Vector3 dir = new Vector3(
                Mathf.Sin(unityDirection * Mathf.Deg2Rad),
                0f,
                Mathf.Cos(unityDirection * Mathf.Deg2Rad)
            ).normalized;

            trans.forward = dir;

            float windRatio = Mathf.Clamp01(ApiWindSpeed / MaxApiWindSpeed);

            m_WindZone.windMain = Mathf.Lerp(2f, 25f, windRatio);
            m_WindZone.windTurbulence = Mathf.Lerp(0.2f, 2f, windRatio);
        }

        // ================= SUN =================

        void UpdateSunPosition()
        {
            if (sunLight == null)
                return;

            DateTime utcTime = DateTime.UtcNow;

            Vector2 sunAngles = CalculateSunPosition(utcTime, Latitude, Longitude);

            float azimuth = sunAngles.x;
            float elevation = sunAngles.y;

            sunLight.transform.rotation =
                Quaternion.Euler(90f - elevation, azimuth, 0f);

            if (elevation < 0)
                sunLight.intensity = 0f;
        }

        // ================= SHADOW DETECTION =================

        public float GetShadowFactor(Vector3 worldPoint)
        {
            if (sunLight == null)
                return 0f;

            Vector3 sunDirection = -sunLight.transform.forward;

            int hitCount = 0;

            Vector3[] offsets = new Vector3[]
            {
                Vector3.zero,
                new Vector3(1f, 0, 0),
                new Vector3(-1f, 0, 0),
                new Vector3(0, 0, 1f),
                new Vector3(0, 0, -1f)
            };

            int totalRays = offsets.Length;

            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 origin = worldPoint + offsets[i];

                RaycastHit[] hits = Physics.RaycastAll(origin, sunDirection, shadowRayDistance);

                bool blocked = false;

                foreach (var hit in hits)
                {
                    float surfaceAngle = Vector3.Dot(hit.normal, Vector3.up);

                    // Ignore flat ground
                    if (surfaceAngle > 0.85f)
                        continue;

                    blocked = true;
                    break;
                }

                if (blocked)
                    hitCount++;
            }

            return (float)hitCount / totalRays;
        }

        // ================= SUN CALC =================

        Vector2 CalculateSunPosition(DateTime time, float lat, float lon)
        {
            double julianDay = time.ToOADate() + 2415018.5;
            double n = julianDay - 2451545.0;

            double L = (280.46 + 0.9856474 * n) % 360;
            double g = (357.528 + 0.9856003 * n) % 360;

            double lambda =
                L + 1.915 * Math.Sin(g * Mathf.Deg2Rad) +
                0.02 * Math.Sin(2 * g * Mathf.Deg2Rad);

            double epsilon = 23.439 - 0.0000004 * n;

            double declination =
                Mathf.Rad2Deg *
                Mathf.Asin(Mathf.Sin((float)epsilon * Mathf.Deg2Rad) *
                Mathf.Sin((float)lambda * Mathf.Deg2Rad));

            double solarTime =
                time.Hour + time.Minute / 60.0 + time.Second / 3600.0 +
                lon / 15.0;

            double hourAngle = (solarTime - 12) * 15;

            double elevation =
                Mathf.Rad2Deg *
                Mathf.Asin(
                Mathf.Sin(lat * Mathf.Deg2Rad) *
                Mathf.Sin((float)declination * Mathf.Deg2Rad) +
                Mathf.Cos(lat * Mathf.Deg2Rad) *
                Mathf.Cos((float)declination * Mathf.Deg2Rad) *
                Mathf.Cos((float)hourAngle * Mathf.Deg2Rad));

            double azimuth =
                Mathf.Rad2Deg *
                Mathf.Acos(
                (Mathf.Sin((float)declination * Mathf.Deg2Rad) -
                Mathf.Sin(lat * Mathf.Deg2Rad) *
                Mathf.Sin((float)elevation * Mathf.Deg2Rad)) /
                (Mathf.Cos(lat * Mathf.Deg2Rad) *
                Mathf.Cos((float)elevation * Mathf.Deg2Rad)));

            return new Vector2((float)azimuth, (float)elevation);
        }

        // ================= JSON =================

        [System.Serializable]
        private class ApiResponse
        {
            public Current current;
        }

        [System.Serializable]
        private class Current
        {
            public float wind_speed_10m;
            public float wind_direction_10m;
            public float temperature_2m;
        }
    }
}