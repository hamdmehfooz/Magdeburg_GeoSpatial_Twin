using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Sequences the VR rendering benchmark and writes one CSV per run.
///
/// Two conditions:
///   Stationary  the rig is placed at each surveyed control site in turn and
///               held still. Measures steady-state cost with a warm cache.
///   Locomotion  the rig follows the baked route. The difference between this
///               and Stationary is the cost of streaming tiles in while moving,
///               and it shows up in the tail percentiles rather than the median.
///
/// The first locomotion repeat can be run cold (no warm-up) to capture
/// first-visit streaming stalls, which are real and worth reporting separately.
/// </summary>
public class BenchmarkRunner : MonoBehaviour
{
    [System.Serializable]
    public struct Site
    {
        public string label;
        public double latitude;
        public double longitude;
    }

    [Header("Wiring")]
    public PerfSampler      sampler;
    public CameraPathReplay path;
    public CesiumGeoreference georeference;
    public Transform rig;

    [Header("Protocol")]
    [Tooltip("Seconds discarded at the start of each run: shader compilation, tile cache fill.")]
    public float warmupSeconds = 15f;
    [Tooltip("Seconds held at each stationary site.")]
    public float stationaryHoldSeconds = 20f;
    [Tooltip("Repeats per stationary site. The view is identical each time, so run-to-run " +
             "variance is only machine state, and one 20 s hold already yields ~1800 frames. " +
             "Coverage comes from using several sites, not from repeating one.")]
    public int stationaryRepeats = 3;
    [Tooltip("Repeats of the locomotion run. This one genuinely varies between runs — tile " +
             "cache state, streaming timing, GC — so it earns more repeats.")]
    public int locomotionRepeats = 5;
    [Tooltip("Run the first locomotion repeat with no warm-up, to capture cold-cache stalls.")]
    public bool includeColdRun = true;

    [Header("Conditions")]
    public bool runStationary = true;
    public bool runLocomotion = true;

    [Header("Output")]
    [Tooltip("Left empty, writes to Application.persistentDataPath/Benchmark.")]
    public string outputDirectory = "";
    [Tooltip("Free-text tag written into every CSV header, e.g. the SSE setting under test.")]
    public string runTag = "fused_C1-C4";

    [Header("Control")]
    [Tooltip("Start automatically after startDelaySeconds. Leave on — pressing a key needs " +
             "the Game view focused, which is easy to get wrong.")]
    public bool autoStart = true;
    [Tooltip("Delay before the first run, so Cesium has begun streaming tiles rather than " +
             "measuring an empty scene.")]
    public float startDelaySeconds = 30f;
    public KeyCode startKey = KeyCode.F9;
    [Tooltip("Draw a progress overlay so you can see the run is alive.")]
    public bool showOverlay = true;

    private GUIStyle _overlayStyle;
    private string _status = "idle";
    private int    _runsDone;
    private int    _runsTotal;
    private float  _phaseEnds;

    [Header("Stationary Sites")]
    [Tooltip("Three of the surveyed control sites, chosen to bracket scene complexity. " +
             "Measured building cover within 60 m and rays hitting geometry out of 72: " +
             "GCP8 0.38 / 67 (densest), GCP1 0.23 / 48 (sparse, and the georeference origin), " +
             "GCP12 0.10 / 52 (most open). Density spans 3.8x across these three.")]
    public List<Site> sites = new List<Site>
    {
        new Site { label = "GCP8",  latitude = 52.14000222, longitude = 11.64234140 },
        new Site { label = "GCP1",  latitude = 52.13958961, longitude = 11.64030469 },
        new Site { label = "GCP12", latitude = 52.14086420, longitude = 11.65073240 },
    };

    private bool   _busy;
    private string _outDir;
    private string _sessionStamp;

    void Awake()
    {
        if (sampler      == null) sampler      = FindFirstObjectByType<PerfSampler>();
        if (path         == null) path         = FindFirstObjectByType<CameraPathReplay>();
        if (georeference == null) georeference = FindFirstObjectByType<CesiumGeoreference>();

        if (rig == null)
        {
            if (path != null && path.rig != null) rig = path.rig;
            else
            {
                GameObject go = GameObject.Find("XR Origin (VR)");
                if (go != null) rig = go.transform;
            }
        }

        _outDir = string.IsNullOrEmpty(outputDirectory)
            ? Path.Combine(Application.persistentDataPath, "Benchmark")
            : outputDirectory;
    }

    void Start()
    {
        if (autoStart) StartCoroutine(AutoStart());
    }

    private IEnumerator AutoStart()
    {
        _status = "waiting for tiles";
        _phaseEnds = Time.realtimeSinceStartup + startDelaySeconds;
        yield return new WaitForSecondsRealtime(startDelaySeconds);
        Run();
    }

    void Update()
    {
        if (!_busy && Input.GetKeyDown(startKey)) Run();
    }

