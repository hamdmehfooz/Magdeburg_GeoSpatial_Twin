using System.Collections.Generic;
using UnityEngine;
using System.IO;


using Application = UnityEngine.Application;
using Debug = UnityEngine.Debug;

public class CoolWaypointPathfinder : MonoBehaviour
{
    public static CoolWaypointPathfinder Instance;

    [Header("References")]
    public CTI.CTI_WeatherWindManager weatherManager;
    public Transform player;

    [Header("Waypoints")]
    public List<Waypoint> waypoints = new List<Waypoint>();

    [Header("Movement")]
    public float moveSpeed = 5f;

    [Header("Auto Connect")]
    public float connectionDistance = 6f;

    private List<Waypoint> currentPath;
    private int pathIndex = 0;

    void Awake()
    {
        Instance = this; // 
    }

    void Update()
    {
        HandleClick();
        MovePlayer();

        if (Input.GetKeyDown(KeyCode.C))
        {
            AutoConnectNodes();
        }

        if (Input.GetKeyDown(KeyCode.S))
        {
            SaveGraph();
        }
    }

    // ================= ADD WAYPOINT =================

    public void AddWaypoint(Waypoint wp)
    {
        if (wp == null) return;

        if (!waypoints.Contains(wp))
        {
            waypoints.Add(wp);
            Debug.Log("Waypoint added. Total: " + waypoints.Count);
        }
    }

    // ================= AUTO CONNECT =================

    public void AutoConnectNodes()
    {
        foreach (var wp in waypoints)
        {
            wp.neighbors.Clear();

            foreach (var other in waypoints)
            {
                if (wp == other) continue;

                float dist = Vector3.Distance(wp.transform.position, other.transform.position);

                if (dist < connectionDistance)
                {
                    Vector3 dir = (other.transform.position - wp.transform.position).normalized;

                    if (!Physics.Raycast(wp.transform.position + Vector3.up, dir, dist))
                    {
                        wp.neighbors.Add(other);
                    }
                }
            }
        }

        Debug.Log("Auto connections created!");
    }

    // ================= CLICK (NAVIGATION ONLY) =================

    void HandleClick()
    {
        
        if (Input.GetKey(KeyCode.LeftShift)) return;

        if (Input.GetMouseButtonDown(0))
        {
            if (waypoints.Count == 0)
            {
                Debug.LogError("No waypoints available!");
                return;
            }

            if (Camera.main == null) return;

            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                Waypoint start = FindClosest(player.position);
                Waypoint end = FindClosest(hit.point);

                if (start == null || end == null)
                {
                    Debug.LogError("Start or End waypoint is NULL!");
                    return;
                }

                currentPath = FindCoolestPath(start, end);

                if (currentPath != null && currentPath.Count > 0)
                {
                    pathIndex = 0;
                    Debug.Log("Path generated!");
                }
                else
                {
                    Debug.LogWarning("No path found!");
                }
            }
        }
    }

    // ================= FIND CLOSEST =================

    Waypoint FindClosest(Vector3 pos)
    {
        Waypoint closest = null;
        float minDist = float.MaxValue;

        foreach (var wp in waypoints)
        {
            if (wp == null) continue;

            float dist = Vector3.Distance(pos, wp.transform.position);

            if (dist < minDist)
            {
                minDist = dist;
                closest = wp;
            }
        }

        return closest;
    }

    // ================= A* algorithm =================

    List<Waypoint> FindCoolestPath(Waypoint start, Waypoint end)
    {
        List<Waypoint> open = new List<Waypoint>();
        HashSet<Waypoint> closed = new HashSet<Waypoint>();

        foreach (var wp in waypoints)
        {
            wp.gCost = float.MaxValue;
            wp.parent = null;
        }

        start.gCost = 0;
        open.Add(start);

        while (open.Count > 0)
        {
            Waypoint current = open[0];

            foreach (var wp in open)
            {
                if (wp.fCost < current.fCost)
                    current = wp;
            }

            open.Remove(current);
            closed.Add(current);

            if (current == end)
                return RetracePath(start, end);

            foreach (var neighbor in current.neighbors)
            {
                if (neighbor == null || closed.Contains(neighbor))
                    continue;

                float shadow = weatherManager.GetShadowFactor(
                    neighbor.transform.position + Vector3.up * 1.5f
                );

                neighbor.temperature = Mathf.Lerp(40f, 20f, shadow);

                float newCost = current.gCost + neighbor.temperature;

                if (newCost < neighbor.gCost)
                {
                    neighbor.gCost = newCost;
                    neighbor.hCost = Vector3.Distance(
                        neighbor.transform.position,
                        end.transform.position
                    );

                    neighbor.parent = current;

                    if (!open.Contains(neighbor))
                        open.Add(neighbor);
                }
            }
        }

        return null;
    }

    List<Waypoint> RetracePath(Waypoint start, Waypoint end)
    {
        List<Waypoint> path = new List<Waypoint>();
        Waypoint current = end;

        while (current != start)
        {
            path.Add(current);
            current = current.parent;
        }

        path.Reverse();
        return path;
    }

    // ================= MOVEMENT =================

    void MovePlayer()
    {
        if (currentPath == null || pathIndex >= currentPath.Count)
            return;

        Vector3 target = currentPath[pathIndex].transform.position;

        player.position = Vector3.MoveTowards(
            player.position,
            target,
            moveSpeed * Time.deltaTime
        );

        if (Vector3.Distance(player.position, target) < 0.2f)
        {
            pathIndex++;
        }
    }

    // ================= SAVE =================

    [System.Serializable]
    public class WaypointData
    {
        public Vector3 position;
        public List<int> neighbors = new List<int>();
    }

    [System.Serializable]
    public class GraphData
    {
        public List<WaypointData> nodes = new List<WaypointData>();
    }

    void SaveGraph()
    {
        GraphData data = new GraphData();

        for (int i = 0; i < waypoints.Count; i++)
        {
            Waypoint wp = waypoints[i];

            WaypointData node = new WaypointData();
            node.position = wp.transform.position;

            foreach (var n in wp.neighbors)
            {
                int index = waypoints.IndexOf(n);
                if (index >= 0)
                    node.neighbors.Add(index);
            }

            data.nodes.Add(node);
        }

        string json = JsonUtility.ToJson(data, true);

        string path = Application.persistentDataPath + "/waypoints.json";

        File.WriteAllText(path, json);

        Debug.Log("Graph saved at: " + path);
    }
}