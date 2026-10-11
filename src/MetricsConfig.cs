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
        //
        // Off by default: it detours every timed method with Harmony. Flip it on
        // for an attribution run, not as a standing production setting.
        public bool EnableModTiming = false;
        public string[] ModTimingAssemblyPrefixes = new[] { "TLAN", "PuckMetrics" };

        // Engine-stage timing via player-loop markers.
        public bool EnableStageTiming = true;

        // Sync-state/log-size/process gauges.
        public bool EnableEngineHealth = true;

        // Physics-step cadence from PhysicsManager.OnBeforeSimulate/OnAfterSimulate:
        // steps per frame, late steps, step span. No Harmony involved.
        public bool EnableTickTiming = true;

        // Harmony-timed SynchronizedObjectManager.Server_Tick (replication send).
        public bool EnableSyncTickTiming = true;

        // Unity Transport driver byte/packet counters and receive-stall watch.
        public bool EnableTransportStats = true;

        // /proc/self scheduler evidence (Linux only): main-thread run-queue
        // wait, CPU split, context switches, threads, RSS.
        public bool EnableProcStats = true;

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

                Debug.Log($"[PuckMetrics] Config loaded: port={config.Port}, interval={config.UpdateIntervalSeconds}s, " +
                          $"perPlayer={config.EnablePerPlayerMetrics}, modTiming={config.EnableModTiming}, " +
                          $"stageTiming={config.EnableStageTiming}, tickTiming={config.EnableTickTiming}, " +
                          $"syncTickTiming={config.EnableSyncTickTiming}, transportStats={config.EnableTransportStats}, " +
                          $"procStats={config.EnableProcStats}");
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
