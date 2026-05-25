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
        private readonly List<float> _frameTimeSamples = new List<float>(512);

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

        private void Awake()
        {
            _config = MetricsConfig.Load();
            _registry = new MetricRegistry();
            _startTime = Time.realtimeSinceStartup;

            DefineMetrics();
            SubscribeToEvents();

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
                "Static server information as labels", "server_name", "game_mode", "level", "max_players", "tick_rate");
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
                "Configured server network tick rate");
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
                "Total frames that exceeded target frame time");
            _physicsStarvationTotal = _registry.CreateCounter("puck_physics_starvation_total",
                "Total frames where physics simulation fell behind (>2x interval)");
            _avgPlayerPing = _registry.CreateGauge("puck_average_player_ping_milliseconds",
                "Average ping across all connected players");
            _gcCollectionsTotal = _registry.CreateCounter("puck_gc_collections_total",
                "Total GC collections", "generation");
            _gcMemoryBytes = _registry.CreateGauge("puck_gc_memory_bytes",
                "Total managed memory in bytes");
            _frameTimeRatio = _registry.CreateGauge("puck_frame_time_ratio",
                "Ratio of actual avg frame time to target (>1 = server lagging)");
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

            // Detect overruns: frame took longer than target
            float targetFrameTime = Application.targetFrameRate > 0
                ? 1f / Application.targetFrameRate
                : 0.005f;

            if (dt > targetFrameTime * 1.1f)
                _overrunFrames++;

            // Physics starvation: frame so long that physics would need to double-step (2x the 50Hz physics interval)
            if (dt > 0.04f)
                _physicsStarvationFrames++;

            // Periodic full collection
            if (Time.realtimeSinceStartup - _lastCollectTime >= _config.UpdateIntervalSeconds)
            {
                CollectMetrics();
                _lastCollectTime = Time.realtimeSinceStartup;
            }
        }

        private void CollectMetrics()
        {
            CollectServerInfo();
            CollectPlayerMetrics();
            CollectGameState();
            CollectPerformanceMetrics();
        }

        private void CollectServerInfo()
        {
            try
            {
                var serverManager = NetworkBehaviourSingleton<ServerManager>.Instance;
                if (serverManager == null) return;

                var config = serverManager.ServerConfig;

                _serverInfo.Set(1,
                    config.name ?? "",
                    config.gameMode ?? "",
                    config.level ?? "",
                    config.maxPlayers.ToString(),
                    config.tickRate.ToString());

                _serverUptime.Set(Time.realtimeSinceStartup - _startTime);
                _networkTickRate.Set(config.tickRate);
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

                // Per-player ping + average
                double pingSum = 0;
                int pingCount = 0;

                if (_config.EnablePerPlayerMetrics)
                    _playerPing.Reset();

                foreach (var player in players)
                {
                    var ping = player.Ping.Value;
                    pingSum += ping;
                    pingCount++;

                    if (_config.EnablePerPlayerMetrics)
                    {
                        _playerPing.Set(ping,
                            player.SteamId.Value.ToString(),
                            player.Username.Value.ToString());
                    }
                }

                _avgPlayerPing.Set(pingCount > 0 ? pingSum / pingCount : 0);
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

            _server?.Dispose();
            Debug.Log("[PuckMetrics] Shut down.");
        }
    }
}
