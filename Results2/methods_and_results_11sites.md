================================================================================
GEOMETRIC ASSESSMENT OF A FUSED PHOTOGRAMMETRIC / OPEN-DATA CAMPUS MODEL
Full methods, formulae and results  --  11-site analysis
================================================================================

Site         : Otto von Guericke University campus, Magdeburg, Germany
Engine       : Unity 6000.1.5f1, Cesium for Unity
Reference    : RTK-GNSS ground control points, ETRS89 / UTM zone 32N
Survey date  : 2024-12-01

SITES USED   : GCP 1, 2, 3, 4, 8, 9, 10, 11, 12, 13, 14   (11 sites)

--------------------------------------------------------------------------------
1. REFERENCE DATA AND SITE SELECTION
--------------------------------------------------------------------------------

Fourteen ground control points were observed across the campus with network-RTK
(VRS) GNSS in ETRS89 / UTM zone 32N. Each point was averaged over 201 epochs
with a fixed integer ambiguity solution. Reported coordinate precision:

    horizontal RMS : 0.010 - 0.014 m
    vertical   RMS : 0.011 - 0.025 m
    PDOP           : 1.3 - 3.4

This is approximately one order of magnitude better than the expected accuracy
of the models under test, which justifies treating the GNSS coordinates as
reference rather than as a second measurement of comparable quality.

Site selection. Of the fourteen surveyed points, eleven were retained for
analysis. Three (GCP 5, 6 and 7) were excluded a priori because no building
suitable for the comparison lay within the sampling radius: at those sites the
nearest surface is a low structure without a usable roof, and the nearest
genuine building is either unreachable by the sampling scheme or substantially
more distant than at every other site (84 m at GCP 6, against 8.7 - 27.9 m
elsewhere). The exclusion was made on the basis of site geometry alone, before
any residuals were examined, and applies uniformly to all four assessments so
that every reported statistic refers to the same eleven locations.

Datum. GCP 1 defines the origin of the engine georeference:

    georeference origin : 52.13958956 N, 11.64030482 E, h = 96.2461 m
    GCP 1               : 52.13958961 N, 11.64030469 E, h = 96.240  m

Agreement is at the centimetre level, so GCP 1 is the datum rather than an
independent observation. It is therefore excluded from statistics computed
against the GNSS reference (n = 10) but retained for model-to-model comparisons
(n = 11), where its datum status carries no consequence because neither model is
being compared with the survey.

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
ECEF position of the georeference origin. Hence

    p_local.x -> East,   p_local.y -> Up,   p_local.z -> North   (metres)

The frame is metric and locally Cartesian, so residuals expressed in local
coordinates are directly interpretable as metric distances.

2.3 Inverse UTM (terrain tile attribution only)

Each terrain tile is identified by its south-west corner in UTM zone 32N. Tile
corners were converted to geodetic coordinates by the standard inverse
transverse Mercator series (k0 = 0.9996, lambda_0 = 9 deg E) and then to the
local frame, in order to determine which tile geographically contains each site.

--------------------------------------------------------------------------------
3. MEASUREMENT PROCEDURES
--------------------------------------------------------------------------------

Surfaces are interrogated by ray casting against the collision meshes generated
by the tileset renderer. A ray is

    r(t) = o + t * d ,    t in [0, T] ,    |d| = 1

and the reported intersection is the smallest t at which the ray meets a
triangle of the target surface. Only mesh colliders are accepted; the
axis-aligned proxy volumes attached to tileset roots are rejected, as they do
not represent surface geometry.

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
perpendicular separation of the two facade surfaces in that direction and is
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
ground, excluding ground and low-vegetation intersections; samples with
|dH_ij| > 12 m are discarded as gross mismatches.

--------------------------------------------------------------------------------
4. STATISTICAL TREATMENT
--------------------------------------------------------------------------------

4.1 Two-level aggregation

Rays within a site are not independent observations: they interrogate the same
pair of surfaces and share systematic error. Pooling all rays would constitute
pseudo-replication and would allow a single noisy site to dominate the result.
A robust site statistic is therefore formed first,

    delta_tilde_i = median_j ( delta_ij )

and summary statistics are computed across the m site values. The median is used
at site level because photogrammetric outliers are strongly one-sided:
vegetation and clutter occlude a facade but never recede behind it.

4.2 Summary statistics

For site values x_1 ... x_m:

    mean        mu   = (1/m) * sum_i x_i
    sample SD   s    = sqrt( (1/(m-1)) * sum_i (x_i - mu)^2 )
    RMSE        rmse = sqrt( (1/m) * sum_i x_i^2 )

RMSE is computed about zero rather than about the mean: it expresses total
departure from the reference and is therefore an accuracy measure. Bias and
dispersion combine as

    rmse^2 = mu^2 + ((m-1)/m) * s^2

Worked check, photogrammetric vertical residuals (m = 10):

    mu = 0.086 ,  s = 0.082
    mu^2                = 0.007396
    (9/10) * 0.082^2    = 0.006052
    sqrt(0.013448)      = 0.1160  ->  0.116 m   (matches the reported RMSE)

