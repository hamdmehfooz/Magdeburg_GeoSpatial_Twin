using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumForUnity;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Per-frame rendering telemetry for the VR benchmark.
///
/// The sampler writes nothing to disk while it is running — every frame goes
/// into a pre-allocated array and the CSV is produced once at the end. Touching
/// the file system mid-run would show up in exactly the metric we are trying to
/// measure.
///
/// Availability differs by build type:
///   FrameTimingManager  release + development  (needs Player Settings -> Other
///                                               Settings -> Frame Timing Stats)
///   XR display stats    release + development
///   ProfilerRecorder    development builds only (counters are stripped in
///                                               release, so those columns come
///                                               out as -1)
/// </summary>
public class PerfSampler : MonoBehaviour
{
    [Header("Capacity")]
    [Tooltip("Pre-allocated frame slots. 60000 covers ~11 min at 90 Hz.")]
    public int maxFrames = 60000;

    [Header("Cesium")]
    [Tooltip("Left empty, every Cesium3DTileset in the scene is found automatically.")]
    public Cesium3DTileset[] tilesets;

    private struct Sample
    {
        public float   t;
        public float   pathDist;
        public Vector3 rigPos;
        public float   cpuFrameMs;
        public float   gpuFrameMs;
        public float   cpuMainMs;
        public float   cpuRenderMs;
        public int     dropped;      // delta since previous frame
        public int     presented;    // delta since previous frame
        public long    drawCalls;
        public long    setPassCalls;
        public long    triangles;
        public long    vertices;
        public float   tileLoadMeanPct;
        public int     tilesetsLoading;
    }

    private Sample[] _buf;
    private int      _n;
    private bool     _capturing;
    private float    _t0;

    private ProfilerRecorder _recDraw;
    private ProfilerRecorder _recSetPass;
    private ProfilerRecorder _recTris;
    private ProfilerRecorder _recVerts;

    private readonly FrameTiming[] _timings = new FrameTiming[1];

    private int _prevDropped;
    private int _prevPresented;

    private CameraPathReplay _path;

    /// <summary>Frames captured in the current run.</summary>
    public int SampleCount { get { return _n; } }

    /// <summary>True while frames are being recorded.</summary>
    public bool IsCapturing { get { return _capturing; } }

    void Awake()
    {
        _buf  = new Sample[Mathf.Max(1000, maxFrames)];
        _path = FindFirstObjectByType<CameraPathReplay>();

        if (tilesets == null || tilesets.Length == 0)
            tilesets = FindObjectsByType<Cesium3DTileset>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
    }

    void OnEnable()
    {
        // Counters live under ProfilerCategory.Render. In a release build these
        // never produce a value; LastValue stays 0 and we emit -1 instead so the
        // analysis can tell "not measured" from "genuinely zero".
        _recDraw    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _recSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _recTris    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        _recVerts   = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
    }

    void OnDisable()
    {
        if (_recDraw.Valid)    _recDraw.Dispose();
        if (_recSetPass.Valid) _recSetPass.Dispose();
        if (_recTris.Valid)    _recTris.Dispose();
        if (_recVerts.Valid)   _recVerts.Dispose();
    }

    /// <summary>Clears the buffer and begins recording from the next frame.</summary>
    public void StartCapture()
    {
        _n         = 0;
        _t0        = Time.realtimeSinceStartup;
        _capturing = true;

        ReadXrCounters(out _prevDropped, out _prevPresented);
    }

    /// <summary>Stops recording. The buffer is kept until the next StartCapture.</summary>
    public void StopCapture()
    {
        _capturing = false;
    }

