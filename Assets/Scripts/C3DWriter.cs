using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class C3DWriter
{
    // Estructura de un punto 3D con su orientacion
    public struct PointData
    {
        public float x, y, z;
        public float qx, qy, qz, qw;
        public float timestamp;
    }

    // Estructura de un joint de la mano
    public struct HandJoint
    {
        public int id;
        public float px, py, pz;
        public float qx, qy, qz, qw;
        public bool hasPose;
    }

    // Un frame completo de captura
    public struct FrameData
    {
        public float timestamp;
        public PointData hmd;
        public PointData leftCtrl;
        public PointData rightCtrl;
        public HandJoint[] leftJoints;
        public HandJoint[] rightJoints;
    }

    // Labels de los markers principales, siguiendo el modelo Plug-in Gait
    // Los puntos LFHD, RFHD, LWRA, LWRB, RWRA, RWRB son sinteticos calculados
    // a partir del centro del HMD y las munecas
    private static readonly string[] BASE_LABELS = new string[]
    {
        "HMD",
        "LFHD", "RFHD",
        "LWRA", "LWRB",
        "RWRA", "RWRB",
        "LFIN", "RFIN"
    };

    // Los 26 joints que define OpenXR por mano
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
    private string _sessionId = "";
    private bool _hasHandTracking = false;

    // Medidas antropometricas medias segun la literatura
    // Se usan para calcular los puntos sinteticos de cabeza y muneca
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

        // Canales analogicos: timestamp + quaternion HMD + quaternion left + quaternion right
        // Si hay hand tracking se añaden los quaterniones de los 26 joints de cada mano
        int analogPerFrame = 13 + (_hasHandTracking ? 208 : 0);

        using (var bw = new BinaryWriter(File.Open(outputPath, FileMode.Create), Encoding.ASCII))
        {
            WriteHeader(bw, nPoints, analogPerFrame);
            WriteParameterSection(bw, nPoints, analogPerFrame, labels);
            WriteDataSection(bw, nPoints, analogPerFrame);
        }

        Debug.Log($"C3DWriter: {_frames.Count} frames guardados en {outputPath}");
    }

    // El header de C3D ocupa exactamente 512 bytes
    private void WriteHeader(BinaryWriter bw, int nPoints, int analogPerFrame)
    {
        long start = bw.BaseStream.Position;

        bw.Write((byte)2);
        bw.Write((byte)0x50);
        bw.Write((short)nPoints);
        bw.Write((short)analogPerFrame);
        bw.Write((short)1);
        bw.Write((short)_frames.Count);
        bw.Write((short)0);
        bw.Write(-0.001f); // escala negativa indica datos en float, valor indica mm
        bw.Write((short)3);
        bw.Write((short)1);
        bw.Write(_frameRate);

        long written = bw.BaseStream.Position - start;
        bw.Write(new byte[512 - written]);
    }

    // Seccion de parametros C3D - contiene los metadatos de la sesion
    private void WriteParameterSection(BinaryWriter bw, int nPoints, int analogPerFrame, List<string> labels)
    {
        long start = bw.BaseStream.Position;

        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((byte)1);
        bw.Write((byte)0x50);

        // Grupo POINT - datos cinematicos 3D
        WriteParamGroupStart(bw, 1, "POINT", "Datos 3D");
        WriteParamInt(bw, 1, "USED", (short)nPoints);
        WriteParamFloat(bw, 1, "SCALE", -0.001f);
        WriteParamInt(bw, 1, "FRAMES", (short)_frames.Count);
        WriteParamFloat(bw, 1, "RATE", _frameRate);
        WriteParamStringArray(bw, 1, "LABELS", labels);
        WriteParamString(bw, 1, "UNITS", "mm");

        // Grupo ANALOG - quaterniones y timestamps
        WriteParamGroupStart(bw, 2, "ANALOG", "Canales analogicos");
        WriteParamInt(bw, 2, "USED", (short)analogPerFrame);
        WriteParamFloat(bw, 2, "RATE", _frameRate);
        WriteParamFloat(bw, 2, "GEN_SCALE", 1.0f);

        // Rellenar hasta el siguiente bloque de 512 bytes
        long written = bw.BaseStream.Position - start;
        long toWrite = 512 - (written % 512);
        if (toWrite < 512) bw.Write(new byte[toWrite]);
    }

    private void WriteDataSection(BinaryWriter bw, int nPoints, int analogPerFrame)
    {
        foreach (var frame in _frames)
        {
            var points = ComputePoints(frame);

            // Datos 3D en mm
            foreach (var p in points)
            {
                bw.Write(p.x * 1000f);
                bw.Write(p.y * 1000f);
                bw.Write(p.z * 1000f);
                bw.Write(0f);
            }

            WriteAnalogChannels(bw, frame);
        }
    }

    // Calcula los puntos 3D incluyendo los sinteticos de cabeza y muneca
    // La conversion de coordenadas es necesaria porque OpenXR usa un sistema
    // left-handed mientras que C3D usa right-handed con el suelo en el plano XY
    private List<Vector3> ComputePoints(FrameData frame)
    {
        var pts = new List<Vector3>();

        Vector3 hmdPos = ConvertPos(frame.hmd.x, frame.hmd.y, frame.hmd.z);
        Quaternion hmdRot = ConvertRot(frame.hmd.qx, frame.hmd.qy, frame.hmd.qz, frame.hmd.qw);

        Vector3 leftPos = ConvertPos(frame.leftCtrl.x, frame.leftCtrl.y, frame.leftCtrl.z);
        Quaternion leftRot = ConvertRot(frame.leftCtrl.qx, frame.leftCtrl.qy, frame.leftCtrl.qz, frame.leftCtrl.qw);

        Vector3 rightPos = ConvertPos(frame.rightCtrl.x, frame.rightCtrl.y, frame.rightCtrl.z);
        Quaternion rightRot = ConvertRot(frame.rightCtrl.qx, frame.rightCtrl.qy, frame.rightCtrl.qz, frame.rightCtrl.qw);

        // HMD
        pts.Add(hmdPos);

        // Puntos sinteticos de la cabeza a izquierda y derecha del centro
        Vector3 headOffset = new Vector3(HEAD_HALF_WIDTH, 0, 0);
        pts.Add(hmdPos + hmdRot * headOffset);
        pts.Add(hmdPos - hmdRot * headOffset);

        // Puntos sinteticos de las munecas
        Vector3 wristOffset = new Vector3(WRIST_HALF_WIDTH, 0, 0);
        pts.Add(leftPos + leftRot * wristOffset);
        pts.Add(leftPos - leftRot * wristOffset);
        pts.Add(rightPos + rightRot * wristOffset);
        pts.Add(rightPos - rightRot * wristOffset);

        // Dedos - se usa el IndexTip si hay hand tracking disponible
        if (_hasHandTracking && frame.leftJoints != null && frame.leftJoints.Length > 10)
            pts.Add(ConvertPos(frame.leftJoints[10].px, frame.leftJoints[10].py, frame.leftJoints[10].pz));
        else
            pts.Add(leftPos);

        if (_hasHandTracking && frame.rightJoints != null && frame.rightJoints.Length > 10)
            pts.Add(ConvertPos(frame.rightJoints[10].px, frame.rightJoints[10].py, frame.rightJoints[10].pz));
        else
            pts.Add(rightPos);

        // Joints de las manos
        if (_hasHandTracking)
        {
            if (frame.leftJoints != null)
                foreach (var j in frame.leftJoints)
                    pts.Add(ConvertPos(j.px, j.py, j.pz));
            else
                for (int i = 0; i < 26; i++) pts.Add(Vector3.zero);

            if (frame.rightJoints != null)
                foreach (var j in frame.rightJoints)
                    pts.Add(ConvertPos(j.px, j.py, j.pz));
            else
                for (int i = 0; i < 26; i++) pts.Add(Vector3.zero);
        }

        return pts;
    }

    private void WriteAnalogChannels(BinaryWriter bw, FrameData frame)
    {
        bw.Write(frame.timestamp);

        bw.Write(frame.hmd.qx); bw.Write(frame.hmd.qy);
        bw.Write(frame.hmd.qz); bw.Write(frame.hmd.qw);

        bw.Write(frame.leftCtrl.qx); bw.Write(frame.leftCtrl.qy);
        bw.Write(frame.leftCtrl.qz); bw.Write(frame.leftCtrl.qw);

        bw.Write(frame.rightCtrl.qx); bw.Write(frame.rightCtrl.qy);
        bw.Write(frame.rightCtrl.qz); bw.Write(frame.rightCtrl.qw);

        if (_hasHandTracking)
        {
            var lj = frame.leftJoints ?? new HandJoint[26];
            var rj = frame.rightJoints ?? new HandJoint[26];
            foreach (var j in lj) { bw.Write(j.qx); bw.Write(j.qy); bw.Write(j.qz); bw.Write(j.qw); }
            foreach (var j in rj) { bw.Write(j.qx); bw.Write(j.qy); bw.Write(j.qz); bw.Write(j.qw); }
        }
    }

    // Conversion de coordenadas OpenXR a C3D
    // OpenXR: left-handed, Y arriba, -Z adelante
    // C3D: right-handed, Z arriba, X adelante
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

    // Helpers para escribir parametros C3D
    private void WriteParamGroupStart(BinaryWriter bw, int groupId, string name, string desc)
    {
        bw.Write((byte)name.Length);
        bw.Write((byte)(-groupId));
        foreach (char c in name) bw.Write((byte)c);
        bw.Write((short)(desc.Length + 1));
        bw.Write((byte)desc.Length);
        foreach (char c in desc) bw.Write((byte)c);
    }

    private void WriteParamInt(BinaryWriter bw, int groupId, string name, short value)
    {
        bw.Write((byte)name.Length);
        bw.Write((byte)groupId);
        foreach (char c in name) bw.Write((byte)c);
        bw.Write((short)3); // next offset
        bw.Write((sbyte)2); // int16
        bw.Write((byte)1);  // 1 dimension
        bw.Write((byte)1);  // size 1
        bw.Write(value);
        bw.Write((byte)0);  // no description
    }

    private void WriteParamFloat(BinaryWriter bw, int groupId, string name, float value)
    {
        bw.Write((byte)name.Length);
        bw.Write((byte)groupId);
        foreach (char c in name) bw.Write((byte)c);
        bw.Write((short)3);
        bw.Write((sbyte)4); // float
        bw.Write((byte)1);
        bw.Write((byte)1);
        bw.Write(value);
        bw.Write((byte)0);
    }

    private void WriteParamString(BinaryWriter bw, int groupId, string name, string value)
    {
        bw.Write((byte)name.Length);
        bw.Write((byte)groupId);
        foreach (char c in name) bw.Write((byte)c);
        bw.Write((short)(value.Length + 4));
        bw.Write((sbyte)(-1)); // char
        bw.Write((byte)1);
        bw.Write((byte)value.Length);
        foreach (char c in value) bw.Write((byte)c);
        bw.Write((byte)0);
    }

    private void WriteParamStringArray(BinaryWriter bw, int groupId, string name, List<string> values)
    {
        int maxLen = 0;
        foreach (var v in values) if (v.Length > maxLen) maxLen = v.Length;

        bw.Write((byte)name.Length);
        bw.Write((byte)groupId);
        foreach (char c in name) bw.Write((byte)c);
        bw.Write((short)(maxLen * values.Count + 5));
        bw.Write((sbyte)(-1));
        bw.Write((byte)2);
        bw.Write((byte)maxLen);
        bw.Write((byte)values.Count);
        foreach (var v in values)
        {
            string padded = v.PadRight(maxLen);
            foreach (char c in padded) bw.Write((byte)c);
        }
        bw.Write((byte)0);
    }
}