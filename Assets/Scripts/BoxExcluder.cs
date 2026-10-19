using CesiumForUnity;
using UnityEngine;

/// <summary>
/// Excludes Cesium 3D Tiles based on a box region.
///
///   invert = false : exclude tiles INSIDE the box   (cut a hole)
///   invert = true  : exclude tiles OUTSIDE the box  (keep only the box)
///
/// Must live on the SAME GameObject as the Cesium3DTileset — Cesium finds
/// excluders via GetComponentsInParent, so putting it on a shared ancestor
/// would apply it to every tileset in the scene.
/// </summary>
public class BoxExcluder : CesiumTileExcluder
{
    [Tooltip("Box defining the region. Drag a GameObject with a BoxCollider.")]
    public BoxCollider region;

    [Tooltip("false = exclude tiles inside the box. true = keep only tiles inside the box.")]
    public bool invert = false;

    public override bool ShouldExclude(Cesium3DTile tile)
    {
        if (region == null) return false;

        // Intersects, not Contains: Cesium traverses a tile tree, so coarse
        // ancestor tiles must survive or their high-detail children never load.
        bool inside = region.bounds.Intersects(tile.bounds);
        return invert ? !inside : inside;
    }
}
