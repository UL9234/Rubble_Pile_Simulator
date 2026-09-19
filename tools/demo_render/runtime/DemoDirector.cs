// RubbleSim demo-render harness (isolated tooling, not part of the simulator's business logic).
// Authoritative source lives in tools/demo_render/; a synced copy is exposed to Unity under Assets/DemoRender/.
//
// DemoDirector is injected at runtime by DemoBootstrap, so it needs no scene edits, no prefab edits
// and no changes to the simulator's own scripts. It:
//   * optionally silences the ROS bridge and the 1024x1024 sensor cameras (a visual demo needs no roscore),
//   * builds its own capture camera (or rides on the robot's own camera for first-person shots),
//   * drives the vine robot programmatically for "robot enters the rubble" shots,
//   * runs the sim under Time.captureFramerate and writes one PNG per virtual frame,
//   * writes shot metadata (framing, frame count, effective args) next to the frames.
//
// Frame-exact capture via captureFramerate means the video is perfectly smooth even if the machine
// renders slower than real time, and it makes every clip reproducible from its arguments alone.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(20000)]
public class DemoDirector : MonoBehaviour
{
    private const float Deg2Rad = Mathf.PI / 180f;

    // ---- configuration (command line) ----
    private string shotName = "shot";
    private string preset = "overview";
    private string outDir;
    private string workDir;
    private int fps = 30;
    private int width = 1280;
    private int height = 720;
    private float startAfter;          // virtual seconds of simulation before the first captured frame
    private float duration = 20f;      // virtual seconds to capture
    private float timeScaleOverride = -1f;
    private bool quitWhenDone = true;
    private bool diag;

    // camera rig
    private float azimuth = 0f;
    private float elevation = 30f;
    private float distMult = 2.2f;
    private float fov = 45f;
    private float orbitDegPerSec = 15f;
    private float dollyFrom = -1f;
    private float dollyTo = -1f;
    private bool hasLook;
    private Vector3 lookAt;
    private float radiusOverride = -1f;

    // robot
    private string drive = "none";
    private float driveSpeed = 1f;
    private bool teleportRobot;
    private string rideTarget = "main";

    // toggles
    private bool silenceRos = true;
    private bool disableMainCam = true;
    private bool disableSensorCams = true;
    private bool rtSrgb = true;

    // ---- runtime state ----
    private Camera shotCam;
    private Camera mainCam;
    private Camera rgbCam;
    private Camera depthCam;
    private FrameRecorder recorder;
    private Transform rideTransform;   // camera we copy the pose from (fpv / sensor shots)
    private Transform robotRoot;
    private VineController vine;
    private float rideFov = 60f;
    private bool useRideFov;
    private float rideFovScale = 1f;

    private int frameIndex;            // virtual frames elapsed
    private int captured;              // frames written
    private int captureFrames;
    private Vector3 frameCenter;
    private float frameRadius;
    private Bounds pileBounds;
    private bool havePileBounds;
    private bool finished;
    private int progEvery = 30;
    private float captureMsTotal;
    private float captureMsLast;
    private float captureMsWorst;
    private readonly StringBuilder log = new StringBuilder();

