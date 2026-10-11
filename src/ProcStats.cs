using System;
using System.IO;
using UnityEngine;

namespace PuckMetrics
{
    /// <summary>
    /// Scheduler evidence from /proc (Linux only). The headline number is the
    /// main thread's run-queue wait: time it was runnable but not on a CPU.
    /// That is the in-process view of CPU contention and hypervisor steal, and
    /// it is what the frame/tick timers cannot distinguish from our own cost.
    ///
    /// Unity's main thread is the process's initial thread, so on Linux
    /// /proc/self/schedstat (thread-group leader) is exactly the game thread.
    /// The ctxt-switch lines of /proc/self/status are the leader's too.
    ///
    /// Four small reads per collection interval; parsing works on the raw
    /// strings with IndexOf/Substring so it binds the same on Mono and .NET
    /// (see the netstandard overload note in the repo's memory: avoid
    /// Split(' ') style overloads that only exist in newer frameworks).
    /// </summary>
    public sealed class ProcStats
    {
        public struct Sample
        {
            public bool HasSchedstat;
            public double MainRunSeconds;
            public double MainWaitSeconds;
            public long MainTimeslices;

            public bool HasStat;
            public double UserSeconds;
            public double SystemSeconds;

            public bool HasStatus;
            public int Threads;
            public long VoluntaryCtxt;
            public long NonvoluntaryCtxt;

            public bool HasStatm;
            public long RssBytes;
        }

        // USER_HZ is fixed at 100 on Linux regardless of the kernel's HZ;
        // sysconf(_SC_CLK_TCK) is not reachable without P/Invoke.
        private const double ClockTicksPerSecond = 100.0;

        public static readonly bool Available = File.Exists("/proc/self/stat");

        private bool _schedstatUnavailable;
        private readonly char[] _spaceSep = { ' ' };

        public bool TryRead(ref Sample s)
        {
            if (!Available) return false;

            ReadSchedstat(ref s);
            ReadStat(ref s);
            ReadStatus(ref s);
            ReadStatm(ref s);

            return s.HasSchedstat || s.HasStat || s.HasStatus || s.HasStatm;
        }

        private void ReadSchedstat(ref Sample s)
        {
            if (_schedstatUnavailable) return;

            try
            {
                // "<run_time_ns> <wait_time_ns> <timeslices>"
                var text = File.ReadAllText("/proc/self/schedstat");
                var parts = text.Trim().Split(_spaceSep, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) { _schedstatUnavailable = true; return; }

                s.MainRunSeconds = ParseLong(parts[0]) / 1e9;
                s.MainWaitSeconds = ParseLong(parts[1]) / 1e9;
                s.MainTimeslices = ParseLong(parts[2]);
                s.HasSchedstat = true;
            }
            catch
            {
                // Kernel without CONFIG_SCHED_INFO: drop just these gauges.
                _schedstatUnavailable = true;
            }
        }

        private void ReadStat(ref Sample s)
        {
            try
            {
                var text = File.ReadAllText("/proc/self/stat");
                // comm may contain spaces; everything after the last ')' is
                // space separated starting with field 3 (state).
                int close = text.LastIndexOf(')');
                if (close < 0) return;

                var parts = text.Substring(close + 1).Trim().Split(_spaceSep, StringSplitOptions.RemoveEmptyEntries);
                // parts[0] = field 3 (state) ... utime = field 14 → parts[11], stime = field 15 → parts[12]
                if (parts.Length < 13) return;

                s.UserSeconds = ParseLong(parts[11]) / ClockTicksPerSecond;
                s.SystemSeconds = ParseLong(parts[12]) / ClockTicksPerSecond;
                s.HasStat = true;
            }
            catch { }
        }

        private void ReadStatus(ref Sample s)
        {
            try
            {
                bool any = false;
                foreach (var line in File.ReadAllLines("/proc/self/status"))
                {
                    if (line.StartsWith("Threads:", StringComparison.Ordinal))
                    {
                        s.Threads = (int)ParseLong(line.Substring(8).Trim());
                        any = true;
                    }
                    else if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal))
                    {
                        s.VoluntaryCtxt = ParseLong(line.Substring(24).Trim());
                        any = true;
                    }
                    else if (line.StartsWith("nonvoluntary_ctxt_switches:", StringComparison.Ordinal))
                    {
                        s.NonvoluntaryCtxt = ParseLong(line.Substring(27).Trim());
                        any = true;
                    }
                }

                s.HasStatus = any;
            }
            catch { }
        }

        private void ReadStatm(ref Sample s)
        {
            try
            {
                // "<size> <resident> <shared> ..." in pages
                var text = File.ReadAllText("/proc/self/statm");
                var parts = text.Trim().Split(_spaceSep, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;

                s.RssBytes = ParseLong(parts[1]) * Environment.SystemPageSize;
                s.HasStatm = true;
            }
            catch { }
        }

        private static long ParseLong(string text)
        {
            long value;
            return long.TryParse(text, out value) ? value : 0;
        }
    }
}
