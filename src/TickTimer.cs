using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PuckMetrics
{
    /// <summary>
    /// Physics-step cadence on B1235+: the engine runs PhysX from Unity's
    /// fixed-step loop (100 Hz) and the replication send
    /// (SynchronizedObjectManager.Server_Tick) is driven from the same step via
    /// PhysicsManager.OnAfterSimulate. Two things make clients stutter and
    /// neither shows in plain frame-time stats:
    ///
    ///  * a long frame makes Unity run several fixed steps back-to-back to
    ///    catch up, so several state sends leave in one frame (a gap, then a
    ///    burst) — counted here as bunched frames / catch-up steps;
    ///  * the wall-clock gap between steps stretches (the process was not
    ///    scheduled, or a frame overran) — measured as the step interval.
    ///
    /// Handlers run on the Unity main thread, allocate nothing and never throw:
    /// an exception in a multicast handler stops the chain and would skip every
    /// subscriber after us, including the engine's own replication tick.
    /// Nothing depends on our position in the invocation list.
    /// </summary>
    public sealed class TickTimer
    {
        public struct Snapshot
        {
            public long Steps;
            public double StepSeconds;
            public double StepMaxSeconds;
            public long Intervals;
            public double IntervalAvgSeconds;
            public double IntervalP99Seconds;
            public double IntervalMaxSeconds;
            public long LateOver2x;
            public long LateOver5x;
            public long SlowMoSteps;
            public long BunchedFrames;
            public long CatchUpSteps;
            public int MaxStepsPerFrame;
            public float TimeScale;
            public float TimeScaleMin;
            public float TickInterval;
            public int TickRate;
        }

        private const int SampleCapacity = 2048;

        // Per-step state.
        private long _beforeTs;
        private long _lastBeforeTs;
        private int _stepsThisFrame;

        // Interval accumulators.
        private long _stepCount;
        private long _stepElapsedTicks;
        private long _stepMaxTicks;
        private long _intervalSumTicks;
        private long _intervalMaxTicks;
        private long _intervalCount;
        private long _lateOver2x;
        private long _lateOver5x;
        private long _slowMoSteps;
        private long _bunchedFrames;
        private long _catchUpSteps;
        private int _maxStepsPerFrame;
        private float _timeScaleLast = 1f;
        private float _timeScaleMin = 1f;

        private readonly long[] _intervalSamples = new long[SampleCapacity];
        private int _sampleCount;

        // 1 / TickRate in Stopwatch ticks, refreshed at every Collect.
        private long _tickTicks;
        private float _tickInterval = 0.01f;
        private int _tickRate = 100;

        private bool _subscribed;

        public TickTimer()
        {
            RefreshTickInterval();
        }

        public void Subscribe()
        {
            if (_subscribed) return;
            PhysicsManager.OnBeforeSimulate += OnBeforeSimulate;
            PhysicsManager.OnAfterSimulate += OnAfterSimulate;
            _subscribed = true;
            Debug.Log("[PuckMetrics] Tick timing subscribed to PhysicsManager.OnBeforeSimulate/OnAfterSimulate.");
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            PhysicsManager.OnBeforeSimulate -= OnBeforeSimulate;
            PhysicsManager.OnAfterSimulate -= OnAfterSimulate;
            _subscribed = false;
        }

        private void OnBeforeSimulate(float dt)
        {
            long now = Stopwatch.GetTimestamp();
            _beforeTs = now;
            _stepsThisFrame++;
            _stepCount++;

            float ts = Time.timeScale;
            _timeScaleLast = ts;
            if (ts < _timeScaleMin) _timeScaleMin = ts;

            if (_lastBeforeTs != 0)
            {
                long gap = now - _lastBeforeTs;

                // Slow motion (goal replays) legitimately spaces fixed steps by
                // fixedDeltaTime / timeScale in wall time; keep those out of the
                // "late" accounting so a replay never reads as a stall.
                if (ts < 0.999f)
                {
                    _slowMoSteps++;
                }
                else
                {
                    _intervalSumTicks += gap;
                    _intervalCount++;
                    if (gap > _intervalMaxTicks) _intervalMaxTicks = gap;
                    if (_sampleCount < SampleCapacity) _intervalSamples[_sampleCount++] = gap;

                    if (gap > _tickTicks * 2)
                    {
                        _lateOver2x++;
                        if (gap > _tickTicks * 5) _lateOver5x++;
                    }
                }
            }

            _lastBeforeTs = now;
        }

        private void OnAfterSimulate(float dt)
        {
            // An After with no matching Before (first step after subscribing,
            // or a build that fires the pair differently) is ignored.
            if (_beforeTs == 0) return;

            long elapsed = Stopwatch.GetTimestamp() - _beforeTs;
            _beforeTs = 0;
            _stepElapsedTicks += elapsed;
            if (elapsed > _stepMaxTicks) _stepMaxTicks = elapsed;
        }

        /// <summary>
        /// Called once per frame from Update. The Update stage always follows
        /// the FixedUpdate stage within a frame, so the count is exactly the
        /// number of fixed steps this frame ran (0 for most frames at a 360 fps
        /// cap against a 100 Hz step).
        /// </summary>
        public void OnFrame()
        {
            int n = _stepsThisFrame;
            _stepsThisFrame = 0;

            if (n > _maxStepsPerFrame) _maxStepsPerFrame = n;
            if (n >= 2)
            {
                _bunchedFrames++;
                _catchUpSteps += n - 1;
            }
        }

        /// <summary>Hand the interval totals to the exporter and reset them.</summary>
        public Snapshot Collect()
        {
            double toSeconds = 1.0 / Stopwatch.Frequency;

            var snap = new Snapshot
            {
                Steps = _stepCount,
                StepSeconds = _stepElapsedTicks * toSeconds,
                StepMaxSeconds = _stepMaxTicks * toSeconds,
                Intervals = _intervalCount,
                IntervalAvgSeconds = _intervalCount > 0 ? (_intervalSumTicks * toSeconds) / _intervalCount : 0,
                IntervalMaxSeconds = _intervalMaxTicks * toSeconds,
                LateOver2x = _lateOver2x,
                LateOver5x = _lateOver5x,
                SlowMoSteps = _slowMoSteps,
                BunchedFrames = _bunchedFrames,
                CatchUpSteps = _catchUpSteps,
                MaxStepsPerFrame = _maxStepsPerFrame,
                TimeScale = _timeScaleLast,
                TimeScaleMin = _timeScaleMin,
                TickInterval = _tickInterval,
                TickRate = _tickRate,
            };

            if (_sampleCount > 0)
            {
                // In-place sort of the fixed buffer: no allocation, main thread.
                Array.Sort(_intervalSamples, 0, _sampleCount);
                int idx = Math.Min((int)(_sampleCount * 0.99f), _sampleCount - 1);
                snap.IntervalP99Seconds = _intervalSamples[idx] * toSeconds;
            }

            _stepCount = 0;
            _stepElapsedTicks = 0;
            _stepMaxTicks = 0;
            _intervalSumTicks = 0;
            _intervalMaxTicks = 0;
            _intervalCount = 0;
            _lateOver2x = 0;
            _lateOver5x = 0;
            _slowMoSteps = 0;
            _bunchedFrames = 0;
            _catchUpSteps = 0;
            _maxStepsPerFrame = 0;
            _timeScaleMin = _timeScaleLast;
            _sampleCount = 0;

            RefreshTickInterval();
            return snap;
        }

        public float TickInterval => _tickInterval;

        private void RefreshTickInterval()
        {
            float interval = 0f;
            int rate = 0;
            try
            {
                var pm = MonoBehaviourSingleton<PhysicsManager>.Instance;
                if (pm != null)
                {
                    interval = pm.TickInterval;
                    rate = pm.TickRate;
                }
            }
            catch { }

            if (interval <= 0f) interval = Time.fixedDeltaTime;
            if (interval <= 0f) interval = 0.01f;
            if (rate <= 0) rate = (int)Math.Round(1f / interval);

            _tickInterval = interval;
            _tickRate = rate;
            _tickTicks = (long)(interval * Stopwatch.Frequency);
        }
    }
}
