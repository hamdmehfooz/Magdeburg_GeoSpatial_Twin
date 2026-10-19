================================================================================
GEOMETRIC ASSESSMENT OF A FUSED PHOTOGRAMMETRIC / OPEN-DATA CAMPUS MODEL
Methods, formulae and results  --  uniform 11-site analysis
================================================================================

Site        : Otto von Guericke University campus, Magdeburg, Germany
Engine      : Unity 6000.1.5f1, Cesium for Unity
Reference   : RTK-GNSS ground control, ETRS89 / UTM zone 32N
Survey date : 2024-12-01

CONTROL SITES : GCP 1, 2, 3, 4, 8, 9, 10, 11, 12, 13, 14   (n = 11)

All four assessments reported below are computed over this identical set of
eleven sites, so every statistic refers to the same locations and the sample
size is uniform throughout.

--------------------------------------------------------------------------------
1. REFERENCE DATA AND SITE SELECTION
--------------------------------------------------------------------------------

Ground control was observed with network-RTK (VRS) GNSS in ETRS89 / UTM zone
32N. Each point was averaged over 201 epochs with a fixed integer ambiguity
solution. Reported coordinate precision:

    horizontal RMS : 0.010 - 0.014 m
    vertical   RMS : 0.011 - 0.025 m
    PDOP           : 1.3 - 3.4

This is approximately one order of magnitude better than the expected accuracy
of the models under test, which justifies treating the GNSS coordinates as
reference rather than as a second measurement of comparable quality.

Site selection. Fourteen points were surveyed across the campus. Eleven were
retained for the geometric assessment. Three sites were excluded because no
building suitable for the comparison lay within the sampling radius: at those
locations the nearest surface is a low structure without a usable roof, and the
nearest genuine building is either unreachable by the sampling scheme or
substantially more distant than at any retained site. The exclusion was decided
on site geometry alone, before any residuals were computed, and the same eleven
sites are used for every assessment.

Georeference origin. GCP 1 also defines the origin of the engine georeference:

    georeference origin : 52.13958956 N, 11.64030482 E, h = 96.2461 m
    GCP 1               : 52.13958961 N, 11.64030469 E, h = 96.240  m

The two agree at the centimetre level. GCP 1 is retained in all statistics for
consistency of sample; its influence is quantified in Section 5.3 and is
immaterial.

--------------------------------------------------------------------------------
2. COORDINATE FRAMEWORK
--------------------------------------------------------------------------------

2.1 Geodetic to Earth-Centred Earth-Fixed (ECEF)

For geodetic latitude phi, longitude lambda and ellipsoidal height h on the
WGS84 ellipsoid:

    a  = 6 378 137.0 m                (semi-major axis)
    f  = 1 / 298.257223563            (flattening)
    e2 = 2f - f^2                     (first eccentricity squared)

    N(phi) = a / sqrt( 1 - e2 * sin^2(phi) )          (prime vertical radius)

    X = ( N(phi) + h ) * cos(phi) * cos(lambda)
    Y = ( N(phi) + h ) * cos(phi) * sin(lambda)
    Z = ( N(phi) * (1 - e2) + h ) * sin(phi)

2.2 ECEF to engine local frame

A left-handed East-Up-North frame is defined tangent to the ellipsoid at the
georeference origin. A point maps as

    p_local = R_EUN * ( X_ecef - X_origin )

with R_EUN the rotation from ECEF to the local tangent basis and X_origin the
ECEF position of the georeference origin, so that

    p_local.x -> East,   p_local.y -> Up,   p_local.z -> North   (metres)

The frame is metric and locally Cartesian, so residuals expressed in local
coordinates are directly interpretable as metric distances; no scaling is
applied.

2.3 Inverse UTM (terrain tile attribution only)

Terrain tiles are identified by their south-west corner in UTM zone 32N. Tile
corners were converted to geodetic coordinates using the standard inverse
transverse Mercator series (k0 = 0.9996, lambda_0 = 9 deg E) and then to the
local frame, in order to determine which tile geographically contains each site.

--------------------------------------------------------------------------------
3. MEASUREMENT PROCEDURES
--------------------------------------------------------------------------------

Surfaces are interrogated by ray casting against the collision meshes generated
by the tileset renderer. A ray is

    r(t) = o + t * d ,    t in [0, T] ,    |d| = 1

and the reported intersection is the smallest t at which the ray meets a
triangle of the target surface. Only mesh colliders are accepted; axis-aligned
proxy volumes attached to tileset roots are rejected, since they do not
represent surface geometry.

3.1 Vertical residuals against the GNSS reference

For site i with local coordinates p_i = (x_i, y_i, z_i), y_i being the surveyed
height, a vertical ray is cast downward from a height offset H:

    o = p_i + (0, H, 0) ,    d = (0, -1, 0) ,    H = 60 m

The residual is

    dZ_i = y_model,i - y_i

Positive values indicate that the modelled surface lies above the surveyed
point. Exactly one photogrammetric intersection was obtained at every site,
confirming that no vegetation canopy intervened.

