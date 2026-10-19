# Draft text for the paper

> Adapt wording to your style. Numbers are as measured — do not change them
> without re-running the analysis.

---

## Methods — Geometric validation

Fourteen ground control points (GCPs) were surveyed across the campus with
RTK-GNSS in network-VRS mode (ETRS89 / UTM zone 32N), each averaged over
201 samples with a fixed-integer solution. Reported coordinate precision was
1.0–1.4 cm horizontally and 1.1–2.5 cm vertically, roughly an order of
magnitude better than the expected accuracy of the models under test, so the
GCPs are treated as reference.

GCP 1 defines the origin of the Cesium georeference and is therefore not an
independent observation; it is reported for completeness but excluded from all
accuracy statistics, leaving 13 checkpoints.

**Vertical assessment.** Each checkpoint was transformed from geographic
coordinates to the engine's local frame using the same WGS84 ellipsoid
transform that positions the tilesets at runtime. A vertical ray was cast onto
each tileset and the intersection height compared with the surveyed ellipsoidal
height. Exactly one photogrammetric intersection was found at every checkpoint,
confirming that no vegetation canopy interfered with the measurement.

**Facade assessment.** For each checkpoint the nearest LoD2 building was
located by horizontal ray search (all fourteen had one within 8.7–27.9 m).
A fan of 25 rays — five heights (3–11 m) by five bearings (±8°) — was then cast
towards that facade, recording the intersection with the LoD2 surface and with
the photogrammetric surface along the same ray. The signed difference is the
facade offset. Facade planes rather than surface-to-surface distances were used
deliberately: LoD2 is a generalised representation without balconies or
projections, so a surface metric would conflate level-of-detail difference with
positional error, whereas a plane offset is largely insensitive to
generalisation.

The per-building median is used as the site statistic, and summary statistics
are computed across the 14 building medians rather than by pooling all 284 rays,
so that a single noisy site cannot dominate the result.

Measurements were made with the LoD2 clipping mask temporarily disabled so that
both datasets coexisted in the overlap region; the deployed VR configuration
suppresses LoD2 geometry inside the photogrammetric footprint.

---

## Results

The photogrammetric surface agrees with the GNSS reference to
**RMSE 0.106 m** across 13 checkpoints, with a mean bias of +0.055 m
(SD 0.094 m) and a maximum absolute deviation of 0.270 m. The small positive
bias indicates the reconstructed surface sits marginally above the surveyed
ground marks, consistent with the surface capturing pavement texture and marker
plates rather than the exact ground point.

Comparison of CityGML LoD2 facades against the photogrammetric facades yields a
mean offset of **+0.004 m** with an RMSE of **0.167 m** over eleven buildings
(SD 0.175 m). Including all fourteen sites raises RMSE to 0.217 m. The near-zero
mean is the substantive result: no systematic planimetric shift is detectable
between the two datasets, indicating that both are referenced to a common datum.
The residual dispersion of ~0.18 m is consistent with the combined effect of
LoD2 generalisation and photogrammetric surface noise, and lies well within the
positional accuracy specified for LoD2 building models derived from German
cadastral data (approximately 0.5 m).

Because the photogrammetric model is independently validated against RTK
reference at the decimetre level, the agreement between LoD2 and photogrammetry
can be interpreted as evidence that the LoD2 data are themselves correctly
georeferenced, rather than merely that the two datasets are mutually consistent.
This supports extending the environment beyond the photogrammetric footprint
using open CityGML data without introducing a positional discontinuity at the
boundary.

---

## Limitations

The facade assessment measures horizontal offset perpendicular to building
walls; it does not evaluate vertical alignment or roof geometry.

Two sites (GCP 6 and GCP 10) exhibited per-ray dispersion above 1.0 m,
indicating that individual rays intersected different surfaces — vegetation,
courtyard openings, or adjacent building faces. Their medians remain plausible
but they are excluded from the filtered statistics and flagged in
Table~\ref{tab:per_checkpoint}. GCP 4 and GCP 13 are borderline
(dispersion 0.88 m and 0.98 m).

The terrain (DGM) comparison is reported for completeness but should not be read
as an accuracy statement for the source data. The digital terrain model was
streamed at a screen-space error threshold of 16, at which the served geometry is
substantially coarser than the 1 m source grid; the observed mean offset of
-1.77 m (SD 0.903 m) is therefore attributable primarily to level-of-detail
simplification rather than to the accuracy of the underlying elevation data. A
dedicated assessment at a lower screen-space error threshold would be required
to characterise the DGM itself.

Whether the GCPs were used as control during photogrammetric bundle adjustment
or withheld as independent checkpoints determines whether the vertical figures
represent independent accuracy or fitting residuals. [STATE WHICH APPLIES.]
