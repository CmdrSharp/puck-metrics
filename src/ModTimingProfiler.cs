using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace PuckMetrics
{
    /// <summary>
    /// Per-mod frame-cost attribution: Harmony-patches the MonoBehaviour
    /// Update/FixedUpdate/LateUpdate methods declared by the configured mod
    /// assemblies with a two-timestamp wrapper, and aggregates the time per
    /// (assembly, hook) for the collector to export.
    ///
    /// Cost per patched invocation is a detour, two monotonic clock reads and a
    /// reference-keyed dictionary lookup — a few hundred nanoseconds, no
    /// allocation. The fleet mods are single-driver designs, so the patched
    /// call count per frame stays in the tens.
    ///
    /// What this deliberately does not see: work a mod does inside Harmony
    /// hooks on game methods (that time lands in the engine stage that ran the
    /// hook, e.g. physics for collision callbacks) and coroutines. The
    /// PlayerLoop stage timers cover those in aggregate.
    ///
    /// The patch bodies are logic-free so a failure in them is not possible in
    /// practice; a method that will not patch is skipped with one log line and
    /// everything else proceeds.
    /// </summary>
    public class ModTimingProfiler
    {
        public sealed class Bucket
        {
            public readonly string Mod;
            public readonly string Hook;

            // Main-thread only: the patched methods and the collector both run
            // on the Unity main thread.
            public long ElapsedTicks;
            public long MaxTicks;

            public Bucket(string mod, string hook)
            {
                Mod = mod;
                Hook = hook;
            }
        }

        private static readonly string[] Hooks = { "Update", "FixedUpdate", "LateUpdate" };

        // Keyed by the original method, which Harmony hands to the shared
        // postfix. Fully built before any patch is applied and never mutated
        // afterwards, so lock-free reads from the patches are safe.
        private static readonly Dictionary<MethodBase, Bucket> Buckets =
            new Dictionary<MethodBase, Bucket>();

        private Harmony _harmony;
        private readonly List<Bucket> _all = new List<Bucket>();

        public int PatchedCount { get; private set; }

        public bool TryInstall(string harmonyId, IEnumerable<string> assemblyPrefixes)
        {
            var prefixes = new List<string>(assemblyPrefixes ?? Array.Empty<string>());
            if (prefixes.Count == 0)
                return false;

            _harmony = new Harmony(harmonyId);

            var prefix = new HarmonyMethod(typeof(ModTimingProfiler), nameof(TimingPrefix));
            var postfix = new HarmonyMethod(typeof(ModTimingProfiler), nameof(TimingPostfix));

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (!MatchesAny(name, prefixes))
                    continue;

                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters)
                        continue;
                    if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type))
                        continue;

                    foreach (var hook in Hooks)
                    {
                        var method = type.GetMethod(hook,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (method == null || method.IsAbstract)
                            continue;

                        var bucket = new Bucket(name, hook);
                        Buckets[method] = bucket;

                        try
                        {
                            _harmony.Patch(method, prefix, postfix);
                            _all.Add(bucket);
                            PatchedCount++;
                        }
                        catch (Exception ex)
                        {
                            Buckets.Remove(method);
                            Debug.LogWarning($"[PuckMetrics] Could not time {type.Name}.{hook}: {ex.Message}");
                        }
                    }
                }
            }

            Debug.Log($"[PuckMetrics] Mod timing active on {PatchedCount} method(s) across " +
                      $"assemblies matching [{string.Join(", ", prefixes)}].");
            return PatchedCount > 0;
        }

        public void Uninstall()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Unpatching mod timing failed: {ex.Message}");
            }

            Buckets.Clear();
            _all.Clear();
            PatchedCount = 0;
        }

        /// <summary>
        /// Hand every bucket's interval totals to the exporter and reset them.
        /// Seconds are aggregated per (mod, hook) by the caller keying on the
        /// bucket fields; buckets for the same mod+hook pair repeat per type.
        /// </summary>
        public void Collect(Action<string, string, double, double> emitSecondsAndMax)
        {
            double toSeconds = 1.0 / Stopwatch.Frequency;

            foreach (var bucket in _all)
            {
                if (bucket.ElapsedTicks == 0 && bucket.MaxTicks == 0)
                    continue;

                emitSecondsAndMax(bucket.Mod, bucket.Hook,
                    bucket.ElapsedTicks * toSeconds, bucket.MaxTicks * toSeconds);

                bucket.ElapsedTicks = 0;
                bucket.MaxTicks = 0;
            }
        }

        private static bool MatchesAny(string assemblyName, List<string> prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (!string.IsNullOrEmpty(prefix) &&
                    assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types ?? Array.Empty<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static void TimingPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void TimingPostfix(MethodBase __originalMethod, long __state)
        {
            if (!Buckets.TryGetValue(__originalMethod, out var bucket))
                return;

            long elapsed = Stopwatch.GetTimestamp() - __state;
            bucket.ElapsedTicks += elapsed;
            if (elapsed > bucket.MaxTicks)
                bucket.MaxTicks = elapsed;
        }
    }
}
