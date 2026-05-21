using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public class C3DWriter
{
    [DllImport("ezc3d_wrapper")] private static extern void c3d_init(float frameRate);
    [DllImport("ezc3d_wrapper")] private static extern void c3d_add_label(string label);
    [DllImport("ezc3d_wrapper")] private static extern void c3d_set_params();
    [DllImport("ezc3d_wrapper")] private static extern int c3d_add_frame(float[] positions, int nPoints, float[] analogs, int nAnalogs);
    [DllImport("ezc3d_wrapper")] private static extern void c3d_write(string path);
    [DllImport("ezc3d_wrapper")] private static extern void c3d_destroy();

    public struct PointData
    {
        public float x, y, z;
        public float qx, qy, qz, qw;
        public float timestamp;
    }

    public struct HandJoint
    {
        public int id;
        public float px, py, pz;
        public float qx, qy, qz, qw;
        public bool hasPose;
    }

    public struct FrameData
    {
        public float timestamp;
        public PointData hmd;
        public PointData leftCtrl;
        public PointData rightCtrl;
        public HandJoint[] leftJoints;
        public HandJoint[] rightJoints;
    }

    private static readonly string[] BASE_LABELS = new string[]
    {
        "HMD", "LFHD", "RFHD", "LWRA", "LWRB", "RWRA", "RWRB", "LFIN", "RFIN"
    };

    private static readonly string[] JOINT_NAMES = new string[]
    {
        "Palm","Wrist",
        "ThumbMetacarpal","ThumbProximal","ThumbDistal","ThumbTip",
        "IndexMetacarpal","IndexProximal","IndexIntermediate","IndexDistal","IndexTip",
        "MiddleMetacarpal","MiddleProximal","MiddleIntermediate","MiddleDistal","MiddleTip",
        "RingMetacarpal","RingProximal","RingIntermediate","RingDistal","RingTip",
        "LittleMetacarpal","LittleProximal","LittleIntermediate","LittleDistal","LittleTip"
    };

    private List<FrameData> _frames = new List<FrameData>();
    private float _frameRate = 72f;
    private bool _hasHandTracking = false;
    private string _sessionId = "";

    private const float HEAD_HALF_WIDTH = 0.0875f;
    private const float WRIST_HALF_WIDTH = 0.0275f;

    public void Initialize(string sessionId, float frameRate, bool hasHandTracking)
    {
        _sessionId = sessionId;
        _frameRate = frameRate;
        _hasHandTracking = hasHandTracking;
        _frames.Clear();
        Debug.Log("C3DWriter: sesion iniciada " + sessionId);
    }

    public void AddFrame(FrameData frame)
    {
        _frames.Add(frame);
    }

    public void Write(string outputPath)
    {
        if (_frames.Count == 0)
        {
            Debug.LogWarning("C3DWriter: no hay frames que guardar");
            return;
        }

        var labels = new List<string>(BASE_LABELS);
        if (_hasHandTracking)
        {
            foreach (var j in JOINT_NAMES) labels.Add("L_" + j);
            foreach (var j in JOINT_NAMES) labels.Add("R_" + j);
        }

        int nPoints = labels.Count;
        int nAnalogs = 0;

        // Inicializar ezc3d via DLL
        c3d_init(_frameRate);

        // Añadir labels
        foreach (var label in labels)
            c3d_add_label(label);

        // Configurar parametros antes de añadir frames
        c3d_set_params();

        // Añadir frames uno a uno
        int frameErrors = 0;
        foreach (var frame in _frames)
        {
            var points = ComputePoints(frame, nPoints);
            float[] positions = new float[nPoints * 3];
            for (int i = 0; i < nPoints; i++)
            {
                positions[i * 3 + 0] = points[i].x * 1000f;
                positions[i * 3 + 1] = points[i].y * 1000f;
                positions[i * 3 + 2] = points[i].z * 1000f;
            }

            float[] analogs = nAnalogs > 0 ? BuildAnalogData(frame, nAnalogs) : new float[0];
            int result = c3d_add_frame(positions, nPoints, analogs, nAnalogs);
            if (result != 0)
            {
                frameErrors++;
                if (frameErrors <= 3)
                    Debug.LogError($"C3DWriter: error al añadir frame, codigo {result}");
            }
        }

        if (frameErrors > 0)
            Debug.LogError($"C3DWriter: {frameErrors} frames fallaron");

        // Escribir el archivo C3D
        c3d_write(outputPath);
        c3d_destroy();

        Debug.Log($"C3DWriter: {_frames.Count} frames guardados en {outputPath}");
    }

    private List<Vector3> ComputePoints(FrameData frame, int nPoints)
    {
        var pts = new List<Vector3>();

        Vector3 hmdPos = ConvertPos(frame.hmd.x, frame.hmd.y, frame.hmd.z);
        Quaternion hmdRot = ConvertRot(frame.hmd.qx, frame.hmd.qy, frame.hmd.qz, frame.hmd.qw);
        Vector3 leftPos = ConvertPos(frame.leftCtrl.x, frame.leftCtrl.y, frame.leftCtrl.z);
        Quaternion leftRot = ConvertRot(frame.leftCtrl.qx, frame.leftCtrl.qy, frame.leftCtrl.qz, frame.leftCtrl.qw);
        Vector3 rightPos = ConvertPos(frame.rightCtrl.x, frame.rightCtrl.y, frame.rightCtrl.z);
        Quaternion rightRot = ConvertRot(frame.rightCtrl.qx, frame.rightCtrl.qy, frame.rightCtrl.qz, frame.rightCtrl.qw);

        pts.Add(hmdPos);

        Vector3 headOffset = new Vector3(HEAD_HALF_WIDTH, 0, 0);
        pts.Add(hmdPos + hmdRot * headOffset);
        pts.Add(hmdPos - hmdRot * headOffset);

        Vector3 wristOffset = new Vector3(WRIST_HALF_WIDTH, 0, 0);
        pts.Add(leftPos + leftRot * wristOffset);
        pts.Add(leftPos - leftRot * wristOffset);
        pts.Add(rightPos + rightRot * wristOffset);
        pts.Add(rightPos - rightRot * wristOffset);

        if (_hasHandTracking && frame.leftJoints != null && frame.leftJoints.Length > 10)
            pts.Add(ConvertPos(frame.leftJoints[10].px, frame.leftJoints[10].py, frame.leftJoints[10].pz));
        else
            pts.Add(leftPos);

        if (_hasHandTracking && frame.rightJoints != null && frame.rightJoints.Length > 10)
            pts.Add(ConvertPos(frame.rightJoints[10].px, frame.rightJoints[10].py, frame.rightJoints[10].pz));
        else
            pts.Add(rightPos);

        if (_hasHandTracking)
        {
            if (frame.leftJoints != null)
                foreach (var j in frame.leftJoints) pts.Add(ConvertPos(j.px, j.py, j.pz));
            else
                for (int i = 0; i < 26; i++) pts.Add(Vector3.zero);

            if (frame.rightJoints != null)
                foreach (var j in frame.rightJoints) pts.Add(ConvertPos(j.px, j.py, j.pz));
            else
                for (int i = 0; i < 26; i++) pts.Add(Vector3.zero);
        }

        return pts;
    }

    private float[] BuildAnalogData(FrameData frame, int nAnalogs)
    {
        var a = new float[nAnalogs];
        int idx = 0;

        a[idx++] = frame.timestamp;
        a[idx++] = frame.hmd.qx; a[idx++] = frame.hmd.qy;
        a[idx++] = frame.hmd.qz; a[idx++] = frame.hmd.qw;
        a[idx++] = frame.leftCtrl.qx; a[idx++] = frame.leftCtrl.qy;
        a[idx++] = frame.leftCtrl.qz; a[idx++] = frame.leftCtrl.qw;
        a[idx++] = frame.rightCtrl.qx; a[idx++] = frame.rightCtrl.qy;
        a[idx++] = frame.rightCtrl.qz; a[idx++] = frame.rightCtrl.qw;

        if (_hasHandTracking)
        {
            var lj = frame.leftJoints ?? new HandJoint[26];
            var rj = frame.rightJoints ?? new HandJoint[26];
            foreach (var j in lj) { a[idx++] = j.qx; a[idx++] = j.qy; a[idx++] = j.qz; a[idx++] = j.qw; }
            foreach (var j in rj) { a[idx++] = j.qx; a[idx++] = j.qy; a[idx++] = j.qz; a[idx++] = j.qw; }
        }

        return a;
    }

    private Vector3 ConvertPos(float x, float y, float z)
    {
        return new Vector3(-z, -x, y);
    }

    private Quaternion ConvertRot(float qx, float qy, float qz, float qw)
    {
        Quaternion q = new Quaternion(qx, qy, qz, qw);
        Quaternion fix = Quaternion.Euler(90, 0, 0);
        return fix * q;
    }
}