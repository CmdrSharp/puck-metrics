using System;
using System.IO;
using UnityEngine;

namespace PuckMetrics
{
    public class MetricsConfig
    {
        public int Port = 9100;
        public float UpdateIntervalSeconds = 2f;
        public bool EnablePerPlayerMetrics = false;
        public string BindAddress = "0.0.0.0";

        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "puck_metrics_config.json");

        public static MetricsConfig Load()
        {
            if (!File.Exists(ConfigPath))
            {
                Debug.Log($"[PuckMetrics] No config at {ConfigPath}, using defaults.");
                return new MetricsConfig();
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonUtility.FromJson<MetricsConfig>(json);

                Debug.Log($"[PuckMetrics] Config loaded: port={config.Port}, interval={config.UpdateIntervalSeconds}s, perPlayer={config.EnablePerPlayerMetrics}");
                return config;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Failed to load config: {ex.Message}. Using defaults.");
                return new MetricsConfig();
            }
        }
    }
}