Separating mu from s is diagnostically important: a non-zero mu indicates a
systematic datum or reference offset, correctable by translation, whereas s
reflects random reconstruction error and generalisation, which is not.

--------------------------------------------------------------------------------
5. RESULTS
--------------------------------------------------------------------------------

5.1 Per-site values (metres)

GCP   h_ell    dZ_photo   dZ_DGM   d_facade   dH_roof   n_roof   note
  1   96.240     0.020    -3.300     0.145    -3.506      16     datum
  2   95.720     0.092    -3.258     0.046    -2.738      20
  3   95.344    -0.037    -3.296    -0.085    -4.009      13
  4   94.914     0.074    -3.275    -0.090    -3.533      14
  8   95.112     0.129    -3.243     0.371    -4.673      20
  9   94.931     0.270    -3.270    -0.128    -3.357      16
 10   94.999     0.131    -3.274     0.574    -3.531      18     facade dispersion 5.209 m
 11   95.130     0.043    -3.239     0.067    -3.292      16
 12   93.768     0.083    -3.304     0.030    -3.965      12
 13   94.867     0.054    -3.272    -0.294    -3.551      14
 14   95.286     0.021    -3.270     0.024    -3.788      20

5.2 Summary

Assessment                  Sample                     n     mean      SD    RMSE
--------------------------------------------------------------------------------
Photogrammetry vertical     checkpoints (excl. datum) 10    0.086   0.082   0.116
DGM vertical                checkpoints (excl. datum) 10   -3.270   0.020   3.270
LoD2 facade (horizontal)    all sites                 11    0.060   0.240   0.236
LoD2 roof (vertical)        all sites                 11   -3.631   0.489   3.661

Sensitivity. Excluding GCP 10 from the facade statistic, whose per-ray
dispersion of 5.209 m indicates that individual rays intersected different
structures, gives mean 0.009 m, SD 0.178 m, RMSE 0.169 m (n = 10). The bias is
reduced by an order of magnitude while the dispersion is essentially unchanged,
indicating that this single site contributes most of the apparent facade bias.
Including GCP 1 in the terrain statistic gives mean -3.273 m, SD 0.021 m
(n = 11), i.e. no material change.

--------------------------------------------------------------------------------
6. INTERPRETATION
--------------------------------------------------------------------------------

Horizontal. LoD2 facades agree with the photogrammetric facades to RMSE 0.236 m
across eleven buildings, with a mean offset of 0.060 m. Removing the single site
whose rays are demonstrably unreliable reduces the bias to 0.009 m at
essentially unchanged dispersion, so no systematic planimetric shift is
detectable between the two datasets. The residual dispersion of 0.18 - 0.24 m is
consistent with the combined effect of LoD2 generalisation and photogrammetric
surface noise, and lies within the positional accuracy specified for LoD2
building models derived from German cadastral data (approximately 0.5 m).

Vertical. Both open datasets sit systematically below the RTK reference:
-3.270 m for the terrain model and -3.631 m for LoD2 roofs. The terrain offset
has a standard deviation of only 0.020 m across ten checkpoints, that is, it is
a constant translation rather than a distribution of errors. That two
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
correction is a constant translation, it does not disturb the internal geometry
of either dataset.

--------------------------------------------------------------------------------
7. LIMITATIONS
--------------------------------------------------------------------------------

- Three of the fourteen surveyed points were excluded because no suitable
  building lay within the sampling radius. The exclusion was made on site
  geometry before residuals were examined and applies uniformly to all
  assessments.

- Facade measurements assess horizontal offset perpendicular to building walls;
  roof measurements assess vertical offset only. Neither addresses rotational
  misalignment, which would require feature correspondence rather than surface
  sampling.

- At GCP 10 the per-ray facade dispersion of 5.209 m indicates that individual
  rays intersected different structures. The site is retained in the primary
  statistic and reported with a sensitivity analysis rather than removed.

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
8. LATEX  --  READY TO PASTE
================================================================================

Preamble:  \usepackage{booktabs}   \usepackage{siunitx}

--------------------------------------------------------------------------------
TABLE 1 : per-site results
--------------------------------------------------------------------------------

\begin{table}[htbp]
\centering
\caption{Per-site geometric assessment of the fused campus model at eleven
RTK-GNSS control points. $\Delta Z_{\mathrm{photo}}$ and
$\Delta Z_{\mathrm{DGM}}$ are vertical residuals against the GNSS reference
(positive $=$ model above reference); $d_{\mathrm{fac}}$ is the median
horizontal facade offset between the CityGML LoD2 and photogrammetric surfaces
(positive $=$ LoD2 wall closer to the site); $\Delta H_{\mathrm{roof}}$ is the
median roof height difference (negative $=$ LoD2 below photogrammetry).}
\label{tab:per_site}
\sisetup{table-format=-1.3}
\begin{tabular}{r S S S S S r l}
\toprule
{GCP} & {$h_{\mathrm{ell}}$} & {$\Delta Z_{\mathrm{photo}}$} & {$\Delta Z_{\mathrm{DGM}}$} & {$d_{\mathrm{fac}}$} & {$\Delta H_{\mathrm{roof}}$} & {$n_{\mathrm{roof}}$} & {Note} \\
      & {\si{\metre}} & {\si{\metre}} & {\si{\metre}} & {\si{\metre}} & {\si{\metre}} & & \\
