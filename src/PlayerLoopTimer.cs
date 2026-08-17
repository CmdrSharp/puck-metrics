using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;

namespace PuckMetrics
{
    /// <summary>
    /// Times the top-level PlayerLoop stages (EarlyUpdate ≈ transport drain,
    /// FixedUpdate ≈ physics + replication, Update ≈ scripts, and so on) by
    /// inserting a begin marker as each stage's first child and an end marker
    /// as its last. Together with the per-mod timers this splits a slow frame
    /// into engine stages vs. mod scripts.
    ///
    /// Unity Netcode and others rebuild the player loop when they install
    /// their own systems, which silently drops these markers; the collector
    /// calls <see cref="EnsureInstalled"/> on its interval, which re-inserts
    /// them when they are gone. Totals live in this object, not the loop, so
    /// a reinstall never resets a counter.
    /// </summary>
    public class PlayerLoopTimer
    {
        private sealed class Stage
        {
            public string Name;
            public long BeginTimestamp;
            public long ElapsedTicks;
            public long MaxTicks;
        }

        // Marker types: only used to tag our systems inside the loop.
        private struct StageBegin { }
        private struct StageEnd { }

        private readonly Dictionary<string, Stage> _stages = new Dictionary<string, Stage>();
        private bool _loggedInstall;

        /// <summary>Install the markers if the current loop does not carry them.</summary>
        public void EnsureInstalled()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null)
                return;

            if (HasMarkers(ref loop))
                return;

            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var top = loop.subSystemList[i];
                var name = top.type != null ? top.type.Name : $"Stage{i}";

                if (!_stages.TryGetValue(name, out var stage))
                {
                    stage = new Stage { Name = name };
                    _stages[name] = stage;
                }

                var children = top.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var wrapped = new PlayerLoopSystem[children.Length + 2];

                var begin = new PlayerLoopSystem
                {
                    type = typeof(StageBegin),
                    updateDelegate = () => stage.BeginTimestamp = Stopwatch.GetTimestamp(),
                };
                var end = new PlayerLoopSystem
                {
                    type = typeof(StageEnd),
                    updateDelegate = () =>
                    {
                        long elapsed = Stopwatch.GetTimestamp() - stage.BeginTimestamp;
                        stage.ElapsedTicks += elapsed;
                        if (elapsed > stage.MaxTicks)
                            stage.MaxTicks = elapsed;
                    },
                };

                wrapped[0] = begin;
                Array.Copy(children, 0, wrapped, 1, children.Length);
                wrapped[children.Length + 1] = end;

                top.subSystemList = wrapped;
                loop.subSystemList[i] = top;
            }

            PlayerLoop.SetPlayerLoop(loop);

            if (!_loggedInstall)
            {
                _loggedInstall = true;
                Debug.Log($"[PuckMetrics] Stage timing installed on {loop.subSystemList.Length} player-loop stage(s).");
            }
        }

        /// <summary>Strip the markers from the current loop.</summary>
        public void Uninstall()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null)
                return;

            var changed = false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var top = loop.subSystemList[i];
                if (top.subSystemList == null)
                    continue;

                var kept = new List<PlayerLoopSystem>(top.subSystemList.Length);
                foreach (var child in top.subSystemList)
                {
                    if (child.type == typeof(StageBegin) || child.type == typeof(StageEnd))
                    {
                        changed = true;
                        continue;
                    }

                    kept.Add(child);
                }

                top.subSystemList = kept.ToArray();
                loop.subSystemList[i] = top;
            }

            if (changed)
                PlayerLoop.SetPlayerLoop(loop);
        }

        /// <summary>Hand each stage's interval totals to the exporter and reset them.</summary>
        public void Collect(Action<string, double, double> emitSecondsAndMax)
        {
            double toSeconds = 1.0 / Stopwatch.Frequency;

            foreach (var stage in _stages.Values)
            {
                if (stage.ElapsedTicks == 0 && stage.MaxTicks == 0)
                    continue;

                emitSecondsAndMax(stage.Name, stage.ElapsedTicks * toSeconds, stage.MaxTicks * toSeconds);

                stage.ElapsedTicks = 0;
                stage.MaxTicks = 0;
            }
        }

        private static bool HasMarkers(ref PlayerLoopSystem loop)
        {
            foreach (var top in loop.subSystemList)
            {
                if (top.subSystemList == null)
                    continue;

                foreach (var child in top.subSystemList)
                {
                    if (child.type == typeof(StageBegin))
                        return true;
                }
            }

            return false;
        }
    }
}
