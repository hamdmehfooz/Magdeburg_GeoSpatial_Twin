# Draft text — revision 2

> Supersedes `Results/`. Three things changed from revision 1:
> the DGM offset is NOT a level-of-detail artefact; vertical and horizontal
> alignment behave very differently; and the roof sample now covers 13 of 14 sites.

---

## Additional methods

**Screen-space error.** The terrain tilesets were re-measured with
`maximumScreenSpaceError` reduced from 16 to 1, increasing the served geometry
from approximately 800 tiles to 120,181 tiles (16.1 million vertices) across the
four terrain assets.

**Terrain tile attribution.** Each 2 km DGM tile is published by Cesium ion as a
globally-extending terrain tileset with low-resolution fallback geometry outside
its data footprint, so four terrain surfaces exist beneath every checkpoint.
Residuals were therefore computed exclusively against the tile whose UTM
footprint contains the checkpoint, identified from the tile identifier.
Sampling the topmost of all four surfaces indiscriminately yields a spurious
result (mean -1.795 m, SD 0.968 m) that describes the fallback terrain rather
than the DGM.

**Roof assessment.** For each checkpoint the nearest building offering a
reachable roof was identified by scanning all bearings at 6 m elevation. Roof
heights were then sampled on a grid stepped 4-14 m inward from the facade and
+/-6 m laterally. At each sample a vertical ray recorded the uppermost LoD2 and
photogrammetric surfaces; samples were retained only where both lay more than
3 m above local ground.

At three checkpoints (5, 6 and 7) the closest surface is a low structure without
a usable roof, so the roof measurement was taken on the nearest genuine building
instead — a different target from the facade measurement at those sites, as
noted in Table 1. At GCP 6 that building lies 84 m away, well beyond the 8.7-34 m
range of all other sites; it is therefore excluded from the primary roof sample.
Including it changes the mean by 3 mm and alters no conclusion.

---

## Results

### Vertical accuracy of the photogrammetric model

RMSE 0.106 m against 13 independent checkpoints, mean +0.055 m, SD 0.094 m.

### Terrain model

Reducing the screen-space error threshold from 16 to 1 changed the residuals
only marginally (RMSE 1.972 m to 2.022 m under the naive sampling scheme),
demonstrating that the discrepancy is not a level-of-detail effect.

Computed against the correct owning tile, the DGM exhibits a **constant vertical
offset of -3.281 m with a standard deviation of only 0.028 m** across all
fourteen points. The internal consistency of the terrain model is therefore
better than 3 cm, and the entire discrepancy is a systematic datum offset rather
than random error. It is correctable by a single translation.

### Building models

Horizontal and vertical alignment behave very differently, and this distinction
is the principal finding.

**Horizontally**, LoD2 facades agree with the photogrammetric facades to
RMSE 0.167 m with a mean offset of +0.004 m across eleven buildings. No
systematic planimetric shift is detectable.

**Vertically**, LoD2 roofs sit **3.678 m below** the photogrammetric roofs
(SD 0.533 m, RMSE 3.713 m, n = 13). This is of the same order as the terrain
offset (-3.281 m), and both datasets originate from the same national open-data
infrastructure. Agreement between two independently derived offsets indicates a
common vertical reference inconsistency in the open-data ingestion path rather
than an error specific to either product.

### Interpretation

The environment can be extended beyond the photogrammetric footprint using open
CityGML and DGM data without introducing planimetric discontinuity: horizontal
agreement is at the decimetre level and unbiased, which is the condition
required for seamless lateral extension.

Vertical continuity requires an explicit correction of approximately 3.3 m
applied to both open datasets. Because the offset is systematic and, for the
terrain model, constant to within 3 cm, this correction is straightforward and
does not compromise the internal geometry of either dataset.

---

## Limitations

The cause of the ~3.3 m vertical offset has not been established here. It is far
smaller than the geoid undulation for this region (approximately 45 m), so it
does not correspond to a simple orthometric-to-ellipsoidal confusion.
Determining its origin would require examining the vertical reference declared
during asset ingestion, and is identified as future work.

Facade measurements assess horizontal offset perpendicular to building walls;
roof measurements assess vertical offset only. Neither addresses rotational
misalignment, which would require correspondence between identifiable features
rather than surface sampling.

At GCP 5 and 7 the facade and roof measurements were taken on different
structures, because the nearest surface at those sites has no usable roof. The
two measurements at those checkpoints should therefore not be interpreted as
describing the same building.

Whether the GCPs were used as control during photogrammetric bundle adjustment
or withheld as checkpoints determines whether the photogrammetric vertical
figures represent independent accuracy or fitting residuals. [STATE WHICH APPLIES.]
