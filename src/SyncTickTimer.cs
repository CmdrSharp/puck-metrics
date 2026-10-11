using System;
using System.Diagnostics;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace PuckMetrics
{
    /// <summary>
    /// Times SynchronizedObjectManager.Server_Tick, the replication send that
    /// the engine runs once per physics step from PhysicsManager.OnAfterSimulate
    /// (snapshot capture, per-client send planning, RPC enqueue). Next to the
    /// TickTimer's before→after span this separates "physics + scripts" from
    /// "sending state to clients".
    ///
    /// Independent of the per-mod profiler (own Harmony id, own config flag).
    /// If Server_Tick throws, the engine swallows it and our postfix does not
    /// run; that sample is simply lost.
    /// </summary>
    public static class SyncTickTimer
    {
        private const string HarmonyId = "com.tlan.puckmetrics.synctick";

        private static Harmony _harmony;
        private static long _ticks;
        private static long _elapsedTicks;
        private static long _maxTicks;

        public static bool Installed { get; private set; }

        public static void Install()
        {
            if (Installed) return;

            try
            {
                var target = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_Tick", new[] { typeof(float) });
                if (target == null)
                {
                    Debug.LogWarning("[PuckMetrics] Sync tick timing off: SynchronizedObjectManager.Server_Tick(float) not found in this build.");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target,
                    new HarmonyMethod(typeof(SyncTickTimer), nameof(Prefix)),
                    new HarmonyMethod(typeof(SyncTickTimer), nameof(Postfix)));

                Installed = true;
                Debug.Log("[PuckMetrics] Sync tick timing patched SynchronizedObjectManager.Server_Tick.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Sync tick timing failed to install: {ex.Message}");
                _harmony = null;
            }
        }

        public static void Uninstall()
        {
            if (!Installed) return;

            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Unpatching sync tick timing failed: {ex.Message}");
            }

            _harmony = null;
            Installed = false;
        }

        /// <summary>Interval totals for the exporter; resets them.</summary>
        public static void Collect(out long ticks, out double seconds, out double maxSeconds)
        {
            double toSeconds = 1.0 / Stopwatch.Frequency;

            ticks = _ticks;
            seconds = _elapsedTicks * toSeconds;
            maxSeconds = _maxTicks * toSeconds;

            _ticks = 0;
            _elapsedTicks = 0;
            _maxTicks = 0;
        }

        private static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            _ticks++;
        }

        private static void Postfix(long __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state;
            _elapsedTicks += elapsed;
            if (elapsed > _maxTicks) _maxTicks = elapsed;
        }
    }
}
