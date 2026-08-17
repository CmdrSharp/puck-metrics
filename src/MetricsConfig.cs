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

        // Per-mod frame-cost attribution: Harmony-time the Update/FixedUpdate/
        // LateUpdate methods of assemblies whose name starts with one of these
        // prefixes. Deliberately not "every mod": per-object third-party mods
        // would multiply the (tiny) per-call overhead, and their cost shows up
        // in the stage timers as the script-stage residual anyway.
        public bool EnableModTiming = true;
        public string[] ModTimingAssemblyPrefixes = new[] { "TLAN", "PuckMetrics" };

        // Engine-stage timing via player-loop markers.
        public bool EnableStageTiming = true;

        // Sync-state/log-size/process gauges.
        public bool EnableEngineHealth = true;

        // Scene-wide transform census cadence, 0 disables. The sweep is a
        // single ~10ms native call — one long frame per census — so it runs
        // rarely: it exists to catch a multi-day object leak, not to be a
        // live gauge.
        public float SceneCountIntervalSeconds = 300f;

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
