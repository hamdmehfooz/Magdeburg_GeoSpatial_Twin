using System;
using System.Collections.Generic;

[Serializable]
public class Track
{
    public long id;
    public string type;      // "bus", "tram", "train"
    // Each entry is [latitude, longitude]
    public List<List<double>> positions;
}

[Serializable]
public class Stop
{
    public string id;
    public string name;
    public double lat;
    public double lon;
}

[Serializable]
public class Route
{
    public string route_id;
    public int route_type;
}

[Serializable]
public class Trip
{
    public string route_id;
    public string trip_id;
    public string shape_id;
}

[Serializable]
public class ShapePoint
{
    public string shape_id;
    public double lat;
    public double lon;
    public int sequence;
}