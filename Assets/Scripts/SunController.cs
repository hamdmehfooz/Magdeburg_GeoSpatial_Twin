using UnityEngine;

public class SunController : MonoBehaviour
{
    [Header("Time Settings")]
    [Range(0, 24)]
    public float timeOfDay = 12f;

    public float daySpeed = 1f;

    [Header("Sun Angles")]
    public float maxElevation = 75f; // noon height

    void Update()
    {
        // Simulate time
        timeOfDay += Time.deltaTime * daySpeed;

        if (timeOfDay > 24f)
            timeOfDay = 0f;

        UpdateSun();
    }

    void UpdateSun()
    {
        // Convert time → angle
        float normalizedTime = timeOfDay / 24f;

        // Elevation (up/down)
        float elevation = Mathf.Sin(normalizedTime * Mathf.PI) * maxElevation;

        // Azimuth (rotation across sky)
        float azimuth = normalizedTime * 360f;

        transform.rotation = Quaternion.Euler(elevation, azimuth, 0f);
    }
}