# Repository size and pushing to GitHub

## Current state

| Measure | Value |
|---|---|
| Packed objects | 5.71 GiB |
| `.git` on disk | 5.8 GB |
| Tracked working tree | 2.9 GB |
| Tracked files | 4,386 |
| Remote | `https://github.com/hamdmehfooz/Magdeburg_GeoSpatial_Twin.git` (exists, empty) |

**Nothing has been pushed.** GitHub hard-rejects any single file over 100 MB and
blocks pushes over 2 GB, so this repository cannot be pushed as it stands.

## Files over the 100 MB limit

| Size | File | Category |
|---|---|---|
| 2,526 MB | `Assets/GTFS/stop_times.txt` | transit feed |
| 1,318 MB | `Results3/IMIQ_Data/sharedassets0.assets.resS` | build output |
| 1,186 MB | `Results3/IMIQ_Data/sharedassets0.assets` | build output |
| 662 MB | `Assets/External_Models/…Windows3DViewer.glb` | source model |
| 577 MB | `Assets/External_Models/…GLB_02.07.26.glb` | source model |
| 420 MB | `Assets/External_Models/…1Container…glb` | source model |
| 287 MB | `Assets/Scenes/SmartMobilityCore.unity` | **needed** |
| 287 MB | `Assets/GTFS/shapes.txt` | transit feed |
| 286 MB | `Assets/External_Models/…13.05.fbx` | source model |
| 169 MB | `Assets/GTFS/trips.txt` | transit feed |
| 138 MB | `Results3/IMIQ_Data/resources.assets.resS` | build output |

## How the build output got committed

A Unity player build was written into `Results3/` rather than a `Build/` folder.
The `.gitignore` only excluded `/Build/` and `/Builds/`, so `IMIQ.exe`,
`IMIQ_Data/` (192 files), `MonoBleedingEdge/`, `UnityPlayer.dll` and the
graphics runtimes were all committed — about 2.7 GB. Only 4 of the 222 tracked
files under `Results3/` are actual research outputs.

This is now covered by the `.gitignore` rules added under *Unity player builds*,
so it will not recur. Those rules do not remove what is already in history —
that is what `clean-history.sh` is for.

## The scene file

`Assets/Scenes/SmartMobilityCore.unity` is **287 MB**, and unlike the others you
need it under version control.

It is that large because the OSM importers bake every generated mesh inline:
178 water areas, 253 green areas and roughly 2,000 trees are serialised as text
inside the scene. Beyond the git problem this makes the scene slow to open and
save, and impossible to merge.

Three ways out, in increasing order of effort:

1. **Git LFS** — versions the scene as-is. GitHub's free tier is 1 GB storage
   and 1 GB/month bandwidth; more needs paid data packs.
2. **Extract the baked meshes into asset files** and reference them from
   prefabs, so the scene stores references rather than geometry. This fixes the
   underlying problem rather than working around it.
3. **Regenerate rather than store** — commit the importer settings and OSM
   extracts, and treat the populated scene as a build product.

## Running the cleanup

```bash
bash tools/clean-history.sh            # dry run — prints what would be removed
```

Before applying:

```bash
git clone --mirror . ../vr-backup.git  # full backup, history included
```

Archive anything in Tier 2 you still need — those files are **not**
regenerable. Then:

```bash
bash tools/clean-history.sh --apply
```

The script rewrites history, repacks, reports the new size, and lists any files
still over 100 MB.

## After the rewrite

`git filter-repo` deletes the remote as a safety measure. Restore it:

```bash
git remote add origin https://github.com/hamdmehfooz/Magdeburg_GeoSpatial_Twin.git
```

Every commit hash changes. Since nothing was ever pushed, no one else is
affected.

Pushing over HTTPS requires a **personal access token**, not an account
password. The `gh` CLI is not installed on this machine; installing it
(`winget install GitHub.cli`) makes authentication easier.

## For the paper

Large binary sources — the station models and GTFS feeds — are better archived
in a data repository such as Zenodo, which issues a DOI you can cite, than
carried in git. That keeps the repository to code and results, which is what a
reader needs to reproduce the work, and gives the data a citable permanent home.