    // LateUpdate, so the rig has already been moved this frame and pathDist
    // matches the pose that was actually rendered.
    void LateUpdate()
    {
        if (!_capturing || _n >= _buf.Length) return;

        Sample s = new Sample();
        s.t = Time.realtimeSinceStartup - _t0;

        if (_path != null)
        {
            s.pathDist = _path.DistanceAlongPath;
            s.rigPos   = _path.CurrentLocalPosition;
        }

        // ── frame timing ──────────────────────────────────────────────────────
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
        {
            s.cpuFrameMs  = (float)_timings[0].cpuFrameTime;
            s.gpuFrameMs  = (float)_timings[0].gpuFrameTime;
            s.cpuMainMs   = (float)_timings[0].cpuMainThreadFrameTime;
            s.cpuRenderMs = (float)_timings[0].cpuRenderThreadFrameTime;
        }
        else
        {
            // Frame Timing Stats disabled, or the first few frames before the
            // manager has history. Fall back to wall-clock so the run is not
            // silently empty.
            s.cpuFrameMs  = Time.unscaledDeltaTime * 1000f;
            s.gpuFrameMs  = -1f;
            s.cpuMainMs   = -1f;
            s.cpuRenderMs = -1f;
        }

        // ── compositor truth ──────────────────────────────────────────────────
        int dropped, presented;
        ReadXrCounters(out dropped, out presented);
        s.dropped   = Mathf.Max(0, dropped   - _prevDropped);
        s.presented = Mathf.Max(0, presented - _prevPresented);
        _prevDropped   = dropped;
        _prevPresented = presented;

        // ── render counters ───────────────────────────────────────────────────
        s.drawCalls    = _recDraw.Valid    ? _recDraw.LastValue    : -1L;
        s.setPassCalls = _recSetPass.Valid ? _recSetPass.LastValue : -1L;
        s.triangles    = _recTris.Valid    ? _recTris.LastValue    : -1L;
        s.vertices     = _recVerts.Valid   ? _recVerts.LastValue   : -1L;

        // ── streaming state ───────────────────────────────────────────────────
        float sum = 0f;
        int   cnt = 0;
        int   busy = 0;
        for (int i = 0; i < tilesets.Length; i++)
        {
            if (tilesets[i] == null) continue;
            float pct = tilesets[i].ComputeLoadProgress();
            sum += pct;
            cnt++;
            if (pct < 99.9f) busy++;
        }
        s.tileLoadMeanPct = cnt > 0 ? sum / cnt : -1f;
        s.tilesetsLoading = busy;

        _buf[_n++] = s;
    }

    private static void ReadXrCounters(out int dropped, out int presented)
    {
        if (!XRStats.TryGetDroppedFrameCount(out dropped))  dropped   = 0;
        if (!XRStats.TryGetFramePresentCount(out presented)) presented = 0;
    }

    /// <summary>
    /// Writes the captured frames to <paramref name="path"/>. Metadata is
    /// emitted as leading '#' lines, which pandas skips with comment='#'.
    /// </summary>
    public void WriteCsv(string path, IList<string> metadata)
    {
        // InvariantCulture throughout — on a German-locale machine the default
        // formatter emits decimal commas, which would shred the CSV.
        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder(_n * 128 + 2048);

        if (metadata != null)
            for (int i = 0; i < metadata.Count; i++)
                sb.Append("# ").Append(metadata[i]).Append('\n');

        sb.Append("frame,t_s,path_dist_m,rig_x,rig_y,rig_z,")
          .Append("cpu_frame_ms,gpu_frame_ms,cpu_main_ms,cpu_render_ms,")
          .Append("dropped,presented,")
          .Append("draw_calls,setpass_calls,triangles,vertices,")
          .Append("tile_load_mean_pct,tilesets_loading\n");

        for (int i = 0; i < _n; i++)
        {
            Sample s = _buf[i];
            sb.Append(i.ToString(ci)).Append(',')
              .Append(s.t.ToString("F4", ci)).Append(',')
              .Append(s.pathDist.ToString("F3", ci)).Append(',')
              .Append(s.rigPos.x.ToString("F3", ci)).Append(',')
              .Append(s.rigPos.y.ToString("F3", ci)).Append(',')
              .Append(s.rigPos.z.ToString("F3", ci)).Append(',')
              .Append(s.cpuFrameMs.ToString("F4", ci)).Append(',')
              .Append(s.gpuFrameMs.ToString("F4", ci)).Append(',')
              .Append(s.cpuMainMs.ToString("F4", ci)).Append(',')
              .Append(s.cpuRenderMs.ToString("F4", ci)).Append(',')
              .Append(s.dropped.ToString(ci)).Append(',')
              .Append(s.presented.ToString(ci)).Append(',')
              .Append(s.drawCalls.ToString(ci)).Append(',')
              .Append(s.setPassCalls.ToString(ci)).Append(',')
              .Append(s.triangles.ToString(ci)).Append(',')
              .Append(s.vertices.ToString(ci)).Append(',')
              .Append(s.tileLoadMeanPct.ToString("F2", ci)).Append(',')
              .Append(s.tilesetsLoading.ToString(ci)).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString());
        Debug.Log("[PerfSampler] Wrote " + _n + " frames to " + path);
    }
}