Terrain attribution. Each 2 km terrain tile is published as a globally
extending tileset carrying low-resolution fallback geometry outside its own data
footprint, so several terrain surfaces exist beneath every site. The residual is
computed exclusively against the tile whose UTM footprint contains the site.
Taking instead the uppermost of all overlapping terrain surfaces yields
-1.795 m with SD 0.968 m, which characterises the fallback terrain and not the
terrain model under test.

The terrain assessment was repeated with the tileset screen-space error
threshold reduced from 16 to 1, increasing the served geometry from
approximately 800 to 120 181 tiles (16.1 million vertices). The residuals were
unchanged, confirming that the offset reported in Section 5 is not a
level-of-detail artefact.

3.2 Horizontal facade offsets (LoD2 versus photogrammetry)

At each site the nearest LoD2 building is located by casting horizontal rays
over all bearings at 6 m elevation. A fan of rays is then cast towards that
facade:

    heights  : 3, 5, 7, 9, 11 m above the site
    bearings : theta_0 + { -8, -4, 0, +4, +8 } degrees
    => 25 rays per site

For ray j at site i, let t_LoD2 and t_photo be the ray parameters at first
intersection with the LoD2 and photogrammetric surfaces. The signed facade
offset is

    delta_ij = t_photo,ij - t_LoD2,ij

Positive values indicate that the photogrammetric surface lies further from the
site than the LoD2 wall. Rays with |delta_ij| > 15 m are discarded as having
intersected different structures.

Because both intersections are taken along a single shared ray, delta is the
perpendicular separation of the two facade surfaces along that direction and is
independent of the position of the ray origin.

Rationale for a plane-based metric. LoD2 is a generalised representation:
extruded cadastral footprints with idealised roof forms, lacking balconies,
projections and facade relief. A surface-to-surface metric such as Hausdorff or
cloud-to-mesh distance would conflate this level-of-detail difference with
positional error. The offset along a shared ray is governed by the relative
position of the two facade planes and is only weakly sensitive to
generalisation, so it isolates the quantity of interest.

3.3 Vertical roof differences (LoD2 versus photogrammetry)

Roof samples are taken on a grid stepped inward from the facade:

    inward offsets  : 4, 7, 10, 14 m beyond the facade intersection
    lateral offsets : -6, -3, 0, +3, +6 m perpendicular to the ray
    => up to 20 samples per site

At each sample a vertical ray records the uppermost LoD2 and photogrammetric
intersections h_LoD2 and h_photo. The roof difference is

    dH_ij = h_LoD2,ij - h_photo,ij

Samples are retained only where both surfaces lie more than 3 m above local
ground, which excludes ground and low-vegetation intersections; samples with
|dH_ij| > 12 m are discarded as gross mismatches.

--------------------------------------------------------------------------------
4. STATISTICAL TREATMENT
--------------------------------------------------------------------------------

4.1 Two-level aggregation

Rays within a site are not independent observations: they interrogate the same
pair of surfaces and share systematic error. Pooling all rays would constitute
pseudo-replication and would allow a single noisy site to dominate the result.
A robust site statistic is therefore formed first,

    x_i = median_j ( measurement_ij )

and summary statistics are computed across the m = 11 site values. The median is
used at site level because photogrammetric outliers are strongly one-sided:
vegetation and clutter occlude a surface but never recede behind it.

4.2 Summary statistics

For site values x_1 ... x_m:

    mean        mu   = (1/m) * sum_i x_i
    sample SD   s    = sqrt( (1/(m-1)) * sum_i (x_i - mu)^2 )
    RMSE        rmse = sqrt( (1/m) * sum_i x_i^2 )

RMSE is computed about zero rather than about the mean: it expresses total
departure from the reference and is therefore an accuracy measure. Bias and
dispersion combine exactly as

    rmse^2 = mu^2 + ((m-1)/m) * s^2

This identity was verified for all four assessments; reconstructed values agree
with the directly computed RMSE to four decimal places:

    photogrammetry vertical : rmse 0.1106   reconstructed 0.1106
    terrain vertical        : rmse 3.2729   reconstructed 3.2729
    LoD2 facade             : rmse 0.2363   reconstructed 0.2363
    LoD2 roof               : rmse 3.6610   reconstructed 3.6610

Separating mu from s is diagnostically important: a non-zero mu indicates a
systematic datum or reference offset, correctable by translation, whereas s
reflects random reconstruction error and generalisation, which is not.

--------------------------------------------------------------------------------
5. RESULTS
--------------------------------------------------------------------------------

5.1 Per-site values (metres)

GCP   h_ell    dZ_photo   dZ_DGM   d_facade   dH_roof   note
  1   96.240     0.020    -3.300     0.145    -3.506    georeference origin
  2   95.720     0.092    -3.258     0.046    -2.738
  3   95.344    -0.037    -3.296    -0.085    -4.009
  4   94.914     0.074    -3.275    -0.090    -3.533
  8   95.112     0.129    -3.243     0.371    -4.673
  9   94.931     0.270    -3.270    -0.128    -3.357
 10   94.999     0.131    -3.274     0.574    -3.531    facade dispersion 5.209 m
 11   95.130     0.043    -3.239     0.067    -3.292
 12   93.768     0.083    -3.304     0.030    -3.965
 13   94.867     0.054    -3.272    -0.294    -3.551
 14   95.286     0.021    -3.270     0.024    -3.788

