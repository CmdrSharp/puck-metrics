using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PuckMetrics
{
    /// <summary>
    /// Long-uptime engine gauges, reached by reflection so a build that lacks a
    /// field (this mod compiles against an older Puck than it runs on) degrades
    /// that one gauge instead of failing the collector.
    ///
    /// The headline gauge is the synchronized-player-state count next to the
    /// connected-client count: the engine's replication manager keeps a
    /// per-client send state whose removal silently no-ops when the Player was
    /// destroyed without a clean despawn, and each orphan costs a full planning
    /// pass every tick. Divergence between these two gauges is that leak.
    /// </summary>
    public class EngineHealthCollector
    {
        private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private Type _somType;
        private FieldInfo _somStatesField;
        private UnityEngine.Object _somInstance;
        private bool _somUnavailable;

        private string _logFilePath;
        private bool _logPathUnavailable;

        private System.Diagnostics.Process _process;
        private bool _handleCountUnavailable;

        /// <summary>Synchronized player states held by the replication manager, or -1.</summary>
        public int SyncPlayerStateCount()
        {
            if (_somUnavailable)
                return -1;

            try
            {
                if (_somType == null)
                {
                    _somType = Type.GetType("SynchronizedObjectManager, Assembly-CSharp");
                    _somStatesField = _somType?.GetField("serverSynchronizedPlayerStates", PrivateInstance);

                    if (_somType == null || _somStatesField == null)
                    {
                        _somUnavailable = true;
                        Debug.Log("[PuckMetrics] Sync-state gauge off: SynchronizedObjectManager." +
                                  "serverSynchronizedPlayerStates not found in this build.");
                        return -1;
                    }
                }

                if (_somInstance == null)
                    _somInstance = UnityEngine.Object.FindAnyObjectByType(_somType) as UnityEngine.Object;

                if (_somInstance == null)
                    return -1;

                var states = _somStatesField.GetValue(_somInstance) as ICollection;
                return states?.Count ?? -1;
            }
            catch (Exception ex)
            {
                _somUnavailable = true;
                Debug.LogWarning($"[PuckMetrics] Sync-state gauge off: {ex.Message}");
                return -1;
            }
        }

        /// <summary>Size of the game's own log file in bytes, or -1.</summary>
        public long LogFileBytes()
        {
            if (_logPathUnavailable)
                return -1;

            try
            {
                if (_logFilePath == null)
                {
                    _logFilePath = Type.GetType("LogManager, Assembly-CSharp")
                        ?.GetField("logFilePath", PrivateStatic)
                        ?.GetValue(null) as string;

                    if (string.IsNullOrEmpty(_logFilePath))
                    {
                        _logPathUnavailable = true;
                        return -1;
                    }
                }

                var info = new FileInfo(_logFilePath);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                _logPathUnavailable = true;
                return -1;
            }
        }

        /// <summary>Resident set of this process in bytes, or -1.</summary>
        public long WorkingSetBytes()
        {
            try
            {
                if (_process == null)
                    _process = System.Diagnostics.Process.GetCurrentProcess();

                _process.Refresh();
                return _process.WorkingSet64;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>OS handle count of this process, or -1 where unsupported.</summary>
        public int HandleCount()
        {
            if (_handleCountUnavailable)
                return -1;

            try
            {
                if (_process == null)
                    _process = System.Diagnostics.Process.GetCurrentProcess();

                return _process.HandleCount;
            }
            catch
            {
                _handleCountUnavailable = true;
                return -1;
            }
        }

        public void Dispose()
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
