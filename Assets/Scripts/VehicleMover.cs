using System.Collections.Generic;
using UnityEngine;

using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

/// <summary>
/// Moves a vehicle (bus or tram) along a pre-computed world-space path.
/// Pauses at stops and yields to vehicles directly ahead on the same route.
/// </summary>
public class VehicleMover : MonoBehaviour
{
    // ── shared registry so vehicles can see each other ──────────────────────
    private static readonly List<VehicleMover> _allVehicles = new();

    // ── route data ───────────────────────────────────────────────────────────
    private List<Vector3> worldPath;
    private List<Vector3> stopWorldPositions;   // cached – no per-frame raycasts
    private List<string>  stopNames;
    private TransitSimulationManager manager;
    private bool isTram;

    // ── movement state ───────────────────────────────────────────────────────
    private int   pathIndex  = 0;
    private float speed      = 15f;
    private float baseSpeed  = 15f;

    private bool  isWaiting  = false;
    private float waitTimer  = 0f;

    // ── stop state ───────────────────────────────────────────────────────────
    // Cooldown prevents the same stop from re-triggering while the vehicle
    // is still within the detection radius after leaving.
    private float _stopCooldown   = 0f;
    private const float STOP_COOLDOWN  = 12f;   // seconds before a stop can fire again
    private const float STOP_RADIUS    = 18f;   // metres (XZ) to trigger a stop

    // ── collision avoidance ──────────────────────────────────────────────────
    private const float LOOKAHEAD_DIST  = 25f;   // metres ahead to start slowing
    private const float STOP_DIST       = 7f;    // metres ahead to fully stop
    private const float LOOKAHEAD_CONE  = 0.70f; // position cone  (~45°)
    private const float SAME_DIR_DOT    = 0.50f; // direction match (~60°) — ignore crossing vehicles

    // ── ground snap ──────────────────────────────────────────────────────────
    private const float GROUND_SNAP_HEIGHT = 1.5f;
    private float       _lastSnapLog       = 0f;
    private Collider[]  _ownColliders;