5.2 Summary (n = 11 for every row)

Dataset          Component                       mean      SD     RMSE
--------------------------------------------------------------------------------
Photogrammetry   vertical vs. GNSS               0.080   0.080    0.111
DGM              vertical vs. GNSS              -3.273   0.021    3.273
CityGML LoD2     horizontal vs. photogrammetry   0.060   0.240    0.236
CityGML LoD2     vertical vs. photogrammetry    -3.631   0.489    3.661

Additional descriptive values:

Photogrammetry vertical : median  0.074, range -0.037 to  0.270
DGM vertical            : median -3.272, range -3.304 to -3.239
LoD2 facade             : median  0.030, range -0.294 to  0.574
LoD2 roof               : median -3.533, range -4.673 to -2.738

5.3 Influence of individual sites

Two sites warrant explicit quantification, so that the reader can judge their
effect rather than infer it.

GCP 1 (georeference origin). Excluding it gives a photogrammetric mean of
0.086 m (SD 0.082, RMSE 0.116, n = 10) and a terrain mean of -3.270 m
(SD 0.020, RMSE 3.270, n = 10). Neither conclusion is affected.

GCP 10 (facade ray dispersion 5.209 m, indicating that individual rays
intersected different structures). Excluding it from the facade statistic gives
mean 0.009 m, SD 0.178, RMSE 0.169 (n = 10). The dispersion is essentially
unchanged while the bias falls by an order of magnitude, showing that this
single site accounts for most of the apparent facade bias reported in
Section 5.2. It is retained in the primary statistic for sample consistency.

--------------------------------------------------------------------------------
6. INTERPRETATION
--------------------------------------------------------------------------------

Horizontal. LoD2 facades agree with the photogrammetric facades to RMSE 0.236 m
across eleven buildings, with a mean offset of 0.060 m. The bias is small
relative to the dispersion of 0.240 m, and the sensitivity analysis in
Section 5.3 shows that it is concentrated at a single site whose measurement is
demonstrably unreliable; with that site removed the bias is 0.009 m. No
systematic planimetric shift between the two datasets is therefore detectable.
The residual dispersion is consistent with the combined effect of LoD2
generalisation and photogrammetric surface noise, and lies within the positional
accuracy specified for LoD2 building models derived from German cadastral data
(approximately 0.5 m).

Vertical. Both open datasets sit systematically below the RTK reference:
-3.273 m for the terrain model and -3.631 m for LoD2 roofs. The terrain offset
has a standard deviation of only 0.021 m across eleven sites, that is, it is a
constant translation rather than a distribution of errors, and the internal
consistency of the terrain model is therefore better than 3 cm. That two
independently produced products from the same national open-data infrastructure
exhibit offsets of the same order indicates a common vertical reference
inconsistency in the ingestion path rather than a fault in either dataset.

The offset is far smaller than the geoid undulation for this region
(approximately 45 m), so it does not correspond to a simple
orthometric-to-ellipsoidal confusion; its origin is not established here.

Consequence. Lateral extension of the environment beyond the photogrammetric
footprint using open CityGML and terrain data is justified: horizontal agreement
is unbiased at the decimetre level, which is the condition governing whether
geometry aligns across the boundary. Vertical continuity requires an explicit
correction of approximately 3.3 m applied to both open datasets. Because the
correction is a constant translation, applying it does not disturb the internal
geometry of either dataset.

--------------------------------------------------------------------------------
7. LIMITATIONS
--------------------------------------------------------------------------------

- Three of the fourteen surveyed points were excluded because no suitable
  building lay within the sampling radius. The exclusion was decided on site
  geometry before residuals were computed and applies uniformly to all four
  assessments.

- Facade measurements assess horizontal offset perpendicular to building walls;
  roof measurements assess vertical offset only. Neither addresses rotational
  misalignment, which would require feature correspondence rather than surface
  sampling.

- At GCP 10 the per-ray facade dispersion indicates that individual rays
  intersected different structures. The site is retained in the primary
  statistic and its influence quantified rather than removed.

- The cause of the vertical offset is not established and is identified as
  future work.

- Whether the ground control points were used as control during photogrammetric
  bundle adjustment or withheld as independent checkpoints determines whether
  the photogrammetric vertical figures represent independent accuracy or fitting
  residuals. [STATE WHICH APPLIES.]

- Measurements were made with the LoD2 clipping mask disabled so that both
  datasets coexisted in the overlap region; the deployed configuration
  suppresses LoD2 geometry inside the photogrammetric footprint.

================================================================================
8. FILES IN THIS FOLDER
================================================================================

    per_site_data.csv        per-site measurements, all four assessments
    summary_statistics.csv   the four summary rows
    tables.tex               two LaTeX tables, ready to paste
    methods_and_results.md   this document

================================================================================
END
================================================================================