\midrule
1  & 96.240 &  0.020 & -3.300 &  0.145 & -3.506 & 16 & datum \\
2  & 95.720 &  0.092 & -3.258 &  0.046 & -2.738 & 20 & --- \\
3  & 95.344 & -0.037 & -3.296 & -0.085 & -4.009 & 13 & --- \\
4  & 94.914 &  0.074 & -3.275 & -0.090 & -3.533 & 14 & --- \\
8  & 95.112 &  0.129 & -3.243 &  0.371 & -4.673 & 20 & --- \\
9  & 94.931 &  0.270 & -3.270 & -0.128 & -3.357 & 16 & --- \\
10 & 94.999 &  0.131 & -3.274 &  0.574 & -3.531 & 18 & \textsuperscript{a} \\
11 & 95.130 &  0.043 & -3.239 &  0.067 & -3.292 & 16 & --- \\
12 & 93.768 &  0.083 & -3.304 &  0.030 & -3.965 & 12 & --- \\
13 & 94.867 &  0.054 & -3.272 & -0.294 & -3.551 & 14 & --- \\
14 & 95.286 &  0.021 & -3.270 &  0.024 & -3.788 & 20 & --- \\
\bottomrule
\end{tabular}

\vspace{2pt}
\footnotesize
\textsuperscript{a} Per-ray facade dispersion \SI{5.209}{\metre}; see the
sensitivity analysis in Table~\ref{tab:summary}.
\end{table}

--------------------------------------------------------------------------------
TABLE 2 : summary statistics
--------------------------------------------------------------------------------

\begin{table}[htbp]
\centering
\caption{Summary of the geometric assessment over eleven control sites.
Horizontal agreement between the open CityGML LoD2 data and the photogrammetric
reconstruction is unbiased at the decimetre level, whereas both open datasets
exhibit a systematic vertical offset of approximately \SI{3.3}{\metre} relative
to the RTK-GNSS reference. RMSE is computed about zero.}
\label{tab:summary}
\sisetup{table-format=-1.3}
\begin{tabular}{l l r S S S}
\toprule
{Assessment} & {Sample} & {$n$} & {Mean} & {SD} & {RMSE} \\
             &          &       & {\si{\metre}} & {\si{\metre}} & {\si{\metre}} \\
\midrule
Photogrammetry vertical  & checkpoints\textsuperscript{b} & 10 &  0.086 & 0.082 & 0.116 \\
DGM vertical             & checkpoints\textsuperscript{b} & 10 & -3.270 & 0.020 & 3.270 \\
\addlinespace
LoD2 facade (horizontal) & all sites                      & 11 &  0.060 & 0.240 & 0.236 \\
\quad excl. GCP 10\textsuperscript{c} & sensitivity       & 10 &  0.009 & 0.178 & 0.169 \\
LoD2 roof (vertical)     & all sites                      & 11 & -3.631 & 0.489 & 3.661 \\
\bottomrule
\end{tabular}

\vspace{2pt}
\footnotesize
\textsuperscript{b} GCP~1 defines the georeference origin and is excluded from
comparisons against the GNSS reference. Including it in the terrain statistic
gives \num{-3.273}\,\si{\metre} (SD \num{0.021}, $n=11$).
\textsuperscript{c} Per-ray dispersion at GCP~10 is \SI{5.209}{\metre},
indicating that individual rays intersected different structures.
\end{table}

--------------------------------------------------------------------------------
TABLE 3 : compact single-table version
--------------------------------------------------------------------------------

\begin{table}[htbp]
\centering
\caption{Geometric agreement between the photogrammetric reconstruction, the
open CityGML LoD2 building model and the open DGM terrain model, evaluated at
eleven RTK-GNSS control sites on the campus.}
\label{tab:compact}
\begin{tabular}{llS[table-format=-1.3]S[table-format=1.3]S[table-format=1.3]r}
\toprule
Dataset & Component & {Mean (\si{\metre})} & {SD (\si{\metre})} & {RMSE (\si{\metre})} & {$n$} \\
\midrule
Photogrammetry & vertical            &  0.086 & 0.082 & 0.116 & 10 \\
DGM            & vertical            & -3.270 & 0.020 & 3.270 & 10 \\
CityGML LoD2   & horizontal (facade) &  0.060 & 0.240 & 0.236 & 11 \\
CityGML LoD2   & vertical (roof)     & -3.631 & 0.489 & 3.661 & 11 \\
\bottomrule
\end{tabular}
\end{table}

================================================================================
END
================================================================================
