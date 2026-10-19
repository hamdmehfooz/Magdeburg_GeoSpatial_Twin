using UnityEngine;
using Debug = UnityEngine.Debug;

public class WaypointDrawer : MonoBehaviour
{
    public GameObject waypointPrefab;

    void Update()
    {
        // SHIFT + LEFT CLICK → CREATE NODE
        if (Input.GetMouseButtonDown(0) && Input.GetKey(KeyCode.LeftShift))
        {
            if (Camera.main == null) return;

            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                GameObject obj = Instantiate(waypointPrefab, hit.point, Quaternion.identity);

                Waypoint wp = obj.GetComponent<Waypoint>();

                if (CoolWaypointPathfinder.Instance != null)
                {
                    CoolWaypointPathfinder.Instance.AddWaypoint(wp);
                    Debug.Log("Waypoint created and registered");
                }
                else
                {
                    Debug.LogError("Pathfinder Instance is NULL!");
                }
            }
        }
    }
}