    // ── waypoint advance ─────────────────────────────────────────────────────
    private const float WAYPOINT_THRESHOLD = 1.5f;

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        _ownColliders = GetComponentsInChildren<Collider>();
    }

    void OnEnable()  => _allVehicles.Add(this);
    void OnDisable() => _allVehicles.Remove(this);

    // ── public initialiser ───────────────────────────────────────────────────

    public void Init(Track track, List<Stop> stops,
                     TransitSimulationManager mgr, bool isTramVehicle)
    {
        manager   = mgr;
        isTram    = isTramVehicle;
        baseSpeed = isTram ? 12f : 18f;
        speed     = baseSpeed;

        // Build world-space path
        worldPath = new List<Vector3>(track.positions.Count);
        foreach (var p in track.positions)
            worldPath.Add(manager.LatLonToUnity(p[0], p[1]));

        // Remove duplicate consecutive points
        for (int i = worldPath.Count - 1; i > 0; i--)
            if (XZDistance(worldPath[i], worldPath[i - 1]) < 0.1f)
                worldPath.RemoveAt(i);

        if (worldPath.Count == 0) return;

        // FIX: Cache stop world positions once at init so Update() never
        // needs to call LatLonToUnity (and its embedded raycast) per frame.
        stopWorldPositions = new List<Vector3>();
        stopNames          = new List<string>();
        if (stops != null)
        {
            foreach (var s in stops)
            {
                stopWorldPositions.Add(manager.LatLonToUnity(s.lat, s.lon));
                stopNames.Add(s.name);
            }
        }

        // Stagger start position so vehicles spread along the route
        pathIndex          = Random.Range(0, worldPath.Count);
        transform.position = worldPath[pathIndex];

        Debug.Log($"{name} init: {worldPath.Count} waypoints, " +
                  $"{stopWorldPositions.Count} stops, start index {pathIndex}.");
    }

    // ── per-frame update ─────────────────────────────────────────────────────

    void Update()
    {
        if (worldPath == null || worldPath.Count < 2) return;

        // Count down stop wait
        if (isWaiting)
        {
            waitTimer -= Time.deltaTime;
            if (waitTimer <= 0f) isWaiting = false;
            return;
        }

        // Count down stop cooldown
        if (_stopCooldown > 0f) _stopCooldown -= Time.deltaTime;

        // FIX: Check stop proximity every frame (not only at waypoint
        // crossings). Stops in GTFS order rarely align with waypoint indices,
        // so checking only at waypoints missed most stops entirely.
        CheckStopProximity();
        if (isWaiting) return;

        // Collision avoidance — gradually slow down or stop based on distance
        // to the nearest same-direction vehicle ahead.
        speed = baseSpeed * GetSpeedMultiplier();
        if (speed < 0.01f) return;

        // XZ-only target: let SnapToGround own the Y axis entirely.
        Vector3 raw    = worldPath[pathIndex];
        Vector3 target = new Vector3(raw.x, transform.position.y, raw.z);

        transform.position = Vector3.MoveTowards(
            transform.position, target, speed * Time.deltaTime);

        // Rotate to face direction of travel (XZ only)
        Vector3 dir = target - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir);
            if (name.Contains("Tram"))
                targetRot *= Quaternion.Euler(0f, 90f, 0f);

            transform.rotation = Quaternion.Slerp(
                transform.rotation, targetRot, Time.deltaTime * 6f);
        }

        // Advance waypoint using XZ distance only
        if (XZDistance(transform.position, target) < WAYPOINT_THRESHOLD)
        {
            pathIndex++;
            if (pathIndex >= worldPath.Count)
            {
                worldPath.Reverse();
                stopWorldPositions?.Reverse();
                stopNames?.Reverse();
                pathIndex = 1;
            }
        }

        SnapToGround();
    }

    // ── stop detection ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks every cached stop position each frame.
    /// Uses a cooldown so each stop triggers at most once per pass-through.
    /// </summary>
    void CheckStopProximity()
    {
        if (stopWorldPositions == null || stopWorldPositions.Count == 0) return;
        if (_stopCooldown > 0f) return;

        for (int i = 0; i < stopWorldPositions.Count; i++)
        {
            if (XZDistance(transform.position, stopWorldPositions[i]) < STOP_RADIUS)
            {
                isWaiting    = true;
                waitTimer    = Random.Range(2.5f, 4.5f);
                _stopCooldown = STOP_COOLDOWN;
                Debug.Log($"{name} stopping at: {stopNames[i]}");
                return;
            }
        }
    }

    // ── collision avoidance ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a 0–1 speed multiplier based on the closest same-direction
    /// vehicle ahead. Smoothly decelerates from LOOKAHEAD_DIST down to a
    /// full stop at STOP_DIST. Vehicles on crossing routes are ignored to
    /// prevent deadlocks at intersections.
    /// </summary>
    float GetSpeedMultiplier()
    {
        Vector3 myPos     = transform.position;
        Vector3 myForward = transform.forward;
        myForward.y = 0f;
        if (myForward.sqrMagnitude < 0.001f) return 1f;
        myForward.Normalize();

        float closest = float.MaxValue;

        foreach (var other in _allVehicles)
        {
            if (other == this || other == null) continue;

            float dist = XZDistance(myPos, other.transform.position);
            if (dist >= LOOKAHEAD_DIST || dist < 0.3f) continue;

            // Is the other vehicle in our forward cone?
            Vector3 toOther = other.transform.position - myPos;
            toOther.y = 0f;
            if (toOther.sqrMagnitude < 0.001f) continue;
            if (Vector3.Dot(myForward, toOther.normalized) < LOOKAHEAD_CONE) continue;

            // Same-direction check: ignore vehicles travelling perpendicular
            // or opposite (crossing trams, oncoming buses) to avoid deadlocks.
            Vector3 otherFwd = other.transform.forward;
            otherFwd.y = 0f;
            if (otherFwd.sqrMagnitude > 0.001f &&
                Vector3.Dot(myForward, otherFwd.normalized) < SAME_DIR_DOT)
                continue;

            if (dist < closest) closest = dist;
        }

        if (closest == float.MaxValue) return 1f;                           // nothing ahead
        if (closest <= STOP_DIST)      return 0f;                           // full stop
        return Mathf.InverseLerp(STOP_DIST, LOOKAHEAD_DIST, closest);      // gradual slowdown
    }

    // ── terrain snap ─────────────────────────────────────────────────────────

    void SnapToGround()
    {
        foreach (var c in _ownColliders) if (c) c.enabled = false;

        Ray ray = new Ray(transform.position + Vector3.up * 200f, Vector3.down);
        bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, 500f,
                                   ~0, QueryTriggerInteraction.Ignore);

        foreach (var c in _ownColliders) if (c) c.enabled = true;

        if (Time.time - _lastSnapLog > 1f)
        {
            _lastSnapLog = Time.time;
            if (hit) Debug.Log($"{name} snap HIT '{hitInfo.collider.name}' y={hitInfo.point.y:F1}");
            else     Debug.LogWarning($"{name} snap MISS at {transform.position}");
        }

        if (hit)
        {
            Vector3 p = transform.position;
            p.y = hitInfo.point.y + GROUND_SNAP_HEIGHT;
            transform.position = p;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static float XZDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
