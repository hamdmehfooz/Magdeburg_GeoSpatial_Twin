using System.Collections.Generic;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Deterministic locomotion for the VR rendering benchmark.
///
/// Waypoints are given in WGS84 latitude/longitude so a route survives any change
/// to the scene origin and can be quoted in the paper. On Bake() they are
/// converted to georeference-local space, run through a Catmull-Rom spline,
/// resampled at a fixed spacing and snapped onto the Cesium terrain. The result
/// is a plain polyline, so the run itself does no spline evaluation and no
/// raycasting — neither may perturb the frame times being measured.
///
/// Two things that matter in VR and are easy to get wrong:
///
///   1. The rig moves, not the camera. Head tracking overwrites the camera's
///      local pose every frame, so anything written to Camera.transform is
///      discarded. We drive the XR Origin and let the HMD pose apply on top.
///
///   2. Motion is time-parameterised, not frame-parameterised. Advancing by
///      speed * deltaTime means a slow frame covers more ground, so every
///      condition traverses the identical route in the identical wall-clock
///      time. Advancing a fixed step per frame would make a slower condition
///      cover less of the map, and the conditions would no longer be comparable.
/// </summary>
public class CameraPathReplay : MonoBehaviour
{
    [System.Serializable]
    public struct Waypoint
    {
        public string label;
        public double latitude;
        public double longitude;
    }

    [Header("Scene")]
    public CesiumGeoreference georeference;
    [Tooltip("The XR Origin. Leave empty to search for one named 'XR Origin (VR)'.")]
    public Transform rig;

    [Header("Route")]
    [Tooltip("Two points give a straight line. Found by sweeping the loaded tiles for the " +
             "longest corridor whose ground stays at street level and which is unobstructed " +
             "at 1.7 m head height: 139 m, ground range 0.87 m, 45% building cover within " +
             "60 m, zero blocked segments. Move the points and re-bake for a different " +
             "stretch; Bake Path reports any obstruction.")]
    public List<Waypoint> waypoints = new List<Waypoint>
    {
        new Waypoint { label = "start", latitude = 52.14017343, longitude = 11.64833807 },
        new Waypoint { label = "end",   latitude = 52.13896686, longitude = 11.64781241 },
    };

    [Header("Motion")]
    [Tooltip("m/s. 1.4 = walking, 5.0 = cycling. Faster stresses tile streaming harder.")]
    public float speed = 1.4f;
    [Tooltip("Yaw the rig to face along the route. Off = fixed heading throughout.")]
    public bool alignToHeading = true;
    public bool loop = false;

    [Header("Path Baking")]
    [Tooltip("Spline resample spacing in metres. Smaller = smoother, slower to bake.")]
    public float sampleSpacing = 2f;
    public bool  snapToTerrain = true;
    public float raycastStartHeight = 400f;
    public float raycastDistance = 1500f;
    [Tooltip("Moving-average window over baked heights, to stop the rig jittering on noisy photogrammetry.")]
    public int heightSmoothWindow = 5;
    [Tooltip("Raised above the snapped ground. Leave at 0 — the Camera Offset already supplies eye height.")]
    public float groundClearance = 0f;

    [Header("Rooftop Rejection")]
    [Tooltip("Samples snapping more than this above the route median are treated as roof hits, not ground.")]
    public float maxRise = 4f;
    [Tooltip("Samples snapping more than this below the route median are treated as holes in the mesh.")]
    public float maxDrop = 8f;
    [Tooltip("Head height used for the post-bake clearance check that reports segments running into geometry.")]
    public float headClearanceHeight = 1.7f;

    [Header("Interference")]
    [Tooltip("Components disabled while the run is active — assign your XRI locomotion providers here so they cannot fight the script.")]
    public Behaviour[] disableDuringRun;

