using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class JSONUploader
{
    private string _endpoint = "";
    private string _apiKey = "";

    public void Initialize(string endpoint, string apiKey)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        Debug.Log("JSONUploader: configurado con endpoint " + endpoint);
    }

    // Manda un chunk de frames en JSON a Supabase via HTTP POST
    // Se usa una coroutine para no bloquear el hilo principal
    // igual que el worker thread que usa Miguel Angel en su implementacion en C++
    public IEnumerator UploadJSON(string jsonPayload)
    {
        if (string.IsNullOrEmpty(_endpoint))
        {
            Debug.LogWarning("JSONUploader: no hay endpoint configurado, saltando upload");
            yield break;
        }

        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);

        using (UnityWebRequest request = new UnityWebRequest(_endpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("apikey", _apiKey);
            request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
            request.SetRequestHeader("Prefer", "return=minimal");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
                Debug.Log("JSONUploader: chunk subido correctamente");
            else
                Debug.LogError("JSONUploader: error al subir - " + request.error);
        }
    }
}