    void OnGUI()
    {
        if (!showOverlay) return;

        // Cached: OnGUI runs several times per frame during measured runs, and
        // allocating a style each time would put garbage in the hot path.
        if (_overlayStyle == null)
        {
            _overlayStyle = new GUIStyle(GUI.skin.label);
            _overlayStyle.fontSize = 18;
            _overlayStyle.normal.textColor = Color.white;
        }
        GUIStyle st = _overlayStyle;

        float remain = Mathf.Max(0f, _phaseEnds - Time.realtimeSinceStartup);
        string line = "BENCHMARK: " + _status;
        if (_runsTotal > 0) line += "   run " + _runsDone + "/" + _runsTotal;
        if (remain > 0.5f)  line += "   " + remain.ToString("F0") + "s";
        if (sampler != null && sampler.IsCapturing)
            line += "   frames " + sampler.SampleCount;

        GUI.Box(new Rect(8f, 8f, 640f, 58f), GUIContent.none);
        GUI.Label(new Rect(16f, 14f, 620f, 22f), line, st);
        GUI.Label(new Rect(16f, 36f, 620f, 22f), _outDir, st);
    }

    /// <summary>Starts the full protocol. Ignored while a run is in progress.</summary>
    public void Run()
    {
        if (_busy)
        {
            Debug.LogWarning("[Benchmark] Already running.");
            return;
        }
        StartCoroutine(RunAll());
    }

    private IEnumerator RunAll()
    {
        _busy = true;
        _sessionStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss",
                                                     CultureInfo.InvariantCulture);
        Directory.CreateDirectory(_outDir);
        Debug.Log("[Benchmark] Output -> " + _outDir);

        if (sampler == null || rig == null)
        {
            Debug.LogError("[Benchmark] Sampler or rig missing — aborting.");
            _busy = false;
            yield break;
        }

        // The route must already be baked. Baking raycasts, and in a build no
        // Cesium tiles are streamed at scene load, so a runtime bake would
        // silently produce a path with no ground under it. Bake in the Editor
        // (right-click CameraPathReplay -> Bake Path) and save the scene.
        if (runLocomotion && path != null && !path.IsBaked)
        {
            Debug.LogError("[Benchmark] Route is not baked. In the Editor, fly the Scene " +
                           "view along the route so Cesium streams the tiles, right-click " +
                           "CameraPathReplay -> Bake Path, save the scene, then rebuild.");
            runLocomotion = false;
        }

        // Interleaved rather than all-stationary-then-all-locomotion: a session
        // drifts as the GPU heats up, and running one condition entirely before
        // the other would fold that drift into the comparison between them.
        int rounds = Mathf.Max(runStationary ? stationaryRepeats : 0,
                               runLocomotion ? locomotionRepeats : 0);

        _runsDone  = 0;
        _runsTotal = (runStationary ? sites.Count * stationaryRepeats : 0) +
                     (runLocomotion && path != null ? locomotionRepeats : 0);

        for (int r = 1; r <= Mathf.Max(1, rounds); r++)
        {
            if (runStationary && r <= stationaryRepeats)
                yield return StartCoroutine(RunStationarySet(r));

            if (runLocomotion && path != null && r <= locomotionRepeats)
            {
                bool cold = includeColdRun && r == 1;
                yield return StartCoroutine(RunLocomotion(r, cold));
            }
        }

