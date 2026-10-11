using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace PuckMetrics
{
    public class MetricsDriver : MonoBehaviour
    {
        private MetricsConfig _config;
        private MetricRegistry _registry;
        private MetricsHttpServer _server;
        private float _lastCollectTime;
        private float _startTime;

        private int _frameCount;
        private float _frameTimeSum;
        private float _frameTimeMax;
        private int _overrunFrames;
        private int _physicsStarvationFrames;
        private int _stallFrames;
        private readonly List<float> _frameTimeSamples = new List<float>(512);

        // Attribution + engine health (Phase 2 instrumentation)
        private ModTimingProfiler _modTiming;
        private PlayerLoopTimer _stageTiming;
        private EngineHealthCollector _engineHealth;
        private float _lastSceneCountTime;
        private bool _modTimingPending;

        // B1235 tick-level collectors
        private TickTimer _tickTimer;
        private TransportStats _transport;
        private ProcStats _proc;
        private SimulateSubscriberTiming _subscribers;
        private double _lastTickCollectTime;

        // Physics tick interval (1 / PhysicsManager.TickRate), refreshed each
        // collection; the frame thresholds below are expressed in it.
        private float _tickInterval = 0.01f;

        // Collection is spread one collector per frame (phase -1 = idle) so the
        // interval tick never lands as a single multi-millisecond frame.
        private const int LastCollectPhase = 6;
        private int _collectPhase = -1;

        // Server info
        private MetricFamily _serverInfo;
        private MetricFamily _serverUptime;

        // Connections / Players
        private MetricFamily _playersConnected;
        private MetricFamily _playersByTeam;
        private MetricFamily _playersByRole;
        private MetricFamily _playerCapacityRatio;
        private MetricFamily _playerPing;

        // Game state
        private MetricFamily _gamePhase;
        private MetricFamily _gamePeriod;
        private MetricFamily _gameTickRemaining;
        private MetricFamily _gameOvertime;
        private MetricFamily _gameScore;
        private MetricFamily _gamePhaseIsWarmup;
        private MetricFamily _gamePhaseIsActive;

        // Events (counters)
        private MetricFamily _goalsTotal;
        private MetricFamily _gamesCompletedTotal;

        // Performance
        private MetricFamily _networkTickRate;
        private MetricFamily _actualFps;
        private MetricFamily _frameTimeAvg;
        private MetricFamily _frameTimeP90;
        private MetricFamily _frameTimeP99;
        private MetricFamily _frameTimeMax_metric;
        private MetricFamily _frameOverrunsTotal;
        private MetricFamily _physicsStarvationTotal;
        private MetricFamily _avgPlayerPing;
        private MetricFamily _gcCollectionsTotal;
        private MetricFamily _gcMemoryBytes;
        private MetricFamily _frameTimeRatio;
        private MetricFamily _stallFramesTotal;
        private MetricFamily _targetFrameRate;
        private MetricFamily _playerPingMax;
        private MetricFamily _playerPingMin;

        // Physics-step cadence
        private MetricFamily _physicsTickRate;
        private MetricFamily _physicsTickInterval;
        private MetricFamily _physicsStepsTotal;
        private MetricFamily _physicsStepsPerSecond;
        private MetricFamily _physicsStepSecondsTotal;
        private MetricFamily _physicsStepMaxSeconds;
        private MetricFamily _physicsStepIntervalAvg;
        private MetricFamily _physicsStepIntervalP99;
        private MetricFamily _physicsStepIntervalMax;
        private MetricFamily _physicsLateStepsTotal;
        private MetricFamily _physicsSlowMoStepsTotal;
        private MetricFamily _physicsBunchedFramesTotal;
        private MetricFamily _physicsCatchUpStepsTotal;
        private MetricFamily _physicsMaxStepsPerFrame;
        private MetricFamily _timeScale;
        private MetricFamily _timeScaleMin;

        // Replication send
        private MetricFamily _syncTicksTotal;
        private MetricFamily _syncTickSecondsTotal;
        private MetricFamily _syncTickMaxSeconds;

        // Transport
        private MetricFamily _transportTxBytes;
        private MetricFamily _transportRxBytes;
        private MetricFamily _transportTxPackets;
        private MetricFamily _transportRxPackets;
        private MetricFamily _transportRxStallMax;
        private MetricFamily _transportDriverResets;

        // Process / scheduler
        private MetricFamily _mainThreadCpuSeconds;
        private MetricFamily _mainThreadWaitSeconds;
        private MetricFamily _mainThreadTimeslices;
        private MetricFamily _processCpuSeconds;
        private MetricFamily _processThreads;
        private MetricFamily _processCtxtSwitches;
        private MetricFamily _processRssBytes;

        // Attribution
        private MetricFamily _modFrameSecondsTotal;
        private MetricFamily _modFrameMaxSeconds;
        private MetricFamily _stageSecondsTotal;
        private MetricFamily _stageMaxSeconds;

        // Engine health
        private MetricFamily _syncPlayerStates;
        private MetricFamily _connectedClients;
        private MetricFamily _networkObjects;
        private MetricFamily _pucksSpawned;
        private MetricFamily _sceneTransforms;
        private MetricFamily _logFileBytes;
        private MetricFamily _workingSetBytes;
        private MetricFamily _processHandles;

        private void Awake()
        {
            _config = MetricsConfig.Load();
            _registry = new MetricRegistry();
            _startTime = Time.realtimeSinceStartup;

            DefineMetrics();
            SubscribeToEvents();

            // Mod timing installs on the first Update, NOT here: plugin folders
            // load alphabetically and metrics/ sits mid-list, so a scan taken in
            // Awake misses every assembly that loads after us (pugs, rules,
            // sound, tags — the ones most worth timing). By the first frame all
            // plugins are loaded.
            _modTimingPending = _config.EnableModTiming;

            if (_config.EnableStageTiming)
                _stageTiming = new PlayerLoopTimer();

            if (_config.EnableEngineHealth)
                _engineHealth = new EngineHealthCollector();

            if (_config.EnableTickTiming)
            {
                _tickTimer = new TickTimer();
                _tickTimer.Subscribe();
                _tickInterval = _tickTimer.TickInterval;
            }

            if (_config.EnableSyncTickTiming)
                SyncTickTimer.Install();

            if (_config.EnableTransportStats)
                _transport = new TransportStats();

            if (_config.EnableProcStats)
            {
                if (ProcStats.Available)
                    _proc = new ProcStats();
                else
                    Debug.Log("[PuckMetrics] Proc stats off: /proc not available on this platform.");
            }

            _lastTickCollectTime = Time.realtimeSinceStartupAsDouble;

            try
            {
                _server = new MetricsHttpServer(_registry, _config.BindAddress, _config.Port);
                _server.Start();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PuckMetrics] Failed to start HTTP server: {ex.Message}");
            }
        }

        private void DefineMetrics()
        {
            // Server info
            _serverInfo = _registry.CreateGauge("puck_server_info",
                "Static server information as labels (tick_rate = configured serverConfig.tickRate, physics_tick_rate = PhysicsManager fixed-step rate)",
                "server_name", "game_mode", "level", "max_players", "tick_rate", "physics_tick_rate");
            _serverUptime = _registry.CreateGauge("puck_server_uptime_seconds",
                "Seconds since the server started");

            // Connections / Players
            _playersConnected = _registry.CreateGauge("puck_players_connected",
                "Total number of connected players");
            _playersByTeam = _registry.CreateGauge("puck_players_by_team",
                "Number of players per team", "team");
            _playersByRole = _registry.CreateGauge("puck_players_by_role",
                "Number of players per role", "role");
            _playerCapacityRatio = _registry.CreateGauge("puck_player_capacity_ratio",
                "Ratio of connected players to max players (0.0-1.0)");
            _playerPing = _registry.CreateGauge("puck_player_ping_milliseconds",
                "Per-player ping in milliseconds", "steam_id", "username");
            _playerPingMax = _registry.CreateGauge("puck_player_ping_max_milliseconds",
                "Highest ping among connected players (0 when empty)");
            _playerPingMin = _registry.CreateGauge("puck_player_ping_min_milliseconds",
                "Lowest ping among connected players (0 when empty)");

            // Game state
            _gamePhase = _registry.CreateGauge("puck_game_phase",
                "Current game phase (1 for active phase)", "phase");
            _gamePeriod = _registry.CreateGauge("puck_game_period",
                "Current period number");
            _gameTickRemaining = _registry.CreateGauge("puck_game_tick_seconds_remaining",
                "Seconds remaining in current phase");
            _gameOvertime = _registry.CreateGauge("puck_game_overtime",
                "Whether the game is in overtime (0 or 1)");
            _gameScore = _registry.CreateGauge("puck_game_score",
                "Current score per team", "team");
            _gamePhaseIsWarmup = _registry.CreateGauge("puck_game_phase_is_warmup",
                "1 if server is in warmup phase (accepting players)");
            _gamePhaseIsActive = _registry.CreateGauge("puck_game_phase_is_active",
                "1 if game is in active play");

            // Events
            _goalsTotal = _registry.CreateCounter("puck_goals_total",
                "Total goals scored", "team");
            _gamesCompletedTotal = _registry.CreateCounter("puck_games_completed_total",
                "Total games completed");

            // Performance
            _networkTickRate = _registry.CreateGauge("puck_network_tick_rate",
                "Replication send rate: equals the physics fixed-step rate (PhysicsManager.TickRate) since B1235");
            _targetFrameRate = _registry.CreateGauge("puck_target_frame_rate",
                "Application.targetFrameRate (serverConfig.tickRate on a dedicated server); the main-loop cap, not the send rate");
            _actualFps = _registry.CreateGauge("puck_actual_fps",
                "Actual server frames per second (smoothed)");
            _frameTimeAvg = _registry.CreateGauge("puck_frame_time_avg_seconds",
                "Average frame time over the collection interval");
            _frameTimeP90 = _registry.CreateGauge("puck_frame_time_p90_seconds",
                "90th percentile frame time");
            _frameTimeP99 = _registry.CreateGauge("puck_frame_time_p99_seconds",
                "99th percentile frame time");
            _frameTimeMax_metric = _registry.CreateGauge("puck_frame_time_max_seconds",
                "Maximum frame time in the collection interval");
            _frameOverrunsTotal = _registry.CreateCounter("puck_frame_overruns_total",
                "Frames longer than one physics tick interval (at least one fixed step landed in the frame; likely bunching)");
            _physicsStarvationTotal = _registry.CreateCounter("puck_physics_starvation_total",
                "Frames longer than two physics tick intervals (Unity ran 2+ fixed steps and 2+ replication sends back-to-back)");
            _avgPlayerPing = _registry.CreateGauge("puck_average_player_ping_milliseconds",
                "Average ping across all connected players");
            _gcCollectionsTotal = _registry.CreateCounter("puck_gc_collections_total",
                "Total GC collections", "generation");
            _gcMemoryBytes = _registry.CreateGauge("puck_gc_memory_bytes",
                "Total managed memory in bytes");
            _frameTimeRatio = _registry.CreateGauge("puck_frame_time_ratio",
                "Ratio of actual avg frame time to the target frame budget (1/targetFrameRate); >1 = main loop cannot hold its cap");
            _stallFramesTotal = _registry.CreateCounter("puck_frames_over_50ms_total",
                "Total frames that took longer than 50ms (visible hitches)");

            // Attribution
            _modFrameSecondsTotal = _registry.CreateCounter("puck_mod_frame_seconds_total",
                "Main-thread seconds spent in a mod's script hooks", "mod", "hook");
            _modFrameMaxSeconds = _registry.CreateGauge("puck_mod_frame_max_seconds",
                "Slowest single invocation of a mod's script hook in the last interval", "mod", "hook");
            _stageSecondsTotal = _registry.CreateCounter("puck_stage_seconds_total",
                "Main-thread seconds spent per player-loop stage", "stage");
            _stageMaxSeconds = _registry.CreateGauge("puck_stage_max_seconds",
                "Slowest single pass of a player-loop stage in the last interval", "stage");

            // Engine health
            _syncPlayerStates = _registry.CreateGauge("puck_sync_player_states",
                "SynchronizedPlayerState entries held by the replication manager (should equal connected clients)");
            _connectedClients = _registry.CreateGauge("puck_connected_clients",
                "Netcode connected clients");
            _networkObjects = _registry.CreateGauge("puck_network_objects",
                "Spawned NetworkObjects");
            _pucksSpawned = _registry.CreateGauge("puck_pucks_spawned",
                "Live puck objects");
            _sceneTransforms = _registry.CreateGauge("puck_scene_transforms",
                "Transforms in the scene (sampled on a slow cadence)");
            _logFileBytes = _registry.CreateGauge("puck_log_file_bytes",
                "Size of the game's own Puck.log");
            _workingSetBytes = _registry.CreateGauge("puck_process_working_set_bytes",
                "Resident memory of the server process");
            _processHandles = _registry.CreateGauge("puck_process_handles",
                "OS handle count of the server process (where supported)");

            // Physics-step cadence (B1235: PhysX runs from Unity's fixed-step
            // loop and the replication send is coupled to it)
            _physicsTickRate = _registry.CreateGauge("puck_physics_tick_rate",
                "Physics fixed-step rate (PhysicsManager.TickRate); the replication send rate is coupled to it");
            _physicsTickInterval = _registry.CreateGauge("puck_physics_tick_interval_seconds",
                "Nominal seconds between fixed steps (1 / tick_rate)");
            _physicsStepsTotal = _registry.CreateCounter("puck_physics_steps_total",
                "Fixed physics steps executed");
            _physicsStepsPerSecond = _registry.CreateGauge("puck_physics_steps_per_second",
                "Fixed steps achieved per wall-clock second over the interval; below tick_rate * time_scale means simulation time is being discarded");
            _physicsStepSecondsTotal = _registry.CreateCounter("puck_physics_step_seconds_total",
                "Main-thread seconds between OnBeforeSimulate and OnAfterSimulate: PhysX + other FixedUpdate scripts + collision callbacks + earlier OnAfterSimulate subscribers");
            _physicsStepMaxSeconds = _registry.CreateGauge("puck_physics_step_max_seconds",
                "Longest single before->after span in the interval");
            _physicsStepIntervalAvg = _registry.CreateGauge("puck_physics_step_interval_avg_seconds",
                "Average wall-clock gap between consecutive fixed steps (slow-motion steps excluded)");
            _physicsStepIntervalP99 = _registry.CreateGauge("puck_physics_step_interval_p99_seconds",
                "99th percentile wall-clock gap between consecutive fixed steps (slow-motion steps excluded)");
            _physicsStepIntervalMax = _registry.CreateGauge("puck_physics_step_interval_max_seconds",
                "Longest wall-clock gap between consecutive fixed steps in the interval (slow-motion steps excluded)");
            _physicsLateStepsTotal = _registry.CreateCounter("puck_physics_late_steps_total",
                "Steps whose gap from the previous step exceeded N times the tick interval", "threshold");
            _physicsSlowMoStepsTotal = _registry.CreateCounter("puck_physics_slowmo_steps_total",
                "Steps taken with Time.timeScale < 1 (goal replays); excluded from late-step accounting");
            _physicsBunchedFramesTotal = _registry.CreateCounter("puck_physics_bunched_frames_total",
                "Frames that ran 2 or more fixed steps (catch-up); what clients feel as stutter");
            _physicsCatchUpStepsTotal = _registry.CreateCounter("puck_physics_catchup_steps_total",
                "Extra steps beyond the first in bunched frames");
            _physicsMaxStepsPerFrame = _registry.CreateGauge("puck_physics_max_steps_per_frame",
                "Most fixed steps run in one frame during the interval (1 = healthy; Unity caps near maximumDeltaTime / fixedDeltaTime)");
            _timeScale = _registry.CreateGauge("puck_time_scale",
                "Time.timeScale seen at the last fixed step");
            _timeScaleMin = _registry.CreateGauge("puck_time_scale_min",
                "Lowest Time.timeScale seen in the interval");

            // Replication send
            _syncTicksTotal = _registry.CreateCounter("puck_sync_ticks_total",
                "Replication send ticks (SynchronizedObjectManager.Server_Tick calls)");
            _syncTickSecondsTotal = _registry.CreateCounter("puck_sync_tick_seconds_total",
                "Main-thread seconds inside Server_Tick (snapshot capture + per-client plan + RPC send)");
            _syncTickMaxSeconds = _registry.CreateGauge("puck_sync_tick_max_seconds",
                "Slowest Server_Tick in the interval");

            // Transport
            _transportTxBytes = _registry.CreateCounter("puck_transport_tx_bytes_total",
                "UTP driver wire bytes sent, accumulated across driver restarts");
            _transportRxBytes = _registry.CreateCounter("puck_transport_rx_bytes_total",
                "UTP driver wire bytes received, accumulated across driver restarts");
            _transportTxPackets = _registry.CreateCounter("puck_transport_tx_packets_total",
                "UTP driver packets sent, accumulated across driver restarts");
            _transportRxPackets = _registry.CreateCounter("puck_transport_rx_packets_total",
                "UTP driver packets received, accumulated across driver restarts");
            _transportRxStallMax = _registry.CreateGauge("puck_transport_rx_stall_max_seconds",
                "Longest span in the interval during which the driver's received-packet count did not change");
            _transportDriverResets = _registry.CreateCounter("puck_transport_driver_resets_total",
                "Times the UTP driver statistics restarted from zero");

            // Process / scheduler (Linux /proc)
            _mainThreadCpuSeconds = _registry.CreateCounter("puck_main_thread_cpu_seconds_total",
                "CPU time consumed by the Unity main thread (/proc/self/schedstat run time)");
            _mainThreadWaitSeconds = _registry.CreateCounter("puck_main_thread_runqueue_wait_seconds_total",
                "Time the main thread was runnable but waiting for a CPU (/proc/self/schedstat); a rising rate is CPU contention or steal");
            _mainThreadTimeslices = _registry.CreateCounter("puck_main_thread_timeslices_total",
                "Timeslices the main thread ran on a CPU (/proc/self/schedstat)");
            _processCpuSeconds = _registry.CreateCounter("puck_process_cpu_seconds_total",
                "CPU seconds of the whole server process by mode (/proc/self/stat)", "mode");
            _processThreads = _registry.CreateGauge("puck_process_threads",
                "Threads in the server process");
            _processCtxtSwitches = _registry.CreateCounter("puck_process_ctxt_switches_total",
                "Context switches of the main thread by type; nonvoluntary = preempted", "type");
            _processRssBytes = _registry.CreateGauge("puck_process_rss_bytes",
                "Resident set size of the server process (/proc/self/statm)");
        }

        private void SubscribeToEvents()
        {
            try
            {
                EventManager.AddEventListener("Event_Everyone_OnGoalScored", OnGoalScored);
                EventManager.AddEventListener("Event_Everyone_OnGameStateChanged", OnGameStateChanged);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Failed to subscribe to events: {ex.Message}");
            }
        }

        private void OnGoalScored(Dictionary<string, object> data)
        {
            try
            {
                if (!data.TryGetValue("byTeam", out var teamObj)) return;
                var team = (PlayerTeam)teamObj;

                _goalsTotal.Inc(1, team.ToString().ToLowerInvariant());
            }
            catch { }
        }

        private void OnGameStateChanged(Dictionary<string, object> data)
        {
            try
            {
                if (!data.TryGetValue("newGameState", out var stateObj)) return;
                var newState = (GameState)stateObj;

                if (newState.Phase == GamePhase.PostGame)
                {
                    _gamesCompletedTotal.Inc(1);
                }
            }
            catch { }
        }

        private void Update()
        {
            // Per-frame performance tracking
            float dt = Time.deltaTime;
            _frameCount++;
            _frameTimeSum += dt;
            if (dt > _frameTimeMax) _frameTimeMax = dt;
            _frameTimeSamples.Add(dt);

            // Thresholds are in physics tick intervals (10 ms at 100 Hz): a
            // frame longer than one interval contains a fixed step and very
            // likely two; longer than two intervals guarantees Unity ran 2+
            // steps (and 2+ replication sends) back-to-back.
            if (dt > _tickInterval)
                _overrunFrames++;

            if (dt > 2f * _tickInterval)
                _physicsStarvationFrames++;

            if (dt > 0.05f)
                _stallFrames++;

            _tickTimer?.OnFrame();
            _transport?.Sample();

            if (_modTimingPending)
            {
                _modTimingPending = false;
                InstallModTiming();
            }

            // Re-arms the stage markers too: Netcode rebuilds the player loop
            // when it installs its own systems, which drops ours.
            _stageTiming?.EnsureInstalled();

            // Periodic collection, one collector per frame: a full pass in a
            // single frame costs several milliseconds against a 2.8ms budget.
            if (_collectPhase < 0 &&
                Time.realtimeSinceStartup - _lastCollectTime >= _config.UpdateIntervalSeconds)
            {
                _lastCollectTime = Time.realtimeSinceStartup;
                _collectPhase = 0;
            }

            if (_collectPhase >= 0)
            {
                RunCollectPhase(_collectPhase);
                _collectPhase = _collectPhase < LastCollectPhase ? _collectPhase + 1 : -1;
            }
        }

        private void InstallModTiming()
        {
            try
            {
                _modTiming = new ModTimingProfiler();
                if (!_modTiming.TryInstall("com.tlan.puckmetrics.timing", _config.ModTimingAssemblyPrefixes))
                    _modTiming = null;
                else
                    _subscribers = new SimulateSubscriberTiming(_modTiming, _config.ModTimingAssemblyPrefixes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PuckMetrics] Mod timing failed to install: {ex.Message}");
                _modTiming = null;
            }
        }

        private void RunCollectPhase(int phase)
        {
            switch (phase)
            {
                case 0: CollectServerInfo(); break;
                case 1: CollectPlayerMetrics(); break;
                case 2: CollectGameState(); break;
                case 3:
                    CollectPerformanceMetrics();
                    CollectAttribution();
                    break;
                case 4: CollectEngineHealth(); break;
                case 5: CollectTickTiming(); break;
                case 6: CollectTransportAndProc(); break;
            }
        }

        private void CollectTickTiming()
        {
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                double wall = now - _lastTickCollectTime;
                _lastTickCollectTime = now;

                if (_tickTimer != null)
                {
                    var s = _tickTimer.Collect();
                    _tickInterval = s.TickInterval;

                    _physicsTickRate.Set(s.TickRate);
                    _physicsTickInterval.Set(s.TickInterval);
                    _physicsStepsTotal.Inc(s.Steps);
                    _physicsStepsPerSecond.Set(wall > 0 ? s.Steps / wall : 0);
                    _physicsStepSecondsTotal.Inc(s.StepSeconds);
                    _physicsStepMaxSeconds.Set(s.StepMaxSeconds);
                    _physicsStepIntervalAvg.Set(s.IntervalAvgSeconds);
                    _physicsStepIntervalP99.Set(s.IntervalP99Seconds);
                    _physicsStepIntervalMax.Set(s.IntervalMaxSeconds);
                    _physicsLateStepsTotal.Inc(s.LateOver2x, "2x");
                    _physicsLateStepsTotal.Inc(s.LateOver5x, "5x");
                    _physicsSlowMoStepsTotal.Inc(s.SlowMoSteps);
                    _physicsBunchedFramesTotal.Inc(s.BunchedFrames);
                    _physicsCatchUpStepsTotal.Inc(s.CatchUpSteps);
                    _physicsMaxStepsPerFrame.Set(s.MaxStepsPerFrame);
                    _timeScale.Set(s.TimeScale);
                    _timeScaleMin.Set(s.TimeScaleMin);
                }

                if (SyncTickTimer.Installed)
                {
                    long ticks; double seconds, maxSeconds;
                    SyncTickTimer.Collect(out ticks, out seconds, out maxSeconds);
                    _syncTicksTotal.Inc(ticks);
                    _syncTickSecondsTotal.Inc(seconds);
                    _syncTickMaxSeconds.Set(maxSeconds);
                }
            }
            catch { }
        }

        private void CollectTransportAndProc()
        {
            try
            {
                if (_transport != null)
                {
                    var t = _transport.Collect();
                    if (t.DriverAvailable)
                    {
                        _transportTxBytes.Set(t.TxBytes);
                        _transportRxBytes.Set(t.RxBytes);
                        _transportTxPackets.Set(t.TxPackets);
                        _transportRxPackets.Set(t.RxPackets);
                        _transportDriverResets.Set(t.DriverResets);
                    }
                    _transportRxStallMax.Set(t.RxStallMaxSeconds);
                }

                if (_proc != null)
                {
                    var p = new ProcStats.Sample();
                    if (_proc.TryRead(ref p))
                    {
                        if (p.HasSchedstat)
                        {
                            _mainThreadCpuSeconds.Set(p.MainRunSeconds);
                            _mainThreadWaitSeconds.Set(p.MainWaitSeconds);
                            _mainThreadTimeslices.Set(p.MainTimeslices);
                        }

                        if (p.HasStat)
                        {
                            _processCpuSeconds.Set(p.UserSeconds, "user");
                            _processCpuSeconds.Set(p.SystemSeconds, "system");
                        }

                        if (p.HasStatus)
                        {
                            _processThreads.Set(p.Threads);
                            _processCtxtSwitches.Set(p.VoluntaryCtxt, "voluntary");
                            _processCtxtSwitches.Set(p.NonvoluntaryCtxt, "nonvoluntary");
                        }

                        if (p.HasStatm)
                            _processRssBytes.Set(p.RssBytes);
                    }
                }
            }
            catch { }
        }

        private void CollectAttribution()
        {
            try
            {
                _subscribers?.Scan();

                _modTiming?.Collect((mod, hook, seconds, maxSeconds) =>
                {
                    _modFrameSecondsTotal.Inc(seconds, mod, hook);
                    _modFrameMaxSeconds.Set(maxSeconds, mod, hook);
                });

                _stageTiming?.Collect((stage, seconds, maxSeconds) =>
                {
                    _stageSecondsTotal.Inc(seconds, stage);
                    _stageMaxSeconds.Set(maxSeconds, stage);
                });
            }
            catch { }
        }

        private void CollectEngineHealth()
        {
            if (_engineHealth == null)
                return;

            try
            {
                var syncStates = _engineHealth.SyncPlayerStateCount();
                if (syncStates >= 0)
                    _syncPlayerStates.Set(syncStates);

                var networkManager = NetworkManager.Singleton;
                if (networkManager != null && networkManager.IsServer)
                {
                    _connectedClients.Set(networkManager.ConnectedClientsList.Count);

                    var spawned = networkManager.SpawnManager?.SpawnedObjects;
                    if (spawned != null)
                        _networkObjects.Set(spawned.Count);
                }

                var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (puckManager != null)
                    _pucksSpawned.Set(puckManager.GetPucks(false).Count);

                var logBytes = _engineHealth.LogFileBytes();
                if (logBytes >= 0)
                    _logFileBytes.Set(logBytes);

                var workingSet = _engineHealth.WorkingSetBytes();
                if (workingSet >= 0)
                    _workingSetBytes.Set(workingSet);

                var handles = _engineHealth.HandleCount();
                if (handles >= 0)
                    _processHandles.Set(handles);

                if (_config.SceneCountIntervalSeconds > 0 &&
                    Time.realtimeSinceStartup - _lastSceneCountTime >= _config.SceneCountIntervalSeconds)
                {
                    _lastSceneCountTime = Time.realtimeSinceStartup;
                    _sceneTransforms.Set(FindObjectsByType<Transform>(FindObjectsSortMode.None).Length);
                }
            }
            catch { }
        }

        private void CollectServerInfo()
        {
            try
            {
                var serverManager = NetworkBehaviourSingleton<ServerManager>.Instance;
                if (serverManager == null) return;

                var config = serverManager.ServerConfig;

                int physicsTickRate = 0;
                var physicsManager = MonoBehaviourSingleton<PhysicsManager>.Instance;
                if (physicsManager != null)
                    physicsTickRate = physicsManager.TickRate;
                else if (Time.fixedDeltaTime > 0f)
                    physicsTickRate = (int)Math.Round(1f / Time.fixedDeltaTime);

                _serverInfo.Set(1,
                    config.name ?? "",
                    config.gameMode ?? "",
                    config.level ?? "",
                    config.maxPlayers.ToString(),
                    config.tickRate.ToString(),
                    physicsTickRate.ToString());

                _serverUptime.Set(Time.realtimeSinceStartup - _startTime);
                _networkTickRate.Set(physicsTickRate);
                _targetFrameRate.Set(Application.targetFrameRate);
            }
            catch { }
        }

        private void CollectPlayerMetrics()
        {
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager == null) return;

                var players = playerManager.GetPlayers();
                int totalPlayers = players.Count;
                _playersConnected.Set(totalPlayers);

                // Capacity ratio
                var serverManager = NetworkBehaviourSingleton<ServerManager>.Instance;
                int maxPlayers = serverManager?.ServerConfig.maxPlayers ?? 12;
                _playerCapacityRatio.Set(maxPlayers > 0 ? (double)totalPlayers / maxPlayers : 0);

                // Per-team counts
                _playersByTeam.Reset();
                int blue = 0, red = 0, spec = 0, none = 0;

                foreach (var player in players)
                {
                    switch (player.GameState.Value.Team)
                    {
                        case PlayerTeam.Blue: blue++; break;
                        case PlayerTeam.Red: red++; break;
                        case PlayerTeam.Spectator: spec++; break;
                        default: none++; break;
                    }
                }

                _playersByTeam.Set(blue, "blue");
                _playersByTeam.Set(red, "red");
                _playersByTeam.Set(spec, "spectator");
                _playersByTeam.Set(none, "none");

                // Per-role counts
                _playersByRole.Reset();
                int attackers = 0, goalies = 0, noRole = 0;

                foreach (var player in players)
                {
                    switch (player.GameState.Value.Role)
                    {
                        case PlayerRole.Attacker: attackers++; break;
                        case PlayerRole.Goalie: goalies++; break;
                        default: noRole++; break;
                    }
                }

                _playersByRole.Set(attackers, "attacker");
                _playersByRole.Set(goalies, "goalie");
                _playersByRole.Set(noRole, "none");

                // Per-player ping + average / spread
                double pingSum = 0;
                int pingCount = 0;
                ulong pingMax = 0;
                ulong pingMin = ulong.MaxValue;

                if (_config.EnablePerPlayerMetrics)
                    _playerPing.Reset();

                foreach (var player in players)
                {
                    var ping = player.Ping.Value;
                    pingSum += ping;
                    pingCount++;
                    if (ping > pingMax) pingMax = ping;
                    if (ping < pingMin) pingMin = ping;

                    if (_config.EnablePerPlayerMetrics)
                    {
                        _playerPing.Set(ping,
                            player.SteamId.Value.ToString(),
                            player.Username.Value.ToString());
                    }
                }

                _avgPlayerPing.Set(pingCount > 0 ? pingSum / pingCount : 0);
                _playerPingMax.Set(pingCount > 0 ? pingMax : 0);
                _playerPingMin.Set(pingCount > 0 ? pingMin : 0);
            }
            catch { }
        }

        private void CollectGameState()
        {
            try
            {
                var gameManager = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameManager == null) return;

                var state = gameManager.GameState.Value;

                _gamePhase.Reset();
                _gamePhase.Set(1, state.Phase.ToString().ToLowerInvariant());

                _gamePeriod.Set(state.Period);
                _gameTickRemaining.Set(state.Tick);
                _gameOvertime.Set(state.IsOvertime ? 1 : 0);

                _gameScore.Set(state.BlueScore, "blue");
                _gameScore.Set(state.RedScore, "red");

                _gamePhaseIsWarmup.Set(state.Phase == GamePhase.Warmup ? 1 : 0);
                _gamePhaseIsActive.Set(state.Phase == GamePhase.Play ? 1 : 0);
            }
            catch { }
        }

        private void CollectPerformanceMetrics()
        {
            // Frame time statistics from samples
            if (_frameTimeSamples.Count > 0)
            {
                float avg = _frameTimeSum / _frameCount;

                _frameTimeAvg.Set(avg);
                _frameTimeMax_metric.Set(_frameTimeMax);
                _actualFps.Set(_frameCount > 0 ? 1.0 / avg : 0);

                // Percentiles - sort samples
                _frameTimeSamples.Sort();
                int count = _frameTimeSamples.Count;
                _frameTimeP90.Set(_frameTimeSamples[(int)(count * 0.90f)]);
                _frameTimeP99.Set(_frameTimeSamples[Math.Min((int)(count * 0.99f), count - 1)]);

                // Frame time ratio
                float targetFrameTime = Application.targetFrameRate > 0
                    ? 1f / Application.targetFrameRate
                    : 0.005f;

                _frameTimeRatio.Set(avg / targetFrameTime);
            }

            // Overruns and starvation (cumulative counters)
            _frameOverrunsTotal.Set(_overrunFrames);
            _physicsStarvationTotal.Set(_physicsStarvationFrames);
            _stallFramesTotal.Set(_stallFrames);

            // GC stats
            for (int gen = 0; gen <= GC.MaxGeneration; gen++)
            {
                _gcCollectionsTotal.Set(GC.CollectionCount(gen), gen.ToString());
            }

            _gcMemoryBytes.Set(GC.GetTotalMemory(false));

            // Reset per-interval tracking
            _frameCount = 0;
            _frameTimeSum = 0f;
            _frameTimeMax = 0f;
            _frameTimeSamples.Clear();
        }

        private void OnDestroy()
        {
            try
            {
                EventManager.RemoveEventListener("Event_Everyone_OnGoalScored", OnGoalScored);
                EventManager.RemoveEventListener("Event_Everyone_OnGameStateChanged", OnGameStateChanged);
            }
            catch { }

            _tickTimer?.Unsubscribe();
            SyncTickTimer.Uninstall();
            _transport?.Dispose();
            _modTiming?.Uninstall();
            _stageTiming?.Uninstall();
            _engineHealth?.Dispose();
            _server?.Dispose();
            Debug.Log("[PuckMetrics] Shut down.");
        }
    }
}