    // Serialized, because a build cannot bake for itself: on scene load Cesium
    // has streamed nothing, and tiles at the far end of the route never load
    // while the rig waits at the start, so every ground raycast would miss.
    // Bake in the Editor with the Scene view flown along the route, and the
    // polyline travels into the build with the scene.
    [SerializeField, HideInInspector]
    private List<Vector3> _pts = new List<Vector3>();           // georeference-local

    private readonly List<float> _cum = new List<float>();      // cumulative distance
    private float _length;
    private float _dist;
    private bool  _running;
    private bool  _baked;
    private Vector3 _currentLocal;
    private float _lastTime;

    /// <summary>Metres travelled along the route.</summary>
    public float DistanceAlongPath { get { return _dist; } }

    /// <summary>Total baked route length in metres.</summary>
    public float TotalLength { get { return _length; } }

    /// <summary>0 at the start of the route, 1 at the end.</summary>
    public float NormalizedProgress { get { return _length > 0f ? _dist / _length : 0f; } }

    /// <summary>Current rig position in georeference-local space, for logging.</summary>
    public Vector3 CurrentLocalPosition { get { return _currentLocal; } }

    public bool IsRunning { get { return _running; } }
    public bool IsBaked   { get { return _baked; } }

    /// <summary>Raised once when a non-looping route reaches its end.</summary>
    public event System.Action OnComplete;

    void Awake()
    {
        if (georeference == null)
            georeference = FindFirstObjectByType<CesiumGeoreference>();

        if (rig == null)
        {
            GameObject go = GameObject.Find("XR Origin (VR)");
            if (go != null) rig = go.transform;
        }

        // A polyline baked in the Editor arrives with the scene; only the
        // cumulative arc length has to be rebuilt.
        if (_pts.Count >= 2)
        {
            RebuildCumulative();
            _baked = _length > 0.01f;
        }
    }

    /// <summary>
    /// Builds the polyline. Run this in the Editor, after flying the Scene view
    /// along the route so Cesium has streamed the tiles — the ground snap can
    /// only hit colliders that exist. The result is serialized into the scene.
    /// </summary>
    [ContextMenu("Bake Path")]
    public void BakeFromMenu()
    {
        Bake();
    }

    /// <summary>Clears the baked polyline so the next Bake() starts fresh.</summary>
    [ContextMenu("Clear Baked Path")]
    public void ClearBakedPath()
    {
        _pts.Clear();
        _cum.Clear();
        _length = 0f;
        _baked  = false;
    }

