using System;
using System.Collections.Generic;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace PuckMetrics
{
    /// <summary>
    /// Extends per-mod attribution to the PhysicsManager.OnBeforeSimulate /
    /// OnAfterSimulate subscribers. Mods that do per-step work (tlan-replay's
    /// world sampler, for instance) hang there rather than in a MonoBehaviour
    /// FixedUpdate, so the MonoBehaviour scan never sees them.
    ///
    /// Once per collection interval (never from inside a handler of the event
    /// being walked) the static events' backing delegates are read by
    /// reflection, their invocation lists walked, and each not-yet-timed
    /// target whose assembly matches the configured prefixes is Harmony-timed
    /// with the profiler's prefix/postfix pair under hook="OnAfterSimulate" /
    /// "OnBeforeSimulate".
    ///
    /// Patching a method that a delegate already points at is fine: the detour
    /// is a jump at the method's native entry, which is where the delegate's
    /// call lands too. Engine subscribers (Puck assembly) are deliberately not
    /// matched; SyncTickTimer covers the one that matters.
    /// </summary>
    public sealed class SimulateSubscriberTiming
    {
        private static readonly FieldInfo BeforeField =
            typeof(PhysicsManager).GetField("OnBeforeSimulate", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo AfterField =
            typeof(PhysicsManager).GetField("OnAfterSimulate", BindingFlags.NonPublic | BindingFlags.Static);

        private readonly ModTimingProfiler _profiler;
        private readonly List<string> _prefixes;

        // null value = assembly is not one we time.
        private readonly Dictionary<Assembly, string> _assemblyMatch = new Dictionary<Assembly, string>();
        private bool _unavailable;

        public SimulateSubscriberTiming(ModTimingProfiler profiler, IEnumerable<string> prefixes)
        {
            _profiler = profiler;
            _prefixes = new List<string>(prefixes ?? Array.Empty<string>());

            if (BeforeField == null || AfterField == null)
            {
                _unavailable = true;
                Debug.Log("[PuckMetrics] Simulate-subscriber timing off: PhysicsManager event fields not found in this build.");
            }
        }

        /// <summary>Walk both events and time any new mod subscriber. Allocates one array per event.</summary>
        public void Scan()
        {
            if (_unavailable || _profiler == null) return;

            try
            {
                ScanEvent(BeforeField, "OnBeforeSimulate");
                ScanEvent(AfterField, "OnAfterSimulate");
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Debug.LogWarning($"[PuckMetrics] Simulate-subscriber timing off: {ex.Message}");
            }
        }

        private void ScanEvent(FieldInfo field, string hook)
        {
            var del = field.GetValue(null) as Delegate;
            if (del == null) return;

            foreach (var d in del.GetInvocationList())
            {
                var method = d.Method;
                var asm = method.DeclaringType?.Assembly;
                if (asm == null) continue;

                string mod;
                if (!_assemblyMatch.TryGetValue(asm, out mod))
                {
                    mod = ModTimingProfiler.MatchPrefix(asm.GetName().Name, _prefixes);
                    _assemblyMatch[asm] = mod;
                }

                if (mod == null || _profiler.IsPatched(method)) continue;

                if (_profiler.TryPatchMethod(method, mod, hook))
                    Debug.Log($"[PuckMetrics] Timing {mod}.{method.DeclaringType.Name}.{method.Name} on {hook}.");
            }
        }
    }
}
