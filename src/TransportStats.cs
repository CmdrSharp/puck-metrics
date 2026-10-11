using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Analytics;
using UnityEngine;

namespace PuckMetrics
{
    /// <summary>
    /// Wire-level counters from the Unity Transport driver, read the same way
    /// the engine's NetworkStatistics does (its server diagnostics are switched
    /// off in shipped builds). Also tracks the longest span in which the
    /// driver's received-packet count did not change: with a lobby of players
    /// sending input at hundreds of packets per second, a receive stall of even
    /// 100 ms means the socket was not drained (process not scheduled, or the
    /// transport thread starved).
    ///
    /// Driver totals restart from zero when NetworkManager is shut down and
    /// started again; deltas use the engine's rule (current &lt; last → take
    /// current) so the exported counters stay monotonic.
    /// </summary>
    public sealed class TransportStats
    {
        public struct Snapshot
        {
            public ulong TxBytes;
            public ulong RxBytes;
            public ulong TxPackets;
            public ulong RxPackets;
            public long DriverResets;
            public double RxStallMaxSeconds;
            public bool DriverAvailable;
        }

        private UnityTransport _transport;
        private bool _unavailable;

        private bool _hasLast;
        private DriverStatistics _last;

        private ulong _txBytes, _rxBytes, _txPackets, _rxPackets;
        private long _driverResets;

        // Receive-stall tracking, sampled every frame.
        private bool _hasPolled;
        private ulong _lastRxPackets;
        private double _lastRxChangeTime;
        private double _maxStall;

        /// <summary>Per-frame: advance the receive-stall watch. Cheap (one struct read).</summary>
        public void Sample()
        {
            if (_unavailable) return;

            DriverStatistics stats;
            if (!TryGet(out stats))
            {
                _hasPolled = false;
                return;
            }

            // An empty server receives nothing; a "stall" only means something
            // while at least one client is sending input.
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.ConnectedClientsList.Count == 0)
            {
                _hasPolled = false;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (!_hasPolled || stats.RxTotalPackets != _lastRxPackets)
            {
                _lastRxPackets = stats.RxTotalPackets;
                _lastRxChangeTime = now;
                _hasPolled = true;
            }

            double stall = now - _lastRxChangeTime;
            if (stall > _maxStall) _maxStall = stall;
        }

        /// <summary>Per-interval: accumulate driver deltas and hand over the totals.</summary>
        public Snapshot Collect()
        {
            var snap = new Snapshot();
            if (_unavailable) return snap;

            DriverStatistics stats;
            if (TryGet(out stats))
            {
                if (_hasLast)
                {
                    bool reset = stats.TxTotalBytes < _last.TxTotalBytes ||
                                 stats.RxTotalBytes < _last.RxTotalBytes ||
                                 stats.TxTotalPackets < _last.TxTotalPackets ||
                                 stats.RxTotalPackets < _last.RxTotalPackets;
                    if (reset) _driverResets++;

                    _txBytes += Delta(stats.TxTotalBytes, _last.TxTotalBytes);
                    _rxBytes += Delta(stats.RxTotalBytes, _last.RxTotalBytes);
                    _txPackets += Delta(stats.TxTotalPackets, _last.TxTotalPackets);
                    _rxPackets += Delta(stats.RxTotalPackets, _last.RxTotalPackets);
                }
                else
                {
                    // First reading after (re)start: fold in what the driver
                    // already counted so a plugin reload does not lose it.
                    _txBytes += stats.TxTotalBytes;
                    _rxBytes += stats.RxTotalBytes;
                    _txPackets += stats.TxTotalPackets;
                    _rxPackets += stats.RxTotalPackets;
                }

                _last = stats;
                _hasLast = true;
                snap.DriverAvailable = true;
            }
            else
            {
                _hasLast = false;
            }

            snap.TxBytes = _txBytes;
            snap.RxBytes = _rxBytes;
            snap.TxPackets = _txPackets;
            snap.RxPackets = _rxPackets;
            snap.DriverResets = _driverResets;
            snap.RxStallMaxSeconds = _maxStall;
            _maxStall = 0;

            return snap;
        }

        public void Dispose()
        {
            _transport = null;
        }

        private bool TryGet(out DriverStatistics stats)
        {
            stats = default(DriverStatistics);

            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening) return false;

                if (_transport == null)
                    _transport = nm.NetworkConfig?.NetworkTransport as UnityTransport;
                if (_transport == null) return false;

                // GetNetworkDriver returns by ref; keep it that way so the
                // driver struct is not copied.
                ref NetworkDriver driver = ref _transport.GetNetworkDriver();
                if (!driver.IsCreated)
                {
                    _transport = null;
                    return false;
                }

                stats = driver.GetStatistics();
                return true;
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Debug.LogWarning($"[PuckMetrics] Transport stats off: {ex.Message}");
                return false;
            }
        }

        private static ulong Delta(ulong current, ulong last)
        {
            return current < last ? current : current - last;
        }
    }
}
