using System;
using System.IO;
using UnityEngine;

public class ConfigReader
{
    public string Endpoint { get; private set; } = "";
    public string ApiKey { get; private set; } = "";
    public int FramesPerFile { get; private set; } = 150;
    public int FrameRate { get; private set; } = 72;
    public bool HandTracking { get; private set; } = true;
    public bool PrimaryButton { get; private set; } = true;
    public bool SecondaryButton { get; private set; } = true;
    public bool Grip { get; private set; } = true;
    public bool Trigger { get; private set; } = true;
    public bool Joystick { get; private set; } = true;

    public void ReadConfig()
    {
        // El archivo de configuracion se busca en el directorio persistente de la app
        // igual que hace Miguel Angel en su ConfigReader de C++
        string path = Path.Combine(Application.persistentDataPath, "initialConfig.json");

        if (!File.Exists(path))
        {
            Debug.LogWarning("ConfigReader: no se encontro initialConfig.json en " + path + ", usando valores por defecto");
            return;
        }

        string json = File.ReadAllText(path);
        var data = JsonUtility.FromJson<ConfigData>(json);

        // Solo sobreescribir los valores que vengan en el JSON
        // si no vienen se quedan los valores por defecto de arriba
        if (!string.IsNullOrEmpty(data.endpoint)) Endpoint = data.endpoint;
        if (!string.IsNullOrEmpty(data.apiKey)) ApiKey = data.apiKey;
        if (data.framesPerFile > 0) FramesPerFile = data.framesPerFile;
        if (data.frameRate > 0) FrameRate = data.frameRate;
        HandTracking = data.handTracking;
        PrimaryButton = data.primaryButton;
        SecondaryButton = data.secondaryButton;
        Grip = data.grip;
        Trigger = data.trigger;
        Joystick = data.joystick;

        Debug.Log("ConfigReader: configuracion cargada desde " + path);
    }

    // Devuelve un bitmask con las features activas
    // el mismo formato que usa la libreria nativa de Miguel Angel
    public uint GetFeatureFlagsBitmask()
    {
        uint flags = 0;
        if (HandTracking) flags |= (1u << 0);
        if (PrimaryButton) flags |= (1u << 1);
        if (SecondaryButton) flags |= (1u << 2);
        if (Grip) flags |= (1u << 3);
        if (Trigger) flags |= (1u << 4);
        if (Joystick) flags |= (1u << 5);
        return flags;
    }

    // Estructura que mapea directamente con el JSON de configuracion
    [Serializable]
    private class ConfigData
    {
        public string endpoint;
        public string apiKey;
        public int framesPerFile;
        public int frameRate;
        public bool handTracking;
        public bool primaryButton;
        public bool secondaryButton;
        public bool grip;
        public bool trigger;
        public bool joystick;
    }
}