        Debug.Log("[Benchmark] Complete. " + _outDir);
        _status = "COMPLETE - " + _runsDone + " runs written";
        _phaseEnds = 0f;
        _busy = false;
    }

    private IEnumerator RunStationarySet(int repeat)
    {
        for (int i = 0; i < sites.Count; i++)
        {
            Site site = sites[i];
            PlaceRigAt(site.latitude, site.longitude);

            // Let the tiles around this viewpoint stream in before measuring.
            _status = "stationary " + site.label + " r" + repeat + " (warm-up)";
            _phaseEnds = Time.realtimeSinceStartup + warmupSeconds;
            yield return new WaitForSecondsRealtime(warmupSeconds);

            _status = "stationary " + site.label + " r" + repeat + " (recording)";
            _phaseEnds = Time.realtimeSinceStartup + stationaryHoldSeconds;
            sampler.StartCapture();
            yield return new WaitForSecondsRealtime(stationaryHoldSeconds);
            sampler.StopCapture();

            WriteRun("stationary", site.label, repeat, true, 0f);
            _runsDone++;
            yield return null;
        }
    }

    private IEnumerator RunLocomotion(int repeat, bool cold)
    {
        path.ResetToStart();

        // A cold run starts sampling the instant the rig begins moving, so the
        // first-visit tile loads land inside the measurement.
        if (!cold)
        {
            _status = "locomotion r" + repeat + " (warm-up)";
            _phaseEnds = Time.realtimeSinceStartup + warmupSeconds;
            yield return new WaitForSecondsRealtime(warmupSeconds);
        }

        _status = "locomotion r" + repeat + (cold ? " (cold, recording)" : " (recording)");
        _phaseEnds = Time.realtimeSinceStartup + path.TotalLength / Mathf.Max(0.01f, path.speed);

        bool done = false;
        System.Action onDone = delegate { done = true; };
        path.OnComplete += onDone;

        sampler.StartCapture();
        path.Begin();

        while (!done && path.IsRunning)
            yield return null;

        sampler.StopCapture();
        path.OnComplete -= onDone;
        path.Stop();

        WriteRun("locomotion", cold ? "cold" : "warm", repeat, !cold, path.TotalLength);
        _runsDone++;
        yield return null;
    }

    private void PlaceRigAt(double lat, double lon)
    {
        if (georeference == null || rig == null) return;

        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(lon, lat, 200.0));
        double3 u = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        Vector3 local = new Vector3((float)u.x, (float)u.y, (float)u.z);

        Transform gt    = georeference.transform;
        Vector3   world = gt.TransformPoint(local);

        // Same MeshCollider-only ground snap the importers use: plain
        // BoxColliders here are tileset and boundary-wall proxies, not ground.
        Ray ray = new Ray(world + Vector3.up * 400f, Vector3.down);
        RaycastHit[] all = Physics.RaycastAll(ray, 1900f, ~0, QueryTriggerInteraction.Ignore);

        float   bestDst = float.MaxValue;
        Vector3 bestPt  = world;
        bool    found   = false;

        for (int i = 0; i < all.Length; i++)
        {
            if (!(all[i].collider is MeshCollider)) continue;
            if (all[i].distance >= bestDst) continue;
            bestDst = all[i].distance;
            bestPt  = all[i].point;
            found   = true;
        }

        rig.position = found ? bestPt : world;
    }

    /// <summary>
    /// Headset refresh rate, or 0 when no display subsystem is running (Editor
    /// without an HMD). XRDevice.refreshRate is obsolete, so this goes through
    /// the running XRDisplaySubsystem instead.
    /// </summary>
    private static float GetDisplayRefreshRate()
    {
        List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);

        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i] == null || !displays[i].running) continue;

            float hz;
            if (displays[i].TryGetDisplayRefreshRate(out hz)) return hz;
        }
        return 0f;
    }

    private void WriteRun(string condition, string label, int repeat, bool warm, float routeLength)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        List<string> meta = new List<string>();

        meta.Add("session=" + _sessionStamp);
        meta.Add("tag=" + runTag);
        meta.Add("condition=" + condition);
        meta.Add("label=" + label);
        meta.Add("repeat=" + repeat.ToString(ci));
        meta.Add("warm=" + (warm ? "1" : "0"));
        meta.Add("frames=" + sampler.SampleCount.ToString(ci));

        meta.Add("unity=" + Application.unityVersion);
        meta.Add("platform=" + Application.platform);
        meta.Add("build_is_editor=" + (Application.isEditor ? "1" : "0"));
        meta.Add("build_is_development=" + (Debug.isDebugBuild ? "1" : "0"));
        meta.Add("gpu=" + SystemInfo.graphicsDeviceName);
        meta.Add("gpu_api=" + SystemInfo.graphicsDeviceType);
        meta.Add("gpu_mem_mb=" + SystemInfo.graphicsMemorySize.ToString(ci));
        meta.Add("cpu=" + SystemInfo.processorType);
        meta.Add("cpu_threads=" + SystemInfo.processorCount.ToString(ci));
        meta.Add("system_mem_mb=" + SystemInfo.systemMemorySize.ToString(ci));
        meta.Add("quality_level=" + QualitySettings.names[QualitySettings.GetQualityLevel()]);

        meta.Add("hmd_refresh_hz=" + GetDisplayRefreshRate().ToString("F2", ci));
        meta.Add("hmd_model=" + SystemInfo.deviceModel);

        if (path != null)
        {
            meta.Add("speed_mps=" + path.speed.ToString("F3", ci));
            meta.Add("route_length_m=" + routeLength.ToString("F2", ci));
            meta.Add("align_to_heading=" + (path.alignToHeading ? "1" : "0"));
        }

        // Record the streaming quality knob per tileset — this is the parameter
        // the SSE sweep varies, so it has to travel with the data.
        Cesium3DTileset[] tilesets = FindObjectsByType<Cesium3DTileset>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < tilesets.Length; i++)
            meta.Add("sse[" + tilesets[i].gameObject.name + "]=" +
                     tilesets[i].maximumScreenSpaceError.ToString("F2", ci));

        string file = string.Format(ci, "{0}_{1}_{2}_r{3}.csv",
                                    _sessionStamp, condition, label, repeat);
        sampler.WriteCsv(Path.Combine(_outDir, file), meta);
    }
}
