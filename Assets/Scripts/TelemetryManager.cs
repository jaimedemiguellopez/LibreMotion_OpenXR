using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class TelemetryManager
{
    private ConfigReader _config;
    private C3DWriter _c3dWriter;
    private JSONUploader _jsonUploader;
    private MonoBehaviour _mono;

    private List<VRFrameData> _buffer = new List<VRFrameData>();
    private List<VRFrameData> _uploadChunk = new List<VRFrameData>();
    private string _sessionId = "";
    private string _deviceInfo = "";
    private bool _initialized = false;
    private double _t0 = -1;

    // Datos de un frame completo de captura
    public struct VRFrameData
    {
        public double timestamp;
        public float[] hmdPos;
        public float[] hmdRot;
        public float[] leftPos;
        public float[] leftRot;
        public float[] rightPos;
        public float[] rightRot;
        public C3DWriter.HandJoint[] leftJoints;
        public C3DWriter.HandJoint[] rightJoints;
    }

    public void Initialize(MonoBehaviour mono, ConfigReader config, string sessionId, string deviceInfo)
    {
        _mono = mono;
        _config = config;
        _sessionId = sessionId;
        _deviceInfo = deviceInfo;

        // Inicializar el escritor C3D con los parametros de configuracion
        _c3dWriter = new C3DWriter();
        _c3dWriter.Initialize(sessionId, _config.FrameRate, _config.HandTracking);

        // Inicializar el uploader JSON con el endpoint de Supabase
        _jsonUploader = new JSONUploader();
        _jsonUploader.Initialize(_config.Endpoint, _config.ApiKey);

        _initialized = true;
        Debug.Log("TelemetryManager: sesion iniciada " + sessionId);
    }

    public void RecordFrame(VRFrameData frame)
    {
        if (!_initialized) return;

        // El primer frame marca el tiempo cero de la sesion
        if (_t0 < 0) _t0 = frame.timestamp;
        frame.timestamp -= _t0;

        _buffer.Add(frame);

        // Convertir el frame al formato que entiende el C3DWriter
        var c3dFrame = new C3DWriter.FrameData
        {
            timestamp = (float)frame.timestamp,
            hmd = new C3DWriter.PointData
            {
                x = frame.hmdPos[0],
                y = frame.hmdPos[1],
                z = frame.hmdPos[2],
                qx = frame.hmdRot[0],
                qy = frame.hmdRot[1],
                qz = frame.hmdRot[2],
                qw = frame.hmdRot[3]
            },
            leftCtrl = new C3DWriter.PointData
            {
                x = frame.leftPos[0],
                y = frame.leftPos[1],
                z = frame.leftPos[2],
                qx = frame.leftRot[0],
                qy = frame.leftRot[1],
                qz = frame.leftRot[2],
                qw = frame.leftRot[3]
            },
            rightCtrl = new C3DWriter.PointData
            {
                x = frame.rightPos[0],
                y = frame.rightPos[1],
                z = frame.rightPos[2],
                qx = frame.rightRot[0],
                qy = frame.rightRot[1],
                qz = frame.rightRot[2],
                qw = frame.rightRot[3]
            },
            leftJoints = frame.leftJoints,
            rightJoints = frame.rightJoints
        };

        _c3dWriter.AddFrame(c3dFrame);

        // Cuando el buffer llega al limite configurado, se manda el chunk a Supabase
        if (_buffer.Count >= _config.FramesPerFile)
        {
            _uploadChunk = new List<VRFrameData>(_buffer);
            _buffer.Clear();
            _mono.StartCoroutine(_jsonUploader.UploadJSON(SerializeChunk(_uploadChunk)));
        }
    }

    public void Shutdown(string outputPath)
    {
        if (!_initialized) return;

        // Mandar los frames que quedaban en el buffer antes de cerrar
        if (_buffer.Count > 0)
        {
            _uploadChunk = new List<VRFrameData>(_buffer);
            _buffer.Clear();
            _mono.StartCoroutine(_jsonUploader.UploadJSON(SerializeChunk(_uploadChunk)));
        }

        // Escribir el archivo C3D final
        _c3dWriter.Write(outputPath);
        _initialized = false;
        Debug.Log("TelemetryManager: sesion cerrada, C3D guardado en " + outputPath);
    }

    // Serializa un chunk de frames a JSON para mandarlo a Supabase
    // El formato es el mismo que usa Miguel Angel en su implementacion
    private string SerializeChunk(List<VRFrameData> frames)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"session_id\":\"{_sessionId}\",");
        sb.Append($"\"device_info\":\"{_deviceInfo}\",");
        sb.Append("\"frames\":[");

        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            sb.Append("{");
            sb.Append($"\"timestamp\":{f.timestamp:F5},");

            // HMD
            sb.Append($"\"head_pose\":{{");
            sb.Append($"\"position\":[{f.hmdPos[0]:F5},{f.hmdPos[1]:F5},{f.hmdPos[2]:F5}],");
            sb.Append($"\"orientation\":[{f.hmdRot[0]:F5},{f.hmdRot[1]:F5},{f.hmdRot[2]:F5},{f.hmdRot[3]:F5}]");
            sb.Append("},");

            // Mano izquierda
            sb.Append($"\"left_hand\":{{");
            sb.Append($"\"position\":[{f.leftPos[0]:F5},{f.leftPos[1]:F5},{f.leftPos[2]:F5}],");
            sb.Append($"\"orientation\":[{f.leftRot[0]:F5},{f.leftRot[1]:F5},{f.leftRot[2]:F5},{f.leftRot[3]:F5}]");
            sb.Append("},");

            // Mano derecha
            sb.Append($"\"right_hand\":{{");
            sb.Append($"\"position\":[{f.rightPos[0]:F5},{f.rightPos[1]:F5},{f.rightPos[2]:F5}],");
            sb.Append($"\"orientation\":[{f.rightRot[0]:F5},{f.rightRot[1]:F5},{f.rightRot[2]:F5},{f.rightRot[3]:F5}]");
            sb.Append("}");

            // Joints de las manos si hay hand tracking
            if (f.leftJoints != null && f.leftJoints.Length > 0)
            {
                sb.Append(",\"hands\":{\"left\":{\"joint_count\":");
                sb.Append(f.leftJoints.Length);
                sb.Append(",\"joints\":[");
                for (int j = 0; j < f.leftJoints.Length; j++)
                {
                    var jt = f.leftJoints[j];
                    sb.Append($"{{\"id\":{jt.id},");
                    sb.Append($"\"position\":[{jt.px:F5},{jt.py:F5},{jt.pz:F5}],");
                    sb.Append($"\"orientation\":[{jt.qx:F5},{jt.qy:F5},{jt.qz:F5},{jt.qw:F5}],");
                    sb.Append($"\"has_pose\":{(jt.hasPose ? "true" : "false")}}}");
                    if (j < f.leftJoints.Length - 1) sb.Append(",");
                }
                sb.Append("]},\"right\":{\"joint_count\":");
                sb.Append(f.rightJoints?.Length ?? 0);
                sb.Append(",\"joints\":[");
                if (f.rightJoints != null)
                {
                    for (int j = 0; j < f.rightJoints.Length; j++)
                    {
                        var jt = f.rightJoints[j];
                        sb.Append($"{{\"id\":{jt.id},");
                        sb.Append($"\"position\":[{jt.px:F5},{jt.py:F5},{jt.pz:F5}],");
                        sb.Append($"\"orientation\":[{jt.qx:F5},{jt.qy:F5},{jt.qz:F5},{jt.qw:F5}],");
                        sb.Append($"\"has_pose\":{(jt.hasPose ? "true" : "false")}}}");
                        if (j < f.rightJoints.Length - 1) sb.Append(",");
                    }
                }
                sb.Append("]}}");
            }

            sb.Append("}");
            if (i < frames.Count - 1) sb.Append(",");
        }

        sb.Append("]}");
        return sb.ToString();
    }
}