    /// <summary>
    /// Builds the polyline. Call once after the Cesium tiles beneath the route
    /// have streamed in — the ground snap can only hit colliders that exist.
    /// </summary>
    public bool Bake()
    {
        _pts.Clear();
        _cum.Clear();
        _baked  = false;
        _length = 0f;

        if (georeference == null || waypoints == null || waypoints.Count < 2)
        {
            Debug.LogError("[CameraPathReplay] Need a georeference and at least two waypoints.");
            return false;
        }

        // 1. waypoints -> georeference-local
        List<Vector3> ctrl = new List<Vector3>(waypoints.Count);
        for (int i = 0; i < waypoints.Count; i++)
            ctrl.Add(LatLonToLocal(waypoints[i].latitude, waypoints[i].longitude));

        // 2. Catmull-Rom through the control points, resampled at a fixed spacing.
        //    Endpoints are duplicated so the curve starts and ends on the first
        //    and last waypoint rather than overshooting them.
        List<Vector3> dense = new List<Vector3>();
        for (int i = 0; i < ctrl.Count - 1; i++)
        {
            Vector3 p0 = ctrl[Mathf.Max(i - 1, 0)];
            Vector3 p1 = ctrl[i];
            Vector3 p2 = ctrl[i + 1];
            Vector3 p3 = ctrl[Mathf.Min(i + 2, ctrl.Count - 1)];

            float segLen  = Vector3.Distance(p1, p2);
            int   steps   = Mathf.Max(2, Mathf.CeilToInt(segLen / Mathf.Max(0.25f, sampleSpacing)));

            for (int s = 0; s < steps; s++)
                dense.Add(CatmullRom(p0, p1, p2, p3, (float)s / steps));
        }
        dense.Add(ctrl[ctrl.Count - 1]);

        // 3. resolve ground heights, rejecting rooftops and filling gaps
        if (snapToTerrain && !ResolveGroundHeights(dense))
            return false;

        // 4. smooth the heights so the rig does not jitter over noisy geometry
        if (heightSmoothWindow > 1)
            SmoothHeights(dense, heightSmoothWindow);

        for (int i = 0; i < dense.Count; i++)
        {
            Vector3 p = dense[i];
            p.y += groundClearance;
            dense[i] = p;
        }

        // 5. cumulative arc length
        _pts.AddRange(dense);
        RebuildCumulative();
        _baked = _pts.Count >= 2 && _length > 0.01f;

#if UNITY_EDITOR
        // Persist the baked polyline into the scene so the build inherits it.
        if (_baked && !Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif

        Debug.Log("[CameraPathReplay] Baked " + _pts.Count + " samples, " +
                  _length.ToString("F1") + " m, " +
                  (_length / Mathf.Max(0.01f, speed)).ToString("F1") + " s at " +
                  speed.ToString("F2") + " m/s.");

        if (_baked)
        {
            CheckClearance();
            ApplyPose(0f);
        }
        return _baked;
    }

    /// <summary>
    /// Resolves a ground height for every sample.
    ///
    /// The photogrammetry mesh has no notion of "ground" — it is one shell over
    /// streets and buildings alike, so a downward ray crossing a building lands
    /// on its roof. That is what sent the route up a facade. Any sensible route
    /// is mostly at street level, so the median of the hits is a dependable
    /// floor and samples far above it are rooftops. Rejected samples, and ones
    /// where the ray missed entirely, are interpolated from their neighbours —
    /// without that they would keep the 200 m ray-seed height and spike the
    /// route into the sky.
    /// </summary>
    private bool ResolveGroundHeights(List<Vector3> pts)
    {
        int     n  = pts.Count;
        bool[]  ok = new bool[n];
        float[] y  = new float[n];

        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 snapped;
            if (!TrySnapLocalToGround(pts[i], out snapped)) continue;
            y[i]  = snapped.y;
            ok[i] = true;
            hits++;
        }

        if (hits == 0)
        {
            Debug.LogError("[CameraPathReplay] No sample found ground anywhere along the " +
                           "route. Fly the Scene view along it so Cesium streams the tiles, " +
                           "then bake again.");
            return false;
        }

        List<float> sorted = new List<float>(hits);
        for (int i = 0; i < n; i++) if (ok[i]) sorted.Add(y[i]);
        sorted.Sort();
        float median = sorted[sorted.Count / 2];

        int roofs = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            if (y[i] <= median + maxRise && y[i] >= median - maxDrop) continue;
            ok[i] = false;
            roofs++;
        }

        FillGaps(y, ok, median);

        for (int i = 0; i < n; i++)
        {
            Vector3 p = pts[i];
            p.y = y[i];
            pts[i] = p;
        }

        int missed = n - hits;
        if (roofs > 0 || missed > 0)
            Debug.LogWarning("[CameraPathReplay] " + roofs + " rooftop hits rejected, " +
                             missed + " rays missed, out of " + n + " samples " +
                             "(interpolated from neighbours). Median ground " +
                             median.ToString("F2") + " m.");
        return true;
    }

    /// <summary>
    /// Fills every entry where ok[] is false by interpolating between the nearest
    /// valid entries either side. Filled entries stay marked invalid so later
    /// lookups keep reaching real measurements rather than chaining off guesses.
    /// </summary>
    private static void FillGaps(float[] y, bool[] ok, float fallback)
    {
        int n = y.Length;
        for (int i = 0; i < n; i++)
        {
            if (ok[i]) continue;

            int prev = i - 1;
            while (prev >= 0 && !ok[prev]) prev--;
            int next = i + 1;
            while (next < n && !ok[next]) next++;

            if      (prev < 0 && next >= n) y[i] = fallback;
            else if (prev < 0)              y[i] = y[next];
            else if (next >= n)             y[i] = y[prev];
            else y[i] = Mathf.Lerp(y[prev], y[next], (float)(i - prev) / (next - prev));
        }
    }

    /// <summary>
    /// Walks the baked route at head height and reports segments that run into
    /// geometry, so a route passing through a building is caught at bake time
    /// rather than discovered in the headset.
    /// </summary>
    private void CheckClearance()
    {
        if (_pts.Count < 2 || georeference == null) return;

        Transform gt      = georeference.transform;
        int       blocked = 0;
        float     firstAt = -1f;

        for (int i = 1; i < _pts.Count; i++)
        {
            Vector3 a = gt.TransformPoint(_pts[i - 1]) + Vector3.up * headClearanceHeight;
            Vector3 b = gt.TransformPoint(_pts[i])     + Vector3.up * headClearanceHeight;

            Vector3 d   = b - a;
            float   len = d.magnitude;
            if (len < 0.01f) continue;

            RaycastHit[] all = Physics.RaycastAll(new Ray(a, d / len), len,
                                                  ~0, QueryTriggerInteraction.Ignore);
            bool hit = false;
            for (int k = 0; k < all.Length; k++)
                if (all[k].collider is MeshCollider) { hit = true; break; }

            if (!hit) continue;

            blocked++;
            if (firstAt < 0f) firstAt = _cum[i - 1];
        }

        if (blocked > 0)
            Debug.LogWarning("[CameraPathReplay] Route runs into geometry on " + blocked +
                             " of " + (_pts.Count - 1) + " segments, first at " +
                             firstAt.ToString("F1") + " m along the path. Move the waypoints " +
                             "into open ground and bake again.");
        else
            Debug.Log("[CameraPathReplay] Clearance check passed — route is unobstructed at " +
                      headClearanceHeight.ToString("F1") + " m.");
    }

    /// <summary>Places the rig at the route start without moving it.</summary>
    public void ResetToStart()
    {
        _dist = 0f;
        if (_baked) ApplyPose(0f);
    }

    /// <summary>Starts traversal. Bakes first if that has not happened yet.</summary>
    public void Begin()
    {
        if (!_baked && !Bake()) return;

        _dist = 0f;
        _lastTime = 0f;          // first frame contributes no motion
        ApplyPose(0f);
        SetInterferenceEnabled(false);
        _running = true;
    }

    /// <summary>Stops traversal and restores anything that was disabled.</summary>
    public void Stop()
    {
        _running = false;
        SetInterferenceEnabled(true);
    }

    void Update()
    {
        if (!_running || !_baked) return;

        // Real elapsed time, tracked by hand. Time.deltaTime is capped at
        // Time.maximumDeltaTime (0.333 s by default), so below ~3 fps the rig
        // would advance slower than the wall clock and the route would take
        // longer in a slow condition than a fast one — destroying the very
        // comparability that time-parameterised motion exists to provide.
        float now = Time.realtimeSinceStartup;
        float dt  = _lastTime > 0f ? now - _lastTime : 0f;
        _lastTime = now;

        _dist += speed * dt;

        if (_dist >= _length)
        {
            if (loop)
            {
                _dist -= _length;
            }
            else
            {
                _dist = _length;
                ApplyPose(_dist);
                Stop();
                if (OnComplete != null) OnComplete();
                return;
            }
        }

        ApplyPose(_dist);
    }

    private void ApplyPose(float dist)
    {
        if (rig == null || _pts.Count < 2) return;

        int i = FindSegment(dist);
        int j = Mathf.Min(i + 1, _pts.Count - 1);

        float segLen = _cum[j] - _cum[i];
        float u      = segLen > 0.0001f ? (dist - _cum[i]) / segLen : 0f;

        Vector3 local = Vector3.Lerp(_pts[i], _pts[j], u);
        _currentLocal = local;

        // World space, so this works whether or not the rig is parented under
        // the georeference.
        rig.position = georeference.transform.TransformPoint(local);

        if (alignToHeading)
        {
            Vector3 fwd = _pts[j] - _pts[i];
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f)
                rig.rotation = Quaternion.LookRotation(
                    georeference.transform.TransformDirection(fwd.normalized), Vector3.up);
        }
    }

    private void RebuildCumulative()
    {
        _cum.Clear();
        if (_pts.Count == 0) { _length = 0f; return; }

        _cum.Add(0f);
        for (int i = 1; i < _pts.Count; i++)
            _cum.Add(_cum[i - 1] + Vector3.Distance(_pts[i - 1], _pts[i]));

        _length = _cum[_cum.Count - 1];
    }

    private int FindSegment(float dist)
    {
        // Binary search over cumulative distance.
        int lo = 0;
        int hi = _cum.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (_cum[mid] <= dist) lo = mid;
            else                   hi = mid;
        }
        return lo;
    }

    private void SetInterferenceEnabled(bool enabled)
    {
        if (disableDuringRun == null) return;
        for (int i = 0; i < disableDuringRun.Length; i++)
            if (disableDuringRun[i] != null) disableDuringRun[i].enabled = enabled;
    }

    private static void SmoothHeights(List<Vector3> pts, int window)
    {
        int half = Mathf.Max(1, window / 2);
        float[] outY = new float[pts.Count];

        for (int i = 0; i < pts.Count; i++)
        {
            float sum = 0f;
            int   n   = 0;
            for (int k = i - half; k <= i + half; k++)
            {
                if (k < 0 || k >= pts.Count) continue;
                sum += pts[k].y;
                n++;
            }
            outY[i] = sum / n;
        }

        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i];
            p.y = outY[i];
            pts[i] = p;
        }
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * ((2f * p1) +
                       (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>
    /// Converts WGS84 lat/lon to georeference-local space. The 200 m ellipsoidal
    /// height is only a hint that puts the point above this campus (which sits
    /// near 95 m) so the downward ray has somewhere to fall from.
    /// </summary>
    private Vector3 LatLonToLocal(double lat, double lon)
    {
        double3 ecef = CesiumWgs84Ellipsoid
            .LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(lon, lat, 200.0));
        double3 u = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)u.x, (float)u.y, (float)u.z);
    }

    /// <summary>
    /// Raycasts a georeference-local point down onto the loaded Cesium meshes.
    /// Only MeshCollider hits count: Cesium builds one per streamed tile, whereas
    /// the BoxColliders in this scene are proxies (the tileset root carries one
    /// spanning the whole photogrammetry area, and the boundary walls carry more)
    /// that would otherwise swallow the ray far above the terrain.
    /// </summary>
    private bool TrySnapLocalToGround(Vector3 local, out Vector3 snappedLocal)
    {
        snappedLocal = local;
        if (georeference == null) return false;

        Transform gt    = georeference.transform;
        Vector3   world = gt.TransformPoint(local);
        Ray       ray   = new Ray(world + Vector3.up * raycastStartHeight, Vector3.down);

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

    void OnDrawGizmosSelected()
    {
        if (_pts.Count < 2 || georeference == null) return;

        Transform gt = georeference.transform;

        Gizmos.color = Color.cyan;
        for (int i = 1; i < _pts.Count; i++)
            Gizmos.DrawLine(gt.TransformPoint(_pts[i - 1]), gt.TransformPoint(_pts[i]));

        // Endpoints, so the route direction is readable at a glance.
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(gt.TransformPoint(_pts[0]), 2f);
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(gt.TransformPoint(_pts[_pts.Count - 1]), 2f);
    }
}