    // =====================================================================================
    //  setup
    // =====================================================================================
    private void Awake()
    {
        shotName = DemoArgs.Get("demoshot", "shot");
        preset = DemoArgs.Get("demopreset", GuessPreset(shotName));
        outDir = DemoArgs.Get("demoout", Path.Combine(Application.dataPath, "../demo_out"));
        workDir = DemoArgs.Get("demowork", Path.Combine(outDir, "_frames", shotName));
        fps = Mathf.Max(1, DemoArgs.GetI("demofps", 30));
        width = Mathf.Max(16, DemoArgs.GetI("demowidth", 1280));
        height = Mathf.Max(16, DemoArgs.GetI("demoheight", 720));
        startAfter = DemoArgs.GetF("demostart", 0f);
        duration = DemoArgs.GetF("demoduration", 20f);
        timeScaleOverride = DemoArgs.GetF("demotimescale", -1f);
        quitWhenDone = DemoArgs.GetB("demoquit", true);
        diag = DemoArgs.GetB("demodiag", false);
        progEvery = DemoArgs.GetI("demoprog", 30);

        // Hardware/rig overrides are read as optionals so an absent flag cannot clobber the preset.
        float argAz = DemoArgs.GetF("demoazimuth", float.NaN);
        float argEl = DemoArgs.GetF("demoelevation", float.NaN);
        float argDist = DemoArgs.GetF("demodist", float.NaN);
        float argFov = DemoArgs.GetF("demofov", float.NaN);
        float argDollyFrom = DemoArgs.GetF("demodollyfrom", float.NaN);
        float argDollyTo = DemoArgs.GetF("demodollyto", float.NaN);

        orbitDegPerSec = DemoArgs.GetF("demoorbitspeed", 15f);
        hasLook = DemoArgs.Has("demolook");
        lookAt = DemoArgs.GetV3("demolook", Vector3.zero);
        radiusOverride = DemoArgs.GetF("demoradius", -1f);

        drive = DemoArgs.Get("demodrive", "none").ToLowerInvariant();
        driveSpeed = DemoArgs.GetF("demodrivespeed", 1f);
        teleportRobot = DemoArgs.GetB("demoteleport", false);
        rideFovScale = DemoArgs.GetF("demoridefovscale", 1f);

        silenceRos = DemoArgs.GetB("demorosoff", true);
        disableMainCam = DemoArgs.GetB("demomaincamoff", true);
        disableSensorCams = DemoArgs.GetB("demosensorcamsoff", true);
        rtSrgb = DemoArgs.GetB("demortssrgb", true);

        ApplyPreset(preset);
        rideTarget = DemoArgs.Get("demoride", rideTarget).ToLowerInvariant();

        if (!float.IsNaN(argAz)) azimuth = argAz;
        if (!float.IsNaN(argEl)) elevation = argEl;
        if (!float.IsNaN(argDist)) distMult = argDist;
        if (!float.IsNaN(argFov)) fov = argFov;
        if (!float.IsNaN(argDollyFrom)) dollyFrom = argDollyFrom;
        if (!float.IsNaN(argDollyTo)) dollyTo = argDollyTo;

        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        captureFrames = Mathf.Max(1, Mathf.RoundToInt(duration * fps));

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(workDir);
    }

    private void Start()
    {
        if (timeScaleOverride >= 0f) Time.timeScale = timeScaleOverride;
        Time.captureFramerate = fps;

        FindSceneObjects();
        if (silenceRos) SilenceRosComponents();
        BuildShotCamera();
        if (disableMainCam && mainCam != null) mainCam.enabled = false;
        if (disableSensorCams)
        {
            if (rgbCam != null) rgbCam.enabled = false;
            if (depthCam != null) depthCam.enabled = false;
        }

        Log(string.Format(CultureInfo.InvariantCulture,
            "[DemoDirector] shot={0} preset={1} fps={2} {3}x{4} start={5}s duration={6}s frames={7} ride={8}",
            shotName, preset, fps, width, height, startAfter, duration, captureFrames, rideTarget));
    }

    private static string GuessPreset(string shot)
    {
        string s = shot.ToLowerInvariant();
        if (s.Contains("orbit")) return "orbit";
        if (s.Contains("fpv")) return "fpv";
        if (s.Contains("sensor")) return "sensor";
        if (s.Contains("follow")) return "follow";
        if (s.Contains("approach")) return "approach";
        if (s.Contains("closeup")) return "closeup";
        if (s.Contains("low")) return "low";
        if (s.Contains("aerial")) return "aerial";
        return "overview";
    }

    private void ApplyPreset(string p)
    {
        switch (p.ToLowerInvariant())
        {
            case "overview": azimuth = 0f; elevation = 15f; distMult = 2.2f; fov = 55f; break;
            case "aerial": azimuth = -25f; elevation = 55f; distMult = 2.4f; fov = 50f; break;
            case "low": azimuth = 10f; elevation = 4f; distMult = 2.0f; fov = 60f; break;
            case "closeup": azimuth = -30f; elevation = 12f; distMult = 1.2f; fov = 45f; break;
            case "orbit": azimuth = 0f; elevation = 20f; distMult = 2.1f; fov = 50f; break;
            case "approach": azimuth = 8f; elevation = 12f; distMult = 2.0f; fov = 48f; dollyFrom = 2.0f; dollyTo = 1.1f; break;
            case "fpv": azimuth = 0f; elevation = 10f; distMult = 1.6f; fov = 60f; rideTarget = "main"; break;
            case "sensor": azimuth = 0f; elevation = 10f; distMult = 1.6f; fov = 90f; rideTarget = "rgb"; break;
            case "follow": azimuth = 0f; elevation = 15f; distMult = 1.4f; fov = 55f; rideTarget = "robot"; break;
            default:
                Log("[DemoDirector] unknown preset '" + p + "', falling back to overview");
                azimuth = 0f; elevation = 30f; distMult = 2.2f; fov = 45f;
                break;
        }
    }

    // =====================================================================================
    //  scene discovery
    // =====================================================================================
    private void FindSceneObjects()
    {
        vine = FindObjectOfType<VineController>();
        if (vine != null) robotRoot = vine.transform;

        mainCam = Camera.main;
        if (mainCam == null)
        {
            Transform t = FindTransformByName("Main Camera");
            if (t != null) mainCam = t.GetComponent<Camera>();
        }
        rgbCam = FindCameraByName("Rgb Camera");
        depthCam = FindCameraByName("Depth Camera");

        switch (rideTarget)
        {
            case "rgb": rideTransform = rgbCam != null ? rgbCam.transform : null; break;
            case "depth": rideTransform = depthCam != null ? depthCam.transform : null; break;
            case "robot": rideTransform = robotRoot; break;
            default: rideTransform = mainCam != null ? mainCam.transform : null; break;
        }

        if (rideTransform != null)
        {
            Camera rc = rideTransform.GetComponent<Camera>();
            if (rc == null) rc = rideTransform.GetComponentInChildren<Camera>();
            if (rc != null)
            {
                rideFov = rc.fieldOfView;
                useRideFov = true;
            }
        }
        else
        {
            Log("[DemoDirector] ride target '" + rideTarget + "' not found");
        }

        if (diag)
        {
            Log("[DemoDirector] diag robot=" + (robotRoot != null ? robotRoot.name + "@" + robotRoot.position.ToString("F2") : "NULL")
                + " mainCam=" + (mainCam != null ? mainCam.name : "NULL")
                + " rgbCam=" + (rgbCam != null ? rgbCam.name : "NULL")
                + " depthCam=" + (depthCam != null ? depthCam.name : "NULL")
                + " ride=" + (rideTransform != null ? rideTransform.name : "NULL")
                + " rideFov=" + rideFov.ToString("F1"));
        }
    }

    private static Camera FindCameraByName(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null)
        {
            Camera c = go.GetComponent<Camera>();
            if (c != null) return c;
        }
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t.name == name && t.gameObject.scene.IsValid())
            {
                Camera c = t.GetComponent<Camera>();
                if (c != null) return c;
            }
        }
        return null;
    }

    private static Transform FindTransformByName(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null) return go.transform;
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t.name == name && t.gameObject.scene.IsValid()) return t;
        }
        return null;
    }

    private void SilenceRosComponents()
    {
        int n = 0;
        foreach (MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>())
        {
            string tn = mb.GetType().Name;
            if (tn == "RosSensorOutput" || tn == "RosPs5")
            {
                mb.enabled = false;
                n++;
            }
        }
        Log("[DemoDirector] silenced " + n + " ROS component(s)");
    }

    // =====================================================================================
    //  framing
    // =====================================================================================
    private void ComputeFraming()
    {
        if (havePileBounds) return;
        havePileBounds = true;

        Vector3 center = new Vector3(0f, 5f, 0f);
        float radius = 9f;

        if (!hasLook)
        {
            Bounds b = new Bounds();
            bool any = false;
            int count = 0;
            foreach (Rigidbody rb in FindObjectsOfType<Rigidbody>())
            {
                if (robotRoot != null && rb.transform.IsChildOf(robotRoot)) continue;
                Renderer r = rb.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                count++;
            }
            if (any && count > 20)
            {
                pileBounds = b;
                center = b.center;
                radius = Mathf.Max(1.5f, b.extents.magnitude);
            }
            else
            {
                // Nothing spawned yet (macro shot starting at t=0): frame the spawn volume instead.
                float sx = DemoArgs.GetF("spawnboundx", 10f);
                float sy = DemoArgs.GetF("spawnboundy", 10f);
                float sz = DemoArgs.GetF("spawnboundz", 10f);
                Vector3 sp = new Vector3(DemoArgs.GetF("spawnposx", 0f), DemoArgs.GetF("spawnposy", 15f), DemoArgs.GetF("spawnposz", 0f));
                // Aim between the falling debris and the pile that is about to form on the ground.
                center = new Vector3(sp.x, sp.y - sy * 0.7f, sp.z);
                radius = new Vector3(sx, sy, sz).magnitude * 0.5f;
                Log("[DemoDirector] framing from spawn volume (debris found=" + count + ")");
            }
        }
        else
        {
            center = lookAt;
        }

        if (hasLook) center = lookAt;
        if (radiusOverride > 0f) radius = radiusOverride;

        frameCenter = center;
        frameRadius = radius;
        Log(string.Format(CultureInfo.InvariantCulture,
            "[DemoDirector] framing center={0} radius={1:F2} pileSize={2}",
            center.ToString("F2"), radius, pileBounds.size.ToString("F2")));
    }

    private static Vector3 SphericalOffset(float azDeg, float elDeg, float dist)
    {
        float az = azDeg * Deg2Rad;
        float el = elDeg * Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));
        return dir * dist;
    }

    // =====================================================================================
    //  capture camera
    // =====================================================================================
    private void BuildShotCamera()
    {
        GameObject go = new GameObject("DemoCaptureCamera");
        shotCam = go.AddComponent<Camera>();
        shotCam.enabled = false;                 // rendered manually, exactly once per virtual frame
        shotCam.fieldOfView = fov;
        shotCam.nearClipPlane = 0.05f;
        shotCam.farClipPlane = 3000f;
        shotCam.clearFlags = CameraClearFlags.Skybox;
        shotCam.allowHDR = true;
        shotCam.allowMSAA = false;
        shotCam.depth = 100;

        // Mirror the scene camera's rendering setup (renderer, culling mask, post-processing) so the
        // demo camera produces exactly the look the simulator itself produces.
        Camera template = rideTransform != null ? rideTransform.GetComponent<Camera>() : null;
        if (template == null) template = mainCam;
        if (template != null)
        {
            shotCam.clearFlags = template.clearFlags;
            shotCam.backgroundColor = template.backgroundColor;
            shotCam.cullingMask = template.cullingMask;
            shotCam.nearClipPlane = template.nearClipPlane;
            shotCam.farClipPlane = template.farClipPlane;
            shotCam.allowHDR = template.allowHDR;
            shotCam.allowMSAA = template.allowMSAA;

            Component data = template.GetComponent("UniversalAdditionalCameraData");
            if (data != null)
            {
                Component mine = shotCam.gameObject.AddComponent(data.GetType());
                foreach (var f in data.GetType().GetFields())
                {
                    try { f.SetValue(mine, f.GetValue(data)); } catch { }
                }
            }
        }

        recorder = new FrameRecorder(shotCam, width, height, Path.Combine(workDir, "frames"), rtSrgb);
        Log("[DemoDirector] capture camera ready (" + width + "x" + height + ")");
    }

    // =====================================================================================
    //  per-frame
    // =====================================================================================
    private void LateUpdate()
    {
        if (finished) return;
        if (timeScaleOverride >= 0f) Time.timeScale = timeScaleOverride;

        float t = frameIndex / (float)fps;   // virtual seconds since load
        bool capturing = t >= startAfter && captured < captureFrames;

        if (capturing && captured == 0)
        {
            ComputeFraming();
            if (teleportRobot && vine != null) vine.RandomizePosition();
        }

        UpdateCamera(t);

        if (capturing)
        {
            float t0 = Time.realtimeSinceStartup;
            recorder.Capture(captured);
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            captureMsTotal += ms;
            captureMsLast = ms;
            if (ms > captureMsWorst) captureMsWorst = ms;
            captured++;
        }

        frameIndex++;

        if (progEvery > 0 && frameIndex % progEvery == 0)
        {
            Log(string.Format(CultureInfo.InvariantCulture,
                "[DemoDirector] progress frame={0} captured={1} virtualTime={2:F2}s wall={3:F1}s lastCapture={4:F1}ms avgCapture={5:F1}ms worst={6:F1}ms",
                frameIndex, captured, Time.time, Time.realtimeSinceStartup,
                captureMsLast, captured > 0 ? captureMsTotal / captured : 0f, captureMsWorst));
        }

        if (captured >= captureFrames) Finish();
        else if (t > startAfter + duration + 60f)
        {
            Log("[DemoDirector] WARNING: bailed out with " + captured + "/" + captureFrames + " frames");
            Finish();
        }
    }

    private void UpdateCamera(float t)
    {
        if (shotCam == null) return;

        if (preset == "fpv" || preset == "sensor")
        {
            if (rideTransform != null)
            {
                shotCam.transform.position = rideTransform.position;
                shotCam.transform.rotation = rideTransform.rotation;
            }
            if (useRideFov)
            {
                // Unity's fieldOfView is vertical. rideFovScale lets a square sensor's horizontal FOV
                // be matched on a 16:9 canvas (scale = 9/16 for a 1:1 sensor).
                float halfV = Mathf.Atan(Mathf.Tan(rideFov * 0.5f * Deg2Rad) * rideFovScale) / Deg2Rad;
                shotCam.fieldOfView = Mathf.Clamp(halfV * 2f, 1f, 170f);
            }
            return;
        }

        if (preset == "follow" && robotRoot != null)
        {
            float dist = frameRadius * distMult;
            Vector3 back = robotRoot.rotation * new Vector3(0f, 0.55f, -1f);
            shotCam.transform.position = robotRoot.position + back.normalized * dist + Vector3.up * (frameRadius * 0.25f);
            shotCam.transform.LookAt(robotRoot.position + Vector3.up * frameRadius * 0.15f);
            shotCam.fieldOfView = fov;
            return;
        }

        float az = azimuth;
        float dm = distMult;

        if (preset == "orbit")
        {
            az = (t - Mathf.Max(0f, startAfter)) * orbitDegPerSec;
        }
        else if (preset == "approach")
        {
            float a = dollyFrom > 0f ? dollyFrom : 2.6f;
            float b = dollyTo > 0f ? dollyTo : 1.05f;
            float f = duration > 0f ? Mathf.Clamp01((t - startAfter) / duration) : 0f;
            f = f * f * (3f - 2f * f);   // ease in/out
            dm = Mathf.Lerp(a, b, f);
        }

        shotCam.transform.position = frameCenter + SphericalOffset(az, elevation, frameRadius * dm);
        shotCam.transform.LookAt(frameCenter + Vector3.up * (frameRadius * 0.05f));
        shotCam.fieldOfView = fov;
    }

    private void FixedUpdate()
    {
        if (finished || drive == "none" || vine == null) return;

        Rigidbody rb = vine.GetComponent<Rigidbody>();
        if (rb == null || rb.isKinematic) return;
        if (frameIndex / (float)fps < startAfter) return;   // stay put until the shot starts

        if (drive == "forward") vine.Move(new Vector3(0f, 0f, driveSpeed), Vector3.zero);
        else if (drive == "forwardleft") vine.Move(new Vector3(-0.35f * driveSpeed, 0f, driveSpeed), Vector3.zero);
        else if (drive == "forwardright") vine.Move(new Vector3(0.35f * driveSpeed, 0f, driveSpeed), Vector3.zero);
    }

    private void Finish()
    {
        if (finished) return;
        finished = true;
        WriteMetadata();
        recorder.Release();
        Debug.Log("[DemoDirector] captured " + captured + " frames -> " + Path.Combine(workDir, "frames"));
        if (quitWhenDone) Application.Quit(0);
    }

    private void WriteMetadata()
    {
        try
        {
            Bounds b = pileBounds;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"shot\": \"").Append(shotName).Append("\",\n");
            sb.Append("  \"preset\": \"").Append(preset).Append("\",\n");
            sb.Append("  \"fps\": ").Append(fps).Append(",\n");
            sb.Append("  \"width\": ").Append(width).Append(",\n");
            sb.Append("  \"height\": ").Append(height).Append(",\n");
            sb.Append("  \"frames\": ").Append(captured).Append(",\n");
            sb.Append("  \"start_after_s\": ").Append(startAfter.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"duration_s\": ").Append(duration.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"frame_center\": \"").Append(frameCenter.ToString("F3")).Append("\",\n");
            sb.Append("  \"frame_radius\": ").Append(frameRadius.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"pile_bounds_center\": \"").Append(b.center.ToString("F3")).Append("\",\n");
            sb.Append("  \"pile_bounds_size\": \"").Append(b.size.ToString("F3")).Append("\",\n");
            sb.Append("  \"ride_target\": \"").Append(rideTarget).Append("\",\n");
            sb.Append("  \"drive\": \"").Append(drive).Append("\",\n");
            sb.Append("  \"teleport_robot\": ").Append(teleportRobot ? "true" : "false").Append(",\n");
            sb.Append("  \"time_scale_override\": ").Append(timeScaleOverride.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"arguments\": \"").Append(DemoArgs.Dump().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append("\"\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(workDir, "shot.json"), sb.ToString());
            File.WriteAllText(Path.Combine(workDir, "shot.log"), log.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError("[DemoDirector] metadata write failed: " + e.Message);
        }
    }

    private void Log(string line)
    {
        log.AppendLine(line);
        Debug.Log(line);
    }
}
