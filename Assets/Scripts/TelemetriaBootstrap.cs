using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;

public class TelemetriaBootstrap : MonoBehaviour
{
    private ConfigReader _config;
    private TelemetryManager _telemetry;
    private XRHandSubsystem _xrHands;

    private InputDevice hmd, left, right;
    private List<InputDevice> devs = new List<InputDevice>();
    private List<XRNodeState> _nodeStates = new List<XRNodeState>();

    private bool _initialized = false;
    private bool _subscribed = false;
    private bool _shutdownCalled = false;
    private double _t0 = -1;
    private double _nextTickSec = 0;
    private double _periodSec = 1.0 / 72.0;

    // ── DIAGNÓSTICO ──────────────────────────────────────────────────────────
    private int _diagCounter = 0;
    private const int DIAG_INTERVAL = 72; // log cada ~1 segundo
    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        _config = new ConfigReader();
        _config.ReadConfig();

        _periodSec = 1.0 / Math.Max(1, _config.FrameRate);

        string sessionId = Application.productName + "-" +
                           DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 6);
        string deviceInfo = SystemInfo.deviceModel + " - " + Application.productName;

        var loader = XRGeneralSettings.Instance?.Manager?.activeLoader;
        if (loader != null)
            _xrHands = loader.GetLoadedSubsystem<XRHandSubsystem>();

        if (_xrHands == null)
            Debug.LogWarning("TelemetriaBootstrap: hand tracking no disponible");

        _telemetry = new TelemetryManager();
        _telemetry.Initialize(this, _config, sessionId, deviceInfo);

        Debug.Log("TelemetriaBootstrap: sesion iniciada " + sessionId);
        _initialized = true;
    }

    void OnEnable()
    {
        InputDevices.deviceConnected += OnDeviceEvent;
        InputDevices.deviceDisconnected += OnDeviceEvent;
        Application.onBeforeRender += OnBeforeRenderTick;
        _subscribed = true;
        AssignDevices();
    }

    void OnDisable()
    {
        InputDevices.deviceConnected -= OnDeviceEvent;
        InputDevices.deviceDisconnected -= OnDeviceEvent;
        if (_subscribed)
        {
            Application.onBeforeRender -= OnBeforeRenderTick;
            _subscribed = false;
        }
        DoShutdown();
    }

    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus) DoShutdown();
    }

    void OnDestroy()
    {
        DoShutdown();
    }

    void DoShutdown()
    {
        if (_shutdownCalled) return;
        _shutdownCalled = true;

        string outputPath = System.IO.Path.Combine(
            Application.persistentDataPath,
            "session_" + Application.productName + "-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".c3d");

        _telemetry?.Shutdown(outputPath);
        Debug.Log("TelemetriaBootstrap: shutdown, C3D guardado en " + outputPath);
    }

    void OnBeforeRenderTick()
    {
        if (!_initialized) return;

        double now = Time.realtimeSinceStartupAsDouble;
        if (_t0 < 0) _t0 = now;
        if (now < _nextTickSec) return;
        _nextTickSec += _periodSec;

        _diagCounter++;

        // ── Re-asignar dispositivos cada frame ───────────────────────────────
        // Con Quest Link el hmd.isValid puede volverse false y no se recupera
        // solo con eventos de conexion. Re-obtenerlo siempre es barato.
        AssignDevices();

        // ── Capturar HMD ─────────────────────────────────────────────────────
        var frame = new TelemetryManager.VRFrameData();
        frame.timestamp = now;

        bool hmdOk = false;

        // Método 1: devicePosition (más fiable que centerEyePosition con Link)
        if (hmd.isValid &&
            hmd.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 hp) &&
            hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion hr))
        {
            frame.hmdPos = new float[] { hp.x, hp.y, -hp.z };
            frame.hmdRot = new float[] { -hr.x, -hr.y, hr.z, hr.w };
            hmdOk = true;
        }

        // Método 2: XRNode.Head como fallback si lo anterior falla
        if (!hmdOk)
        {
            InputTracking.GetNodeStates(_nodeStates);
            foreach (var node in _nodeStates)
            {
                if (node.nodeType == XRNode.Head)
                {
                    bool hasPos = node.TryGetPosition(out Vector3 np);
                    bool hasRot = node.TryGetRotation(out Quaternion nr);
                    if (hasPos && hasRot)
                    {
                        frame.hmdPos = new float[] { np.x, np.y, -np.z };
                        frame.hmdRot = new float[] { -nr.x, -nr.y, nr.z, nr.w };
                        hmdOk = true;
                        if (_diagCounter % DIAG_INTERVAL == 0)
                            Debug.Log("[HMD] Usando fallback XRNode.Head — pos=" + np);
                    }
                    break;
                }
            }
        }

        if (!hmdOk)
        {
            frame.hmdPos = new float[] { 0, 0, 0 };
            frame.hmdRot = new float[] { 0, 0, 0, 1 };
        }

        // ── LOG DE DIAGNÓSTICO cada ~1 segundo ───────────────────────────────
        if (_diagCounter % DIAG_INTERVAL == 0)
        {
            bool isTracked = false;
            hmd.TryGetFeatureValue(CommonUsages.isTracked, out isTracked);

            Debug.Log(string.Format(
                "[DIAG] hmd.isValid={0} | isTracked={1} | hmdOk={2} | " +
                "pos=({3:F3},{4:F3},{5:F3}) | left.isValid={6} | right.isValid={7}",
                hmd.isValid, isTracked, hmdOk,
                frame.hmdPos[0], frame.hmdPos[1], frame.hmdPos[2],
                left.isValid, right.isValid));

            if (hmdOk && frame.hmdPos[0] == 0f && frame.hmdPos[1] == 0f && frame.hmdPos[2] == 0f)
                Debug.LogWarning("[DIAG] POSE EN CERO — mueve la cabeza, si sigue en cero hay problema de tracking");
        }
        // ─────────────────────────────────────────────────────────────────────

        // ── Mando izquierdo ──────────────────────────────────────────────────
        if (left.isValid &&
            left.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lp) &&
            left.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion lr))
        {
            frame.leftPos = new float[] { lp.x, lp.y, -lp.z };
            frame.leftRot = new float[] { -lr.x, -lr.y, lr.z, lr.w };
        }
        else
        {
            frame.leftPos = new float[] { 0, 0, 0 };
            frame.leftRot = new float[] { 0, 0, 0, 1 };
        }

        // ── Mando derecho ────────────────────────────────────────────────────
        if (right.isValid &&
            right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rp) &&
            right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rr))
        {
            frame.rightPos = new float[] { rp.x, rp.y, -rp.z };
            frame.rightRot = new float[] { -rr.x, -rr.y, rr.z, rr.w };
        }
        else
        {
            frame.rightPos = new float[] { 0, 0, 0 };
            frame.rightRot = new float[] { 0, 0, 0, 1 };
        }

        // ── Hand tracking ────────────────────────────────────────────────────
        if (_config.HandTracking && _xrHands != null)
        {
            frame.leftJoints = CaptureJoints(_xrHands.leftHand);
            frame.rightJoints = CaptureJoints(_xrHands.rightHand);
        }

        _telemetry.RecordFrame(frame);
    }

    C3DWriter.HandJoint[] CaptureJoints(XRHand hand)
    {
        if (!hand.isTracked) return null;

        var joints = new C3DWriter.HandJoint[26];
        for (int i = 0; i < 26; i++)
        {
            var id = XRHandJointIDUtility.FromIndex(i);
            var joint = hand.GetJoint(id);
            bool hasPose = joint.TryGetPose(out Pose pose);

            joints[i] = new C3DWriter.HandJoint
            {
                id = i,
                px = hasPose ? pose.position.x : 0,
                py = hasPose ? pose.position.y : 0,
                pz = hasPose ? -pose.position.z : 0,
                qx = hasPose ? -pose.rotation.x : 0,
                qy = hasPose ? -pose.rotation.y : 0,
                qz = hasPose ? pose.rotation.z : 0,
                qw = hasPose ? pose.rotation.w : 1,
                hasPose = hasPose
            };
        }
        return joints;
    }

    void OnDeviceEvent(InputDevice device) => AssignDevices();

    void AssignDevices()
    {
        devs.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, devs);
        hmd = devs.Count > 0 ? devs[0] : default;

        devs.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller, devs);
        left = devs.Count > 0 ? devs[0] : default;

        devs.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, devs);
        right = devs.Count > 0 ? devs[0] : default;
    }

    void Update() { }
}