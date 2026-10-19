using System.Collections.Generic;
using UnityEngine;

public class Waypoint : MonoBehaviour
{
    public List<Waypoint> neighbors = new List<Waypoint>();

    public float temperature;

    public float gCost = float.MaxValue;
    public float hCost;
    public float fCost => gCost + hCost;

    public Waypoint parent;

    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;

        foreach (var n in neighbors)
        {
            if (n != null)
                Gizmos.DrawLine(transform.position, n.transform.position);
        }

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(transform.position, 0.3f);
    }
}