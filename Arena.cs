// Requires: ArenaUI

using CompanionServer;
using Facepunch;
using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UI = Oxide.Plugins.ArenaUI.UI;
using UI4 = Oxide.Plugins.ArenaUI.UI4;

namespace Oxide.Plugins
{
    using ArenaEx;

    [Info("Arena", "EsC1337", "1.3.0"), Description("The core mechanics for arena combat games")]
    public class Arena : RustPlugin
    {
        #region Fields
        [PluginReference]
        internal Plugin Economics, Kits, NoEscape, RotatingPickups, ServerRewards, Spawns, ZoneManager;


        private int scrapItemId;

        private static Regex hexFilter;

        private static DropOnDeath dropOnDeath = DropOnDeath.Nothing;

        public Hash<string, IEventPlugin> EventModes { get; set; } = new Hash<string, IEventPlugin>();

        public Hash<string, BaseEventGame> ActiveEvents { get; set; } = new Hash<string, BaseEventGame>();


        public EventData Events { get; private set; }

        private RestoreData Restore { get; set; }

        private LobbyHandler Lobby { get; set; }

        public static Arena Instance { get; private set; }

        public static ConfigData Configuration { get; set; }


        public static bool IsUnloading { get; private set; }


        internal const string ADMIN_PERMISSION = "arena.admin";

        internal const string BLACKLISTED_PERMISSION = "arena.blacklisted";

        private const string RESPAWN_BAG_CMD = "respawn_sleepingbag";
        private const string REMOVE_BAG_CMD = "respawn_sleepingbag_remove";
        private const string RESPAWN_CMD = "respawn";

        private const uint LOBBY_BAG_ID = 113;
        #endregion

        #region Oxide Hooks
        private void Loaded()
        {
            LoadData();

            permission.RegisterPermission(ADMIN_PERMISSION, this);

            permission.RegisterPermission(BLACKLISTED_PERMISSION, this);

            hexFilter = new Regex("^([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$");

            Instance = this;

            IsUnloading = false;
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void OnServerInitialized()
        {
            if (!CheckDependencies())
                return;

            if (Configuration.Server.DisableServerEvents)
                ConVar.Server.events = false;

            if (!Configuration.Server.UseChat)
                Unsubscribe(nameof(OnPlayerChat));

            if (!Configuration.Event.AddToTeams)
            {
                Unsubscribe(nameof(OnTeamLeave));
                Unsubscribe(nameof(OnTeamDisband));
                Unsubscribe(nameof(OnTeamInvite));
                Unsubscribe(nameof(OnTeamCreate));
            }

            Lobby = new LobbyHandler(Configuration.Lobby.LobbySpawnfile);

            scrapItemId = ItemManager.FindItemDefinition("scrap").itemid;
            dropOnDeath = ParseType<DropOnDeath>(Configuration.Event.DropOnDeath);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                OnPlayerConnected(player);

            Debug.Log($"[Arena] - Loading event games in 5 seconds...");
            timer.In(5, InitializeEvents);
        }

        private void Unload()
        {
            IsUnloading = true;

            SaveRestoreData();

            foreach (BaseEventGame baseEventGame in ActiveEvents.Values)
                UnityEngine.Object.Destroy(baseEventGame);

            BaseEventPlayer[] eventPlayers = UnityEngine.Object.FindObjectsOfType<BaseEventPlayer>();
            for (int i = 0; i < eventPlayers?.Length; i++)
                UnityEngine.Object.DestroyImmediate(eventPlayers[i]);

            DestroyTeleporters();

            hexFilter = null;
            Configuration = null;
            Instance = null;
        }

        private void OnServerSave() => SaveRestoreData();
        #endregion

        #region Player Connect/Disconnect
        private void OnPlayerConnected(BasePlayer player)
        {
            if (Configuration.Server.RestorePlayers)
                TryRestorePlayer(player);

            if (Lobby.ForceLobbyRespawn && player.IsDead())
            {
                NextTick(() => Lobby.SendRespawnOptions(player));
                return;
            }

            if (Lobby.ForceLobbyRespawn && Configuration.Lobby.KeepPlayersInLobby && !player.IsAdmin && !permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION))
            {
                if (ZoneManager && !ZoneManager.Call<bool>("IsPlayerInZone", Configuration.Lobby.LobbyZoneID, player))
                    Lobby.TeleportToLobby(player);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                eventPlayer.Event.LeaveEvent(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (Configuration.Server.RestorePlayers)
                TryRestorePlayer(player);
        }

        private void TryRestorePlayer(BasePlayer player)
        {
            if (player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(1f, () => OnPlayerConnected(player));
                return;
            }

            UnlockClothingSlots(player);

            if (Restore.HasRestoreData(player.userID))
                Restore.RestorePlayer(player);
        }

        private object OnDefaultItemsReceive(PlayerInventory playerInventory) => Lobby.ForceLobbyRespawn && !Configuration.Server.RestorePlayers ? (object)true : null;
        #endregion

        #region Damage
        private void OnEntityTakeDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null)
                return;

            BasePlayer player = entity.ToPlayer();

            if (player != null)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer != null)
                {
                    eventPlayer.Event.OnPlayerTakeDamage(eventPlayer, hitInfo);
                }
            }
            else
            {
                BaseEventPlayer attacker = GetUser(hitInfo.InitiatorPlayer);
                if (attacker != null)
                {
                    if (attacker.Event.CanDealEntityDamage(attacker, entity, hitInfo))
                        return;

                    ClearDamage(hitInfo);
                }
            }
        }

        // TruePVE bypass
        private object CanEntityTakeDamage(BaseCombatEntity baseCombatEntity, HitInfo hitInfo)
        {
            if (baseCombatEntity == null || !(baseCombatEntity is BasePlayer))
                return null;

            return GetUser((baseCombatEntity as BasePlayer)) != null ? (object)true : null;
        }

        private object CanBeWounded(BasePlayer player, HitInfo hitInfo)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                return false;
            return null;
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo hitInfo)
        {
            if (player != null)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer != null)
                {
                    if (!eventPlayer.IsDead)
                        eventPlayer.Event.PrePlayerDeath(eventPlayer, hitInfo);
                    return false;
                }
            }
            return null;
        }

        private void OnEntityDeath(BasePlayer player, HitInfo hitInfo)
        {
            if (Lobby.ForceLobbyRespawn)
                NextTick(() => Lobby.SendRespawnOptions(player));
        }
        #endregion

        #region Spectate
        private object CanSpectateTarget(BasePlayer player, string name)
        {
            BaseEventPlayer eventPlayer = player.GetComponent<BaseEventPlayer>();
            if (eventPlayer != null && eventPlayer.Player.IsSpectating())
            {
                eventPlayer.UpdateSpectateTarget();
                return false;
            }
            return null;
        }
        #endregion

        #region Spawned Entities
        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (player == null)
                return;

            BaseCombatEntity baseCombatEntity = gameObject?.ToBaseEntity() as BaseCombatEntity;
            if (baseCombatEntity == null)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                eventPlayer.Event.OnEntityDeployed(baseCombatEntity);
        }

        private void OnItemDeployed(Deployer deployer, BaseCombatEntity baseCombatEntity)
        {
            BasePlayer player = deployer.GetOwnerPlayer();
            if (player == null)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                eventPlayer.Event.OnEntityDeployed(baseCombatEntity);
        }

        private object OnCreateWorldProjectile(HitInfo hitInfo, Item item)
        {
            if (hitInfo == null)
                return null;

            if (hitInfo.InitiatorPlayer != null)
            {
                BaseEventPlayer eventPlayer = GetUser(hitInfo.InitiatorPlayer);
                if (eventPlayer != null)
                    return false;
            }

            if (hitInfo.HitEntity?.ToPlayer() != null)
            {
                BaseEventPlayer eventPlayer = GetUser(hitInfo.HitEntity.ToPlayer());
                if (eventPlayer != null)
                    return false;
            }

            return null;
        }

        private void OnItemDropped(Item item, WorldItem worldItem)
        {
            BasePlayer player = item.GetOwnerPlayer();
            if (player != null)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer != null)
                {
                    eventPlayer.Event.OnWorldItemDropped(worldItem);
                }
            }
        }
        #endregion

        #region Items
        private object CanDropActiveItem(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null && !eventPlayer.Event.CanDropActiveItem())
                return false;
            return null;
        }

        private string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                return "You can not open your backpack during an event";
            return null;
        }
        #endregion

        #region Command Blacklist
        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (player == null || player.IsAdmin || eventPlayer == null)
                return null;

            if (Configuration.Event.CommandBlacklist.Any(x => x.StartsWith("/") ? x.Substring(1).Equals(command, StringComparison.OrdinalIgnoreCase) : x.Equals(command, StringComparison.OrdinalIgnoreCase)))
            {
                SendReply(player, Message("Error.CommandBlacklisted", player.userID));
                return false;
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !player.IsConnected)
                return null;

            if (Lobby.ForceLobbyRespawn && (player.IsDead() || player.IsSpectating()))
            {
                if (arg.cmd.Name == RESPAWN_CMD)
                {
                    Lobby.RespawnAtLobby(player);
                    return false;
                }

                if (arg.cmd.Name == REMOVE_BAG_CMD)
                {
                    uint num = arg.GetUInt(0, 0);
                    if (num == LOBBY_BAG_ID)
                        return false;
                }

                if (arg.cmd.Name == RESPAWN_BAG_CMD)
                {
                    uint num = arg.GetUInt(0, 0);
                    if (num == LOBBY_BAG_ID)
                    {
                        Lobby.RespawnAtLobby(player);
                        return false;
                    }
                }
            }

            BaseEventPlayer eventPlayer = GetUser(player);

            if (player.IsAdmin || eventPlayer == null || arg.Args == null)
                return null;

            if (Configuration.Event.CommandBlacklist.Any(x => arg.cmd.FullName.Equals(x, StringComparison.OrdinalIgnoreCase)))
            {
                SendReply(player, Message("Error.CommandBlacklisted", player.userID));
                return false;
            }
            return null;
        }
        #endregion

        #region Chat Handler
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || player.IsAdmin || permission.UserHasPermission(player.UserIDString, "arena.admin"))
                return null;

            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
            {
                eventPlayer.Event.BroadcastToPlayers(player, message);
            }
            else
            {
                foreach (BasePlayer otherPlayer in BasePlayer.activePlayerList)
                {
                    if (GetUser(otherPlayer) == null)
                        otherPlayer.SendConsoleCommand("chat.add", new object[] { 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}" });
                }
            }

            return false;
        }
        #endregion

        #region Teams
        private object OnTeamLeave(RelationshipManager.PlayerTeam playerTeam, BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer == null)
                return null;

            if (eventPlayer.Event.Plugin.IsTeamEvent)
                return true;

            return null;
        }

        private object OnTeamDisband(RelationshipManager.PlayerTeam playerTeam)
        {
            foreach (BaseEventGame baseEventGame in ActiveEvents.Values)
            {
                if (baseEventGame.IsEventTeam(playerTeam.teamID))
                    return true;
            }
            return null;
        }

        private object OnTeamInvite(BasePlayer player, BasePlayer other)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                return true;
            return null;
        }

        private object OnTeamCreate(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);
            if (eventPlayer != null)
                return true;
            return null;
        }
        #endregion

        #region Event Construction
        public static void RegisterEvent(string eventName, IEventPlugin plugin) => Instance.EventModes[eventName] = plugin;

        public static void UnregisterEvent(string eventName)
        {
            Instance.EventModes.Remove(eventName);

            for (int i = Instance.ActiveEvents.Count - 1; i >= 0; i--)
            {
                KeyValuePair<string, BaseEventGame> kvp = Instance.ActiveEvents.ElementAt(i);

                if (kvp.Value.Config.EventType.Equals(eventName))
                {
                    kvp.Value.EndEvent();
                    UnityEngine.Object.Destroy(kvp.Value);
                    Instance.ActiveEvents.Remove(kvp.Key);
                }
            }
        }

        private void InitializeEvents()
        {
            foreach (string eventName in Events.events.Keys)
            {
                object success = OpenEvent(eventName);
                if (success is string)
                    Debug.LogWarning($"[Arena] - {(string)success}");
            }
        }

        public object OpenEvent(string eventName)
        {
            EventConfig eventConfig;

            if (Events.events.TryGetValue(eventName, out eventConfig))
            {
                if (!string.IsNullOrEmpty(eventConfig.Permission) && !permission.PermissionExists(eventConfig.Permission))
                    permission.RegisterPermission(eventConfig.Permission, this);

                if (eventConfig.IsDisabled)
                    return $"The event {eventName} is disabled";

                IEventPlugin iEventPlugin;
                if (!EventModes.TryGetValue(eventConfig.EventType, out iEventPlugin) || iEventPlugin == null)
                    return $"Unable to find event plugin for game mode: {eventConfig.EventType}";

                object success = ValidateEventConfig(eventConfig);
                if (success is string)
                    return $"Failed to open event {eventName} : {(string)success}";

                if (!iEventPlugin.InitializeEvent(eventConfig))
                    return $"The event {eventName} is already active";
                return null;
            }
            else return "Failed to find a event with the specified name";
        }

        public static bool InitializeEvent<T>(IEventPlugin plugin, EventConfig config) where T : BaseEventGame
        {
            if (Instance.ActiveEvents.ContainsKey(config.EventName))
                return false;

            BaseEventGame eventGame = new GameObject(config.EventName).AddComponent<T>();
            eventGame.InitializeEvent(plugin, config);

            Instance.ActiveEvents[config.EventName] = eventGame;
            return true;
        }

        public static void ShutdownEvent(string eventName)
        {
            BaseEventGame baseEventGame;
            if (Instance.ActiveEvents.TryGetValue(eventName, out baseEventGame))
            {
                Instance.ActiveEvents.Remove(eventName);
                UnityEngine.Object.Destroy(baseEventGame);
            }
        }
        #endregion

        #region Functions
        public BaseEventGame FindEvent(string name)
        {
            BaseEventGame baseEventGame;
            ActiveEvents.TryGetValue(name, out baseEventGame);
            return baseEventGame;
        }

        public IEventPlugin GetPlugin(string name)
        {
            IEventPlugin eventPlugin;
            if (EventModes.TryGetValue(name, out eventPlugin))
                return eventPlugin;

            return null;
        }

        private bool CheckDependencies()
        {
            if (!Spawns)
            {
                PrintError("Unable to load EventManager - Spawns database not found. Please download Spawns database to continue");
                rust.RunServerCommand("oxide.unload", "EventManager");
                return false;
            }

            if (!ZoneManager)
                PrintError("ZoneManager is not installed! Unable to restrict event players to zones");

            if (!Kits)
                PrintError("Kits is not installed! Unable to issue any weapon kits");

            return true;
        }

        internal void TeleportPlayer(BasePlayer player, string spawnFile, bool sleep = false)
        {
            ResetMetabolism(player);

            object spawnPoint = Spawns?.Call("GetRandomSpawn", spawnFile);
            if (spawnPoint is Vector3)
                TeleportPlayer(player, (Vector3)spawnPoint, sleep);
        }

        private static void Broadcast(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                player?.SendConsoleCommand("chat.add", 0, Configuration.Message.ChatIcon, string.Format(Message(key, player.userID), args));
        }

        internal static void GetEventsOfType(string eventType, List<BaseEventGame> list)
        {
            foreach (BaseEventGame eventGame in Instance.ActiveEvents.Values)
            {
                if (eventGame.Config.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                    list.Add(eventGame);
            }
        }

        internal static bool HasActiveEventsOfType(string eventType)
        {
            foreach (BaseEventGame eventGame in Instance.ActiveEvents.Values)
            {
                if (eventGame.Config.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static void GetRegisteredEvents(List<IEventPlugin> list)
        {
            list.AddRange(Instance.EventModes.Values);
            list.Sort((IEventPlugin a, IEventPlugin b) =>
            {
                return a.EventName.CompareTo(b.EventName);
            });
        }
        internal static bool IsValidHex(string s) => hexFilter.IsMatch(s);
        #endregion

        #region Classes and Components
        public abstract class BaseEventGame : MonoBehaviour
        {
            internal IEventPlugin Plugin { get; private set; }

            internal EventConfig Config { get; private set; }

            public EventStatus Status { get; protected set; }

            protected GameTimer Timer { get; set; }

            internal RewardType RewardType { get; private set; }



            protected CuiElementContainer scoreContainer = null;


            internal List<BaseEventPlayer> eventPlayers = Pool.GetList<BaseEventPlayer>();

            internal List<BaseEventPlayer> joiningSpectators = Pool.GetList<BaseEventPlayer>();


            internal List<ScoreEntry> scoreData = Pool.GetList<ScoreEntry>();

            private List<BaseCombatEntity> _deployedObjects = Pool.GetList<BaseCombatEntity>();

            private List<DroppedItemContainer> _droppedInventories = Pool.GetList<DroppedItemContainer>();

            private List<WorldItem> _droppedItems = Pool.GetList<WorldItem>();

            private List<PlayerCorpse> _droppedCorpses = Pool.GetList<PlayerCorpse>();


            private Hash<string, List<string>> _kitBeltItems = new Hash<string, List<string>>();

            protected readonly HashSet<BaseEventPlayer> spectateTargets = new HashSet<BaseEventPlayer>();

            internal HashSet<BaseEventPlayer> SpectateTargets { get { return spectateTargets; } }


            private EventTeleporter _eventTeleporter;

            private bool _isClosed = false;

            protected int _roundNumber = 0;

            internal int RoundNumber => _roundNumber;

            internal EventTeam TeamA { get; private set; }

            internal EventTeam TeamB { get; private set; }


            public bool GodmodeEnabled { get; protected set; } = true;

            public EventResults LastEventResult { get; private set; } = new EventResults();

            internal string EventInformation
            {
                get
                {
                    string str = string.Format(Message("Info.Event.Current"), Config.EventName, Config.EventType);
                    str += string.Format(Message("Info.Event.Player"), eventPlayers.Count, Config.MaximumPlayers);
                    return str;
                }
            }

            internal string EventStatus => string.Format(Message("Info.Event.Status"), Status);

            #region Initialization and Destruction
            protected virtual void OnDestroy()
            {
                CleanupEntities();

                StopAllSpectating();

                for (int i = eventPlayers.Count - 1; i >= 0; i--)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    LeaveEvent(eventPlayer);
                }

                for (int i = joiningSpectators.Count - 1; i >= 0; i--)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    LeaveEvent(eventPlayer);
                }

                Pool.FreeList(ref scoreData);
                Pool.FreeList(ref _deployedObjects);
                Pool.FreeList(ref _droppedItems);
                Pool.FreeList(ref _droppedInventories);
                Pool.FreeList(ref _droppedCorpses);
                Pool.FreeList(ref eventPlayers);
                Pool.FreeList(ref joiningSpectators);

                spectateTargets.Clear();

                TeamA?.Destroy();
                TeamB?.Destroy();

                Timer?.StopTimer();

                DestroyTeleporter();

                Destroy(gameObject);
            }

            internal virtual void InitializeEvent(IEventPlugin plugin, EventConfig config)
            {
                this.Config = config;
                this.Plugin = this.Config.Plugin = plugin;

                TeamA = new EventTeam(Team.A, config.TeamConfigA.Color, config.TeamConfigA.Clothing, new SpawnSelector(config.TeamConfigA.Spawnfile));

                if (plugin.IsTeamEvent)
                    TeamB = new EventTeam(Team.B, config.TeamConfigB.Color, config.TeamConfigB.Clothing, new SpawnSelector(config.TeamConfigB.Spawnfile));

                Timer = new GameTimer(this);

                GodmodeEnabled = true;

                RewardType = ParseType<RewardType>(Config.Rewards.Type);

                Status = Arena.EventStatus.Open;

                CreateTeleporter();
            }

            internal List<string> GetItemsForKit(string kit)
            {
                List<string> list;
                if (!_kitBeltItems.TryGetValue(kit, out list))
                {
                    JObject obj = Instance.Kits?.Call<JObject>("GetKitObject", kit);
                    if (obj == null)
                    {
                        Debug.LogError($"[Arena] - Kits failed to return data for kit : {kit}. Is this a valid kit name?");
                        return null;
                    }

                    list = _kitBeltItems[kit] = new List<string>();

                    JArray jArray = obj["BeltItems"] as JArray;

                    foreach (JObject itemObj in jArray)
                        list.Add((string)itemObj["Shortname"]);

                }
                return list;
            }
            #endregion

            #region Event Management

            internal virtual void CloseEvent()
            {
                _isClosed = true;
                BroadcastToPlayers("Notification.EventClosed");
            }

            protected virtual void StartEvent()
            {
                if (joiningSpectators.Count > 0)
                {
                    joiningSpectators.ForEach((BaseEventPlayer eventPlayer) => eventPlayers.Add(eventPlayer));
                    joiningSpectators.Clear();
                }

                if (!HasMinimumRequiredPlayers())
                {
                    BroadcastToPlayers("Notification.NotEnoughToStart");
                    EndEvent();
                    return;
                }

                //if (Config.UseEventBots && Config.MaximumBots > 0 && eventPlayers.Count < Config.MaximumPlayers)
                //    SpawnEventBots();

                _roundNumber = 0;

                LastEventResult.UpdateFromEvent(this);

                Timer.StopTimer();

                UpdateScoreboard();

                Status = Arena.EventStatus.Started;

                StartNextRound();

                SetZoneOccupied(this, true);
            }

            protected virtual void StartNextRound()
            {
                if (!HasMinimumRequiredPlayers())
                {
                    BroadcastToPlayers("Notification.NotEnoughToContinue");
                    EndEvent();
                    return;
                }

                CleanupEntities();

                _roundNumber += 1;

                if (Config.TimeLimit > 0)
                    Timer.StartTimer(Config.TimeLimit, string.Empty, EndRound);

                GodmodeEnabled = false;

                if (CanEnterBetweenRounds())
                {
                    joiningSpectators.ForEach((BaseEventPlayer eventPlayer) => eventPlayers.Add(eventPlayer));
                    joiningSpectators.Clear();
                }

                //if (Config.UseEventBots && Config.MaximumBots > 0)
                //{
                //    if (eventPlayers.Count > Config.MaximumPlayers)
                //        RemoveExcessBots();
                //    else if (eventPlayers.Count < Config.MaximumPlayers)
                //        SpawnEventBots();
                //}

                eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer?.Player == null)
                        return;

                    if (CanRespawnPlayer(eventPlayer))
                    {
                        if (eventPlayer.IsDead)
                            RespawnPlayer(eventPlayer);
                        else
                        {
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        }
                    }
                });

                RebuildSpectateTargets();
                UpdateSpectatorTargets();

                UpdateScoreboard();
            }

            protected virtual bool CanRespawnPlayer(BaseEventPlayer baseEventPlayer) => true;

            protected virtual void EndRound()
            {
                UpdateScoreboard();

                if (_roundNumber >= Config.RoundsToPlay)
                {
                    BroadcastToPlayers("Notification.EventFinished");
                    InvokeHandler.Invoke(this, EndEvent, 1f);
                }
                else
                {
                    GodmodeEnabled = true;

                    Timer.StopTimer();

                    LastEventResult.UpdateFromEvent(this);

                    ProcessWinners();

                    eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                    {
                        if (eventPlayer == null || eventPlayer.Player == null)
                            return;

                        eventPlayer.ResetStatistics();

                        if (!CanRespawnPlayer(eventPlayer))
                            return;

                        if (eventPlayer.IsDead)
                        {
                            eventPlayer.OnRoundFinished();
                            RespawnPlayer(eventPlayer);
                        }

                        ArenaStatistics.Data.OnGamePlayed(eventPlayer.Player, Config.EventType);
                    });

                    ArenaStatistics.Data.OnGamePlayed(Config.EventType);

                    if (CanEnterBetweenRounds())
                    {
                        joiningSpectators.ForEach((BaseEventPlayer eventPlayer) =>
                        {
                            eventPlayers.Add(eventPlayer);
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        });

                        joiningSpectators.Clear();
                    }

                    RebuildSpectateTargets();
                    UpdateSpectatorTargets();

                    BroadcastToPlayers("Notification.NextRoundStartsIn", _roundNumber, Configuration.Timer.RoundInterval);
                    Timer.StartTimer(Configuration.Timer.RoundInterval, Message("Timer.NextRoundStartsIn"), StartNextRound);
                }
            }

            internal virtual void EndEvent()
            {
                Timer.StopTimer();

                Status = Arena.EventStatus.Finished;

                GodmodeEnabled = true;

                LastEventResult.UpdateFromEvent(this);

                CleanupEntities();

                SetZoneOccupied(this, true);

                if (!IsUnloading)
                    ProcessWinners();

                StopAllSpectating();

                eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer == null || eventPlayer.Player == null)
                        return;

                    eventPlayer.ResetStatistics();

                    if (eventPlayer is NPCEventPlayer)
                    {
                        eventPlayer.Player.Kill(BaseNetworkable.DestroyMode.None);
                        return;
                    }

                    if (eventPlayer.IsDead)
                        RespawnPlayer(eventPlayer);

                    if (!IsUnloading)
                        ArenaStatistics.Data.OnGamePlayed(eventPlayer.Player, Config.EventType);
                });

                if (!IsUnloading)
                {
                    ArenaStatistics.Data.OnGamePlayed(Config.EventType);

                    if (Configuration.Event.StartOnFinish)
                    {
                        BroadcastToPlayers("Notification.NextEventStartsIn", Configuration.Timer.RoundInterval);
                        Timer.StartTimer(Configuration.Timer.RoundInterval, Message("UI.NextGameStartsIn"), StartEvent);
                    }
                    else
                    {
                        EjectAllPlayers();

                        if (joiningSpectators.Count > 0)
                            InsertJoiningSpectators();
                    }

                    RebuildSpectateTargets();
                }
            }
            #endregion

            #region Player Management
            internal bool CanJoinEvent(BasePlayer player)
            {
                if ((GetActualPlayerCount() + joiningSpectators.Count) >= Config.MaximumPlayers)
                {
                    player.ChatMessage(Message("Notification.MaximumPlayers", player.userID));
                    return false;
                }

                string str = CanJoinEvent();
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return false;
                }

                return true;
            }

            protected virtual string CanJoinEvent()
            {
                return string.Empty;
            }

            protected virtual bool CanEnterBetweenRounds() => true;

            protected virtual bool CanEnterDuringRound() => !_isClosed;

            internal virtual void JoinEvent(BasePlayer player, Team team = Team.None)
            {
                if (Status == Arena.EventStatus.Started && !CanEnterDuringRound())
                    CreateSpectatorPlayer(player, team);
                else CreateEventPlayer(player, team);

                if (Configuration.Message.BroadcastJoiners)
                    BroadcastToPlayers("Notification.PlayerJoined", player.displayName, Config.EventName);

                if ((Status == Arena.EventStatus.Open || Status == Arena.EventStatus.Finished))
                {
                    if (HasMinimumRequiredPlayers())
                    {
                        Status = Arena.EventStatus.Prestarting;
                        Timer.StartTimer(Configuration.Timer.Prestart, Message("Notification.RoundStartsIn"), StartEvent);
                    }
                    else BroadcastToPlayers("Notification.WaitingForPlayers", Config.MinimumPlayers - eventPlayers.Count);
                }
            }

            internal virtual void LeaveEvent(BasePlayer player)
            {
                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer == null)
                    return;

                if (eventPlayer is NPCEventPlayer)
                {
                    LeaveEvent(eventPlayer as NPCEventPlayer);
                }
                else
                {
                    LeaveEvent(eventPlayer);

                    if (Configuration.Message.BroadcastLeavers)
                        BroadcastToPlayers("Notification.PlayerLeft", player.displayName, Config.EventName);
                }
            }

            internal void LeaveEvent(NPCEventPlayer eventPlayer)
            {
                eventPlayers.Remove(eventPlayer);

                StripInventory(eventPlayer.Player);

                DestroyImmediate(eventPlayer);
            }

            internal virtual void LeaveEvent(BaseEventPlayer eventPlayer)
            {
                BasePlayer player = eventPlayer.Player;

                if (eventPlayer.IsDead || player.IsSpectating())
                {
                    ResetPlayer(eventPlayer.Player);
                    TeleportPlayer(player, eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), false);
                }

                StripInventory(player);

                ResetMetabolism(player);

                if (!string.IsNullOrEmpty(Config.ZoneID))
                    Instance?.ZoneManager?.Call("RemovePlayerFromZoneWhitelist", Config.ZoneID, player);

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = eventPlayer.Team == Team.A ? TeamA : TeamB;
                    eventTeam.RemoveFromTeam(player);
                }

                eventPlayers.Remove(eventPlayer);
                joiningSpectators.Remove(eventPlayer);

                RebuildSpectateTargets();

                UpdateSpectatorTargets(eventPlayer);

                DestroyImmediate(eventPlayer);

                if (!player.IsConnected || player.IsSleeping() || IsUnloading)
                    player.Die();
                else
                {
                    if (Configuration.Server.RestorePlayers)
                        Instance?.Restore.RestorePlayer(player);
                }

                if (Status != Arena.EventStatus.Finished && (!HasMinimumRequiredPlayers() || GetActualPlayerCount() == 0))
                {
                    BroadcastToPlayers("Notification.NotEnoughToContinue");
                    EndEvent();
                }
            }

            internal void InsertJoiningSpectators()
            {
                joiningSpectators.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    eventPlayers.Add(eventPlayer);

                    if (Configuration.Message.BroadcastJoiners)
                        BroadcastToPlayers("Notification.PlayerJoined", eventPlayer.Player.displayName, Config.EventName);

                    if (eventPlayer.Player.IsSpectating())
                    {
                        ResetPlayer(eventPlayer.Player);
                        OnPlayerRespawn(eventPlayer);
                    }
                });

                joiningSpectators.Clear();

                if (HasMinimumRequiredPlayers())
                {
                    Status = Arena.EventStatus.Prestarting;
                    Timer.StartTimer(Configuration.Timer.Prestart, Message("Notification.RoundStartsIn"), StartEvent);
                }
            }

            protected virtual void CreateEventPlayer(BasePlayer player, Team team = Team.None)
            {
                if (player == null)
                    return;

                BaseEventPlayer eventPlayer = AddPlayerComponent(player);

                eventPlayer.ResetPlayer();

                eventPlayer.Event = this;

                eventPlayer.Team = team;

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = team == Team.A ? TeamA : TeamB;

                    eventTeam.AddToTeam(player);
                }

                eventPlayers.Add(eventPlayer);

                if (!Config.AllowClassSelection || GetAvailableKits(eventPlayer.Team).Count == 1)
                    eventPlayer.Kit = GetAvailableKits(eventPlayer.Team).First();

                SpawnPlayer(eventPlayer, Status == Arena.EventStatus.Started, true);

                if (!string.IsNullOrEmpty(Config.ZoneID))
                    Instance.ZoneManager?.Call("AddPlayerToZoneWhitelist", Config.ZoneID, player);
            }

            protected virtual Team GetSpectatingTeam(Team currentTeam) => currentTeam == Team.A ? Team.A : Team.B;

            protected virtual void CreateSpectatorPlayer(BasePlayer player, Team team = Team.None)
            {
                if (player == null)
                    return;

                BaseEventPlayer eventPlayer = AddPlayerComponent(player);

                eventPlayer.ResetPlayer();

                eventPlayer.Event = this;

                eventPlayer.Team = team;

                if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams && Configuration.Event.AddToTeams)
                {
                    EventTeam eventTeam = GetSpectatingTeam(team) == Team.A ? TeamA : TeamB;

                    eventTeam.AddToTeam(player);
                }

                joiningSpectators.Add(eventPlayer);

                if (!Config.AllowClassSelection || GetAvailableKits(eventPlayer.Team).Count == 1)
                    eventPlayer.Kit = GetAvailableKits(team).First();

                eventPlayer.Player.GetMounted()?.AttemptDismount(eventPlayer.Player);

                if (eventPlayer.Player.HasParent())
                    eventPlayer.Player.SetParent(null, true, true);

                StripInventory(player);

                ResetMetabolism(player);

                TeleportPlayer(player, eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), false);

                if (!string.IsNullOrEmpty(Config.ZoneID))
                    Instance.ZoneManager?.Call("AddPlayerToZoneWhitelist", Config.ZoneID, player);

                eventPlayer.BeginSpectating();

                UpdateScoreboard(eventPlayer);

                BroadcastToPlayer(eventPlayer, Message("Notification.JoinerSpectate", player.userID));

                ArenaUI.ShowHelpText(eventPlayer);
            }

            protected virtual Team GetPlayerTeam() => Team.None;

            protected virtual BaseEventPlayer AddPlayerComponent(BasePlayer player) => player.gameObject.GetComponent<BaseEventPlayer>() ?? player.gameObject.AddComponent<BaseEventPlayer>();

            internal virtual void OnPlayerRespawn(BaseEventPlayer baseEventPlayer)
            {
                SpawnPlayer(baseEventPlayer, Status == Arena.EventStatus.Started);
            }

            internal void SpawnPlayer(BaseEventPlayer eventPlayer, bool giveKit = true, bool sleep = false)
            {
                if (eventPlayer == null || eventPlayer.Player == null)
                    return;

                if (eventPlayer.Player.IsSpectating())
                    eventPlayer.FinishSpectating();

                eventPlayer.Player.GetMounted()?.AttemptDismount(eventPlayer.Player);

                if (eventPlayer.Player.HasParent())
                    eventPlayer.Player.SetParent(null, true);

                StripInventory(eventPlayer.Player);

                ResetMetabolism(eventPlayer.Player);

                TeleportPlayer(eventPlayer.Player, eventPlayer.Team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), sleep);

                if (string.IsNullOrEmpty(eventPlayer.Kit) && !(eventPlayer is NPCEventPlayer))
                {
                    eventPlayer.ForceSelectClass();
                    ArenaUI.DisplayDeathScreen(eventPlayer, Message("UI.SelectClass", eventPlayer.Player.userID), true);
                    return;
                }

                UpdateScoreboard(eventPlayer);

                if (giveKit)
                {
                    Instance.NextTick(() =>
                    {
                        if (CanGiveKit(eventPlayer))
                        {
                            GiveKit(eventPlayer.Player, eventPlayer.Kit);
                            OnKitGiven(eventPlayer);
                        }
                    });
                }

                eventPlayer.ApplyInvincibility();

                OnPlayerSpawned(eventPlayer);

                RebuildSpectateTargets();

                UpdateSpectatorTargets();

                ArenaUI.ShowHelpText(eventPlayer);
            }

            protected virtual void OnPlayerSpawned(BaseEventPlayer eventPlayer) { }

            protected void EjectAllPlayers()
            {
                for (int i = eventPlayers.Count - 1; i >= 0; i--)
                    LeaveEvent(eventPlayers[i].Player);
                eventPlayers.Clear();

                for (int i = joiningSpectators.Count - 1; i >= 0; i--)
                    LeaveEvent(joiningSpectators[i].Player);
                joiningSpectators.Clear();
            }

            private bool HasMinimumRequiredPlayers()
            {
                if (GetActualPlayerCount() == 0)
                    return false;

                if (eventPlayers.Count >= Config.MinimumPlayers)
                    return true;

                if (Config.UseEventBots && eventPlayers.Count + Config.MaximumBots >= Config.MinimumPlayers)
                    return true;

                return false;
            }

            #endregion

            #region Damage and Death
            internal virtual bool CanDealEntityDamage(BaseEventPlayer attacker, BaseEntity entity, HitInfo hitInfo)
            {
                if (entity is BaseCombatEntity && _deployedObjects.Contains(entity as BaseCombatEntity))
                    return true;

                return false;
            }

            protected virtual float GetDamageModifier(BaseEventPlayer eventPlayer, BaseEventPlayer attackerPlayer) => 1f;

            internal virtual void OnPlayerTakeDamage(BaseEventPlayer eventPlayer, HitInfo hitInfo)
            {
                BaseEventPlayer attacker = GetUser(hitInfo.InitiatorPlayer);

                if (attacker == null && CanKillEntity(hitInfo.Initiator as BaseCombatEntity))
                {
                    (hitInfo.Initiator as BaseCombatEntity).Die(new HitInfo(eventPlayer.Player, hitInfo.Initiator, Rust.DamageType.Suicide, 1000f));
                    ClearDamage(hitInfo);
                    return;
                }

                if (GodmodeEnabled || eventPlayer.IsDead || eventPlayer.IsInvincible || eventPlayer.Player.IsSpectating())
                {
                    ClearDamage(hitInfo);
                    return;
                }

                float damageModifier = GetDamageModifier(eventPlayer, attacker);
                if (damageModifier != 1f)
                    hitInfo.damageTypes.ScaleAll(damageModifier);

                eventPlayer.OnTakeDamage(attacker?.Player.userID ?? 0U);
            }

            protected virtual bool CanKillEntity(BaseCombatEntity baseCombatEntity)
            {
                if (baseCombatEntity == null)
                    return false;

                return baseCombatEntity is BaseNpc || baseCombatEntity is NPCPlayer;
            }

            internal virtual void PrePlayerDeath(BaseEventPlayer eventPlayer, HitInfo hitInfo)
            {
                if (dropOnDeath != DropOnDeath.Nothing)
                {
                    if (dropOnDeath == DropOnDeath.Corpse && CanDropCorpse())
                        eventPlayer.DropCorpse();
                    else if (dropOnDeath == DropOnDeath.Backpack && CanDropBackpack())
                        eventPlayer.DropInventory();
                    else if (dropOnDeath == DropOnDeath.Weapon && CanDropWeapon())
                        eventPlayer.DropWeapon();
                    else if (dropOnDeath == DropOnDeath.Ammo && CanDropAmmo())
                        eventPlayer.DropAmmo();
                }

                if (eventPlayer.Player.isMounted)
                {
                    BaseMountable baseMountable = eventPlayer.Player.GetMounted();
                    if (baseMountable != null)
                    {
                        baseMountable.DismountPlayer(eventPlayer.Player);
                        eventPlayer.Player.EnsureDismounted();
                    }
                }

                eventPlayer.IsDead = true;

                RebuildSpectateTargets();

                UpdateSpectatorTargets(eventPlayer);

                eventPlayer.Player.limitNetworking = true;

                eventPlayer.Player.DisablePlayerCollider();

                eventPlayer.Player.RemoveFromTriggers();

                eventPlayer.RemoveFromNetwork();

                OnEventPlayerDeath(eventPlayer, GetUser(hitInfo?.InitiatorPlayer), hitInfo);

                ClearDamage(hitInfo);
            }

            internal virtual void OnEventPlayerDeath(BaseEventPlayer victim, BaseEventPlayer attacker = null, HitInfo hitInfo = null)
            {
                if (victim == null || victim.Player == null)
                    return;

                StripInventory(victim.Player);

                if (Configuration.Message.BroadcastKills)
                    DisplayKillToChat(victim, attacker?.Player != null ? attacker.Player.displayName : string.Empty);
            }

            protected virtual void DisplayKillToChat(BaseEventPlayer victim, string attackerName)
            {
                if (string.IsNullOrEmpty(attackerName))
                {
                    if (victim.IsOutOfBounds)
                        BroadcastToPlayers("Notification.Death.OOB", victim.Player.displayName);
                    else BroadcastToPlayers("Notification.Death.Suicide", victim.Player.displayName);
                }
                else BroadcastToPlayers("Notification.Death.Killed", victim.Player.displayName, attackerName);
            }
            #endregion

            #region Winners
            protected void ProcessWinners()
            {
                List<BaseEventPlayer> winners = Pool.GetList<BaseEventPlayer>();
                GetWinningPlayers(ref winners);

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer == null)
                        continue;

                    if (winners.Contains(eventPlayer) && CanIssueRewards(eventPlayer))
                    {
                        ArenaStatistics.Data.AddStatistic(eventPlayer.Player, "Wins");
                        Instance.GiveReward(eventPlayer, RewardType, Config.Rewards.WinAmount);
                    }
                    else ArenaStatistics.Data.AddStatistic(eventPlayer.Player, "Losses");

                    ArenaStatistics.Data.AddStatistic(eventPlayer.Player, "Played");
                }

                if (winners.Count > 0)
                {
                    if (Configuration.Message.BroadcastWinners)
                    {
                        if (Plugin.IsTeamEvent)
                        {
                            Team team = winners[0].Team;
                            Broadcast("Notification.EventWin.Multiple.Team", team == Team.B ? TeamB.Color : TeamA.Color, team == Team.B ? Plugin.TeamBName : Plugin.TeamAName);
                        }
                        else
                        {
                            if (winners.Count > 1)
                                Broadcast("Notification.EventWin.Multiple", winners.Select(x => x.Player.displayName).ToSentence());
                            else Broadcast("Notification.EventWin", winners[0].Player.displayName);
                        }
                    }

                    if (Plugin.IsTeamEvent)
                    {
                        Team team = winners[0].Team;
                        BroadcastRoundWinMessage("UI.EventWin.Multiple.Team", team == Team.B ? TeamB.Color : TeamA.Color, team == Team.B ? Plugin.TeamBName : Plugin.TeamAName);
                    }
                    else
                    {
                        if (winners.Count > 1)
                            BroadcastRoundWinMessage("UI.EventWin.Multiple");
                        else BroadcastRoundWinMessage("UI.EventWin", winners[0].Player.displayName);
                    }
                }

                Pool.FreeList(ref winners);
            }

            protected virtual bool CanIssueRewards(BaseEventPlayer eventPlayer) => true;

            protected abstract void GetWinningPlayers(ref List<BaseEventPlayer> list);
            #endregion

            #region Kits and Items
            protected virtual bool CanDropBackpack() => true;

            protected virtual bool CanDropCorpse() => true;

            protected virtual bool CanDropWeapon() => true;

            protected virtual bool CanDropAmmo() => true;

            internal virtual bool CanDropActiveItem() => false;

            protected virtual bool CanGiveKit(BaseEventPlayer eventPlayer) => true;

            protected virtual void OnKitGiven(BaseEventPlayer eventPlayer)
            {
                if (Plugin.IsTeamEvent)
                {
                    string kit = eventPlayer.Team == Team.B ? Config.TeamConfigB.Clothing : Config.TeamConfigA.Clothing;
                    if (!string.IsNullOrEmpty(kit))
                    {
                        List<Item> items = eventPlayer.Player.inventory.containerWear.itemList;
                        for (int i = 0; i < items.Count; i++)
                        {
                            Item item = items[i];
                            item.RemoveFromContainer();
                            item.Remove();
                        }

                        GiveKit(eventPlayer.Player, kit);
                    }
                }

                //if (eventPlayer is ArenaAI.HumanAI)
                //    (eventPlayer as ArenaAI.HumanAI).OnKitGiven();
            }

            internal List<string> GetAvailableKits(Team team) => team == Team.B ? Config.TeamConfigB.Kits : Config.TeamConfigA.Kits;
            #endregion

            #region Overrides
            internal virtual void GetAdditionalEventDetails(ref List<KeyValuePair<string, object>> list, ulong playerId) { }
            #endregion

            #region Spectating
            internal virtual void RebuildSpectateTargets()
            {
                spectateTargets.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    if (eventPlayer == null || eventPlayer.IsDead || eventPlayer.Player.IsSleeping() || eventPlayer.Player.IsSpectating())
                        continue;

                    spectateTargets.Add(eventPlayer);
                }
            }

            internal void UpdateSpectatorTargets(BaseEventPlayer target = null)
            {
                eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer != null && eventPlayer.Player.IsSpectating() && (eventPlayer.SpectateTarget == null || eventPlayer.SpectateTarget == target))
                    {
                        if (spectateTargets.Count > 0)
                            eventPlayer.UpdateSpectateTarget();
                        else
                        {
                            ResetPlayer(eventPlayer.Player);
                            OnPlayerRespawn(eventPlayer);
                        }
                    }
                });

                joiningSpectators.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer != null && eventPlayer.SpectateTarget == null || eventPlayer.SpectateTarget == target)
                    {
                        if (spectateTargets.Count > 0)
                            eventPlayer.UpdateSpectateTarget();
                        else eventPlayer.SetSpectateTarget(null);
                    }
                });
            }

            private void StopAllSpectating()
            {
                joiningSpectators.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer != null && eventPlayer.Player != null && eventPlayer.Player.IsSpectating())
                        eventPlayer.FinishSpectating();
                });

                eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                {
                    if (eventPlayer != null && eventPlayer.Player != null && eventPlayer.Player.IsSpectating())
                        eventPlayer.FinishSpectating();
                });
            }
            #endregion

            //#region Event Bots
            //internal virtual ArenaAI.Settings Settings { get; } = new ArenaAI.Settings()
            //{
            //    GiveDefaultItems = false
            //};

            //internal virtual ArenaAI.HumanAI CreateAIPlayer(Vector3 position, ArenaAI.Settings settings) => ArenaAI.SpawnNPC<ArenaAI.HumanAI>(position, settings);

            //protected virtual Team GetAIPlayerTeam() => Team.None;

            //private void SpawnEventBots()
            //{
            //    Debug.Log($"spawn event bots");
            //    int count = Mathf.Min(Config.MaximumBots, Config.MaximumPlayers - eventPlayers.Count);

            //    for (int i = 0; i < count; i++)
            //        SpawnBot();
            //}

            //protected void SpawnBot()
            //{
            //    Debug.Log($"spawn bot");
            //    Team team = GetAIPlayerTeam();

            //    ArenaAI.HumanAI humanAI = CreateAIPlayer(team == Team.B ? TeamB.Spawns.GetSpawnPoint() : TeamA.Spawns.GetSpawnPoint(), Settings);

            //    humanAI.ResetPlayer();

            //    humanAI.Event = this;
            //    humanAI.Team = team;

            //    if (Plugin.IsTeamEvent && Configuration.Event.AddToTeams)
            //    {
            //        EventTeam eventTeam = team == Team.A ? TeamA : TeamB;

            //        eventTeam.AddToTeam(humanAI);
            //    }

            //    eventPlayers.Add(humanAI);

            //    humanAI.Kit = GetAvailableKits(team).GetRandom();

            //    StripInventory(humanAI.Entity);

            //    GiveKit(humanAI.Entity, humanAI.Kit);

            //    OnKitGiven(humanAI);
            //}

            //private void RemoveExcessBots()
            //{
            //    int count = Mathf.Min(GetNPCPlayerCount(), eventPlayers.Count - Config.MaximumPlayers);

            //    for (int i = 0; i < count; i++)
            //        RemoveRandomBot();
            //}

            //protected virtual void RemoveRandomBot()
            //{
            //    if (Plugin.IsTeamEvent)
            //    {
            //        Team removeTeam = GetTeamCount(Team.A) > GetTeamCount(Team.B) ? Team.A : Team.B;

            //        for (int i = 0; i < eventPlayers.Count; i++)
            //        {
            //            BaseEventPlayer eventPlayer = eventPlayers[i];
            //            if (eventPlayer is NPCEventPlayer && eventPlayer.Team == removeTeam)
            //            {
            //                eventPlayers.Remove(eventPlayer);
            //                eventPlayer.Player.Kill(BaseNetworkable.DestroyMode.None);
            //                return;
            //            }
            //        }
            //    }
            //    else
            //    {
            //        for (int i = 0; i < eventPlayers.Count; i++)
            //        {
            //            BaseEventPlayer eventPlayer = eventPlayers[i];
            //            if (eventPlayer is NPCEventPlayer)
            //            {
            //                eventPlayers.Remove(eventPlayer);
            //                eventPlayer.Player.Kill(BaseNetworkable.DestroyMode.None);
            //                return;
            //            }
            //        }
            //    }
            //}

            //internal virtual void UpdateEnemyTargets(ArenaAI.HumanAI humanAI)
            //{
            //    for (int i = 0; i < eventPlayers.Count; i++)
            //    {
            //        BaseEventPlayer eventPlayer = eventPlayers[i];
            //        if (eventPlayer == null || eventPlayer == humanAI || (Plugin.IsTeamEvent && eventPlayer.Team == humanAI.Team))
            //            continue;

            //        humanAI.Memory.Update(eventPlayer);
            //    }
            //}

            //internal Vector3 GetRandomAIDestination(ArenaAI.HumanAI eventPlayer)
            //{
            //    BaseEventPlayer randomPlayer = eventPlayers.GetRandom();
            //    if (randomPlayer == eventPlayer)
            //        return GetRandomAIDestination(eventPlayer);

            //    return randomPlayer.Transform.position;
            //}
            //#endregion

            #region Player Counts
            internal int GetActualPlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (!(eventPlayers[i] is NPCEventPlayer))
                        count++;
                }
                return count;
            }

            internal int GetNPCPlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if ((eventPlayers[i] is NPCEventPlayer))
                        count++;
                }
                return count;
            }

            internal int GetAlivePlayerCount()
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (!eventPlayers[i]?.IsDead ?? false)
                        count++;
                }
                return count;
            }

            internal int GetTeamCount(Team team)
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    if (eventPlayers[i]?.Team == team)
                        count++;
                }
                return count;
            }

            internal int GetTeamAliveCount(Team team)
            {
                int count = 0;
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer != null && eventPlayer.Team == team && !eventPlayer.IsDead)
                        count++;
                }
                return count;
            }
            #endregion

            #region Teams
            internal virtual int GetTeamScore(Team team) => 0;

            protected void BalanceTeams()
            {
                int aCount = GetTeamCount(Team.A);
                int bCount = GetTeamCount(Team.B);

                int difference = aCount > bCount + 1 ? aCount - bCount : bCount > aCount + 1 ? bCount - aCount : 0;
                Team moveFrom = aCount > bCount + 1 ? Team.A : bCount > aCount + 1 ? Team.B : Team.None;

                if (difference > 1 && moveFrom != Team.None)
                {
                    BroadcastToPlayers("Notification.Teams.Unbalanced");

                    List<BaseEventPlayer> teamPlayers = Pool.GetList<BaseEventPlayer>();

                    eventPlayers.ForEach(x =>
                    {
                        if (x.Team == moveFrom)
                            teamPlayers.Add(x);
                    });

                    for (int i = 0; i < (int)Math.Floor((float)difference / 2); i++)
                    {
                        BaseEventPlayer eventPlayer = teamPlayers.GetRandom();
                        teamPlayers.Remove(eventPlayer);

                        eventPlayer.Team = moveFrom == Team.A ? Team.B : Team.A;

                        if (Plugin.IsTeamEvent && Plugin.CanUseRustTeams)
                        {
                            EventTeam currentTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamB : eventPlayer.Event.TeamA;
                            EventTeam newTeam = eventPlayer.Team == Team.A ? eventPlayer.Event.TeamA : eventPlayer.Event.TeamB;

                            currentTeam.RemoveFromTeam(eventPlayer.Player);
                            newTeam.AddToTeam(eventPlayer.Player);
                        }

                        BroadcastToPlayer(eventPlayer, string.Format(Message("Notification.Teams.TeamChanged", eventPlayer.Player.userID), eventPlayer.Team));
                    }

                    Pool.FreeList(ref teamPlayers);
                }
            }
            #endregion

            #region Entity Management
            internal void OnEntityDeployed(BaseCombatEntity entity) => _deployedObjects.Add(entity);

            internal void OnWorldItemDropped(WorldItem worldItem) => _droppedItems.Add(worldItem);

            internal void OnInventorySpawned(DroppedItemContainer entity) => _droppedInventories.Add(entity);

            internal void OnCorpseSpawned(PlayerCorpse entity) => _droppedCorpses.Add(entity);

            private void CleanupEntities()
            {
                for (int i = _deployedObjects.Count - 1; i >= 0; i--)
                {
                    BaseCombatEntity entity = _deployedObjects[i];
                    if (entity != null && !entity.IsDestroyed)
                        entity.DieInstantly();
                }
                _deployedObjects.Clear();

                for (int i = _droppedInventories.Count - 1; i >= 0; i--)
                {
                    DroppedItemContainer droppedItemContainer = _droppedInventories[i];
                    if (droppedItemContainer != null && !droppedItemContainer.IsDestroyed)
                    {
                        droppedItemContainer.inventory?.Clear();
                        droppedItemContainer.DieInstantly();
                    }
                }
                _droppedInventories.Clear();

                for (int i = _droppedItems.Count - 1; i >= 0; i--)
                {
                    WorldItem worldItem = _droppedItems[i];
                    if (worldItem != null && !worldItem.IsDestroyed)
                    {
                        worldItem.DestroyItem();
                        worldItem.Kill(BaseNetworkable.DestroyMode.None);
                    }
                }
                _droppedItems.Clear();

                for (int i = _droppedCorpses.Count - 1; i >= 0; i--)
                {
                    PlayerCorpse playerCorpse = _droppedCorpses[i];
                    if (playerCorpse != null && !playerCorpse.IsDestroyed)
                    {
                        for (int y = 0; y < playerCorpse.containers?.Length; y++)
                            playerCorpse.containers[y]?.Clear();

                        playerCorpse.DieInstantly();
                    }
                }
                _droppedCorpses.Clear();
            }
            #endregion

            #region Scoreboard
            internal void UpdateScoreboard()
            {
                UpdateScores();
                BuildScoreboard();

                if (scoreContainer != null)
                {
                    eventPlayers.ForEach((BaseEventPlayer eventPlayer) =>
                    {
                        if (!eventPlayer.IsDead)
                            eventPlayer.AddUI(ArenaUI.UI_SCORES, scoreContainer);
                    });

                    joiningSpectators.ForEach((BaseEventPlayer eventPlayer) => eventPlayer.AddUI(ArenaUI.UI_SCORES, scoreContainer));
                }
            }

            protected void UpdateScoreboard(BaseEventPlayer eventPlayer)
            {
                if (Status == Arena.EventStatus.Started && scoreContainer != null && !eventPlayer.IsDead)
                    eventPlayer.AddUI(ArenaUI.UI_SCORES, scoreContainer);
            }

            protected void UpdateScores()
            {
                scoreData.Clear();

                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    scoreData.Add(new ScoreEntry(eventPlayer, GetFirstScoreValue(eventPlayer), GetSecondScoreValue(eventPlayer)));
                }

                SortScores(ref scoreData);
            }

            protected abstract void BuildScoreboard();

            protected abstract float GetFirstScoreValue(BaseEventPlayer eventPlayer);

            protected abstract float GetSecondScoreValue(BaseEventPlayer eventPlayer);

            protected abstract void SortScores(ref List<ScoreEntry> list);
            #endregion

            #region Event Messaging
            internal void BroadcastToPlayers(string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }
            }

            internal void BroadcastToPlayers(Func<string, ulong, string> GetMessage, string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(GetMessage(key, eventPlayer.Player.userID), args) : GetMessage(key, eventPlayer.Player.userID));
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(GetMessage(key, eventPlayer.Player.userID), args) : GetMessage(key, eventPlayer.Player.userID));
                }
            }

            internal void BroadcastToPlayers(BasePlayer player, string message)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null)
                        eventPlayer.Player.SendConsoleCommand("chat.add", new object[] { 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}" });
                }

                for (int i = 0; i < joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = joiningSpectators[i];
                    if (eventPlayer?.Player != null)
                        eventPlayer.Player.SendConsoleCommand("chat.add", new object[] { 0, player.UserIDString, $"<color={(player.IsAdmin ? "#aaff55" : player.IsDeveloper ? "#fa5" : "#55AAFF")}>{player.displayName}</color>: {message}" });
                }
            }

            internal void BroadcastToTeam(Team team, string key, string[] args = null)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];
                    if (eventPlayer?.Player != null && eventPlayer.Team == team)
                        BroadcastToPlayer(eventPlayer, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID));
                }
            }

            internal void BroadcastToPlayer(BaseEventPlayer eventPlayer, string message) => eventPlayer?.Player?.SendConsoleCommand("chat.add", 2, Configuration.Message.ChatIcon, message);

            internal void BroadcastRoundWinMessage(string key, params object[] args)
            {
                for (int i = 0; i < eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = eventPlayers[i];

                    CuiElementContainer container = UI.Container(ArenaUI.UI_POPUP, new UI4(0.2f, 0.4f, 0.8f, 0.6f), false);
                    UI.OutlineLabel(container, ArenaUI.UI_POPUP, args != null ? string.Format(Message(key, eventPlayer.Player.userID), args) : Message(key, eventPlayer.Player.userID), 20, UI4.Full);

                    eventPlayer.AddUI(ArenaUI.UI_POPUP, container);
                    InvokeHandler.Invoke(eventPlayer, () => eventPlayer.DestroyUI(ArenaUI.UI_POPUP), 5f);
                }
            }
            #endregion

            #region Teams
            internal bool IsEventTeam(ulong teamID)
            {
                if ((TeamA?.IsEventTeam(teamID) ?? false) || (TeamB?.IsEventTeam(teamID) ?? false))
                    return true;
                return false;
            }

            internal class EventTeam
            {
                internal Team Team { get; private set; }

                internal string Color { get; private set; }

                internal string Clothing { get; private set; }

                internal SpawnSelector Spawns { get; private set; }


                private RelationshipManager.PlayerTeam _playerTeam;

                internal RelationshipManager.PlayerTeam PlayerTeam
                {
                    get
                    {
                        if (_playerTeam == null)
                        {
                            _playerTeam = RelationshipManager.ServerInstance.CreateTeam();

                            _playerTeam.invites.Clear();
                            _playerTeam.members.Clear();
                            _playerTeam.onlineMemberConnections.Clear();

                            _playerTeam.teamName = $"Team {Team}";
                        }
                        return _playerTeam;
                    }
                }

                internal EventTeam(Team team, string color, string clothing, SpawnSelector spawns)
                {
                    this.Team = team;
                    this.Clothing = clothing;
                    this.Spawns = spawns;

                    if (string.IsNullOrEmpty(color) || color.Length < 6 || color.Length > 6 || !hexFilter.IsMatch(color))
                        this.Color = team == Team.A ? "#9b2021" : "#0000d8";
                    else this.Color = "#" + color;
                }

                internal void AddToTeam(BasePlayer player)
                {
                    if (player.currentTeam != 0UL && player.currentTeam != PlayerTeam.teamID)
                    {
                        RelationshipManager.PlayerTeam oldTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                        if (oldTeam != null)
                        {
                            oldTeam.members.Remove(player.userID);
                            player.ClearTeam();
                        }
                    }

                    player.currentTeam = PlayerTeam.teamID;

                    if (!PlayerTeam.members.Contains(player.userID))
                        PlayerTeam.members.Add(player.userID);

                    RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);
                    RelationshipManager.ServerInstance.playerToTeam.Add(player.userID, PlayerTeam);

                    player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                    player.TeamUpdate();

                    PlayerTeam.MarkDirty();
                }

                //internal void AddToTeam(ArenaAI.HumanAI humanAI)
                //{

                //}

                internal void RemoveFromTeam(BasePlayer player)
                {
                    if (_playerTeam != null)
                    {
                        _playerTeam.members.Remove(player.userID);
                        RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);

                        if (player != null)
                        {
                            player.ClearTeam();
                            player.BroadcastAppTeamRemoval();
                        }
                    }
                }

                //internal void RemoveFromTeam(ArenaAI.HumanAI humanAI)
                //{

                //}

                internal void Destroy()
                {
                    Spawns.Destroy();

                    if (_playerTeam != null)
                    {
                        for (int i = _playerTeam.members.Count - 1; i >= 0; i--)
                        {
                            ulong playerID = _playerTeam.members[i];

                            _playerTeam.members.Remove(playerID);
                            RelationshipManager.ServerInstance.playerToTeam.Remove(playerID);

                            BasePlayer basePlayer = RelationshipManager.FindByID(playerID);
                            if (basePlayer != null)
                            {
                                basePlayer.ClearTeam();
                                basePlayer.BroadcastAppTeamRemoval();
                            }
                        }

                        RelationshipManager.ServerInstance.teams.Remove(_playerTeam.teamID);

                        _playerTeam.invites.Clear();
                        _playerTeam.members.Clear();
                        _playerTeam.onlineMemberConnections.Clear();
                        _playerTeam.teamID = 0UL;
                        _playerTeam.teamLeader = 0UL;
                        _playerTeam.teamName = string.Empty;

                        Pool.Free(ref _playerTeam);
                    }
                }

                internal bool IsEventTeam(ulong teamID)
                {
                    if (_playerTeam == null)
                        return false;

                    return _playerTeam.teamID.Equals(teamID);
                }
            }
            #endregion

            #region Event Teleporter
            private bool CanEnterTeleporter(BasePlayer player)
            {
                if (!string.IsNullOrEmpty(Config.Permission) && !Instance.permission.UserHasPermission(player.UserIDString, Config.Permission))
                {
                    player.ChatMessage(Message("Error.NoPermission", player.userID));
                    return false;
                }

                if (Instance.permission.UserHasPermission(player.UserIDString, BLACKLISTED_PERMISSION))
                {
                    player.ChatMessage(Message("Error.Blacklisted", player.userID));
                    return false;
                }

                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer != null)
                    return false;

                if (!CanJoinEvent(player))
                    return false;

                return true;
            }

            private void EnterTeleporter(BasePlayer player) => JoinEvent(player, GetPlayerTeam());

            private string GetTeleporterInformation()
            {
                return string.Format(Message("Info.Teleporter"), Config.EventName,
                        Config.EventType,
                        eventPlayers.Count,
                        Config.MaximumPlayers,
                        _roundNumber,
                        Config.RoundsToPlay,
                        Status,
                        Plugin.UseScoreLimit ? string.Format(Message("Info.Teleporter.Score"), Config.ScoreLimit) :
                        Plugin.UseTimeLimit ? string.Format(Message("Info.Teleporter.Time"), Config.TimeLimit == 0 ? "Unlimited" : Config.TimeLimit.ToString()) : string.Empty,
                        Config.AllowClassSelection ? "Class Selector" : "Event Specific");
            }

            private void CreateTeleporter()
            {
                if (Config.Teleporter == null)
                    return;

                CreateTeleporter(Config.Teleporter);
            }

            internal void CreateTeleporter(EventConfig.SerializedTeleporter serializedTeleporter)
            {
                if (!Configuration.Lobby.LobbyEnabled || string.IsNullOrEmpty(Configuration.Lobby.LobbySpawnfile))
                {
                    Debug.LogWarning($"Failed setting up event teleporter for {Config.EventName}\nTeleporters require the lobby to be enabled and a valid lobby spawn file");
                    return;
                }

                _eventTeleporter = EventTeleporter.Create(serializedTeleporter.Position, serializedTeleporter.Radius);
                _eventTeleporter.Initialize(CanEnterTeleporter, EnterTeleporter, GetTeleporterInformation);
            }

            internal void DestroyTeleporter()
            {
                if (_eventTeleporter != null)
                {
                    Destroy(_eventTeleporter);
                    _eventTeleporter = null;
                }
            }
            #endregion
        }

        public class NPCEventPlayer : BaseEventPlayer
        {
            //protected override void Awake()
            //{
            //    Player = GetComponent<BasePlayer>();

            //    Transform = Player.transform;

            //    if (Configuration.Event.Health.Enabled)
            //    {
            //        _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;
            //        InvokeHandler.InvokeRepeating(this, HealthRestoreTick, 1f, 1f);
            //    }
            //}

            internal override void AddUI(string panel, CuiElementContainer container) { }

            internal override void DestroyUI() { }

            internal override void DestroyUI(string panel) { }

            internal override void AddPlayerDeath(BaseEventPlayer attacker = null)
            {
                Deaths++;
            }

            internal override void OnPlayerDeath(BaseEventPlayer attacker = null, float respawnTime = 5, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                InvokeHandler.Invoke(this, ()=> RespawnPlayer(this), respawnTime);
            }
        }

        public class BaseEventPlayer : MonoBehaviour
        {
            protected float _respawnDurationRemaining;

            protected float _invincibilityEndsAt;

            private double _resetDamageTime;

            protected List<ulong> _damageContributors = Pool.GetList<ulong>();

            protected bool _isOOB;

            protected int _oobTime;

            protected int _spectateIndex = 0;

            protected double _nextHealthRestoreTime;


            internal BasePlayer Player { get; set; }

            internal BaseEventGame Event { get; set; }

            internal Transform Transform { get; set; }

            internal Team Team { get; set; } = Team.None;

            internal int Kills { get; set; }

            internal int Deaths { get; set; }



            internal bool IsDead { get; set; }

            internal bool AutoRespawn { get; set; }

            internal bool CanRespawn => _respawnDurationRemaining <= 0;

            internal int RespawnRemaining => Mathf.CeilToInt(_respawnDurationRemaining);

            internal bool IsInvincible => Time.time < _invincibilityEndsAt;


            internal BaseEventPlayer SpectateTarget { get; private set; } = null;


            internal string Kit { get; set; }


            internal bool IsOutOfBounds
            {
                get
                {
                    return _isOOB;
                }
                set
                {
                    if (value)
                    {
                        _oobTime = 10;
                        InvokeHandler.Invoke(this, TickOutOfBounds, 1f);
                    }
                    else InvokeHandler.CancelInvoke(this, TickOutOfBounds);

                    _isOOB = value;
                }
            }

            protected virtual void Awake()
            {
                Player = GetComponent<BasePlayer>();

                Transform = Player.transform;

                Instance.Restore.AddData(Player);

                Player.metabolism.bleeding.max = 0;
                Player.metabolism.bleeding.value = 0;
                Player.metabolism.radiation_level.max = 0;
                Player.metabolism.radiation_level.value = 0;
                Player.metabolism.radiation_poison.max = 0;
                Player.metabolism.radiation_poison.value = 0;

                Player.metabolism.SendChangesToClient();

                Interface.Call("DisableBypass", Player.userID);

                if (Configuration.Event.Health.Enabled)
                {
                    _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;
                    InvokeHandler.InvokeRepeating(this, HealthRestoreTick, 1f, 1f);
                }
            }

            protected virtual void OnDestroy()
            {
                if (Player.IsSpectating())
                    FinishSpectating();

                Player.limitNetworking = false;

                Player.EnablePlayerCollider();

                Player.health = Player.MaxHealth();

                Player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                Player.metabolism.bleeding.max = 1;
                Player.metabolism.bleeding.value = 0;
                Player.metabolism.radiation_level.max = 100;
                Player.metabolism.radiation_level.value = 0;
                Player.metabolism.radiation_poison.max = 500;
                Player.metabolism.radiation_poison.value = 0;

                Player.metabolism.SendChangesToClient();

                Interface.Call("EnableBypass", Player.userID);

                if (Player.isMounted)
                    Player.GetMounted()?.AttemptDismount(Player);

                DestroyUI();

                if (IsUnloading)
                    StripInventory(Player);

                UnlockClothingSlots(Player);

                InvokeHandler.CancelInvoke(this, TickOutOfBounds);
                InvokeHandler.CancelInvoke(this, HealthRestoreTick);

                Pool.FreeList(ref _damageContributors);
                Pool.FreeList(ref _openPanels);
            }

            internal void ResetPlayer()
            {
                Team = Team.None;
                Kills = 0;
                Deaths = 0;
                IsDead = false;
                AutoRespawn = false;
                Kit = string.Empty;

                _spectateIndex = 0;
                _respawnDurationRemaining = 0;
                _invincibilityEndsAt = 0;
                _resetDamageTime = 0;
                _oobTime = 0;
                _isOOB = false;

                _damageContributors.Clear();
            }

            internal virtual void ResetStatistics()
            {
                Kills = 0;
                Deaths = 0;

                _spectateIndex = 0;
                _respawnDurationRemaining = 0;
                _invincibilityEndsAt = 0;
                _resetDamageTime = 0;
                _oobTime = 0;
                _isOOB = false;

                _damageContributors.Clear();
            }

            internal void ForceSelectClass()
            {
                IsDead = true;
            }

            protected void RespawnTick()
            {
                if (!IsDead)
                    return;

                _respawnDurationRemaining = Mathf.Clamp(_respawnDurationRemaining - 1f, 0f, float.MaxValue);

                ArenaUI.UpdateRespawnButton(this);

                if (_respawnDurationRemaining <= 0f)
                {
                    InvokeHandler.CancelInvoke(this, RespawnTick);

                    if (AutoRespawn)
                        RespawnPlayer(this);
                }
            }

            internal void OnRoundFinished()
            {
                if (IsDead)
                {
                    InvokeHandler.CancelInvoke(this, RespawnTick);
                    _respawnDurationRemaining = 0;
                }
            }

            #region Death
            internal void OnKilledPlayer(HitInfo hitInfo)
            {
                Kills++;

                int rewardAmount = Event.Config.Rewards.KillAmount;

                ArenaStatistics.Data.AddStatistic(Player, "Kills");

                if (hitInfo != null)
                {
                    if (hitInfo.damageTypes.IsMeleeType())
                        ArenaStatistics.Data.AddStatistic(Player, "Melee");

                    if (hitInfo.isHeadshot)
                    {
                        ArenaStatistics.Data.AddStatistic(Player, "Headshots");
                        rewardAmount = Event.Config.Rewards.HeadshotAmount;
                    }
                }

                if (rewardAmount > 0)
                    Instance.GiveReward(this, Event.RewardType, rewardAmount);
            }

            internal virtual void OnPlayerDeath(BaseEventPlayer attacker = null, float respawnTime = 5f, HitInfo hitInfo = null)
            {
                AddPlayerDeath(attacker);

                _respawnDurationRemaining = respawnTime;

                InvokeHandler.InvokeRepeating(this, RespawnTick, 1f, 1f);

                DestroyUI();

                string message = attacker != null ? string.Format(Message("UI.Death.Killed", Player.userID), attacker.Player.displayName) :
                                 IsOutOfBounds ? Message("UI.Death.OOB", Player.userID) :
                                 Message("UI.Death.Suicide", Player.userID);

                ArenaUI.DisplayDeathScreen(this, message, true);
            }

            internal virtual void AddPlayerDeath(BaseEventPlayer attacker = null)
            {
                Deaths++;
                ArenaStatistics.Data.AddStatistic(Player, "Deaths");
                ApplyAssistPoints(attacker);
            }

            protected void ApplyAssistPoints(BaseEventPlayer attacker = null)
            {
                if (_damageContributors.Count > 1)
                {
                    for (int i = 0; i < _damageContributors.Count - 1; i++)
                    {
                        ulong contributorId = _damageContributors[i];
                        if (attacker != null && attacker.Player.userID == contributorId)
                            continue;

                        ArenaStatistics.Data.AddStatistic(contributorId, "Assists");
                    }
                }

                _resetDamageTime = 0;
                _damageContributors.Clear();
            }

            internal void ApplyInvincibility() => _invincibilityEndsAt = Time.time + Configuration.Event.InvincibilityTime;
            #endregion

            protected void TickOutOfBounds()
            {
                if (Player == null)
                {
                    Event.LeaveEvent(this);
                    return;
                }

                if (IsDead || Player.IsSpectating())
                    return;

                if (IsOutOfBounds)
                {
                    if (_oobTime == 10)
                        Event.BroadcastToPlayer(this, Message("Notification.OutOfBounds", Player.userID));
                    else if (_oobTime == 0)
                    {
                        Effect.server.Run("assets/prefabs/tools/c4/effects/c4_explosion.prefab", Player.transform.position);

                        if (Event.Status == EventStatus.Started)
                            Event.PrePlayerDeath(this, null);
                        else Event.SpawnPlayer(this, false);
                    }
                    else Event.BroadcastToPlayer(this, string.Format(Message("Notification.OutOfBounds.Time", Player.userID), _oobTime));

                    _oobTime--;

                    InvokeHandler.Invoke(this, TickOutOfBounds, 1f);
                }
            }

            #region Drop On Death
            internal void DropInventory()
            {
                const string BACKPACK_PREFAB = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";

                DroppedItemContainer itemContainer = ItemContainer.Drop(BACKPACK_PREFAB, Player.transform.position, Quaternion.identity, new ItemContainer[] { Player.inventory.containerBelt, Player.inventory.containerMain });
                if (itemContainer != null)
                {
                    itemContainer.playerName = Player.displayName;
                    itemContainer.playerSteamID = Player.userID;

                    itemContainer.CancelInvoke(itemContainer.RemoveMe);
                    itemContainer.Invoke(itemContainer.RemoveMe, Configuration.Timer.Corpse);

                    Event.OnInventorySpawned(itemContainer);
                }
            }

            internal void DropCorpse()
            {
                const string CORPSE_PREFAB = "assets/prefabs/player/player_corpse.prefab";

                PlayerCorpse playerCorpse = Player.DropCorpse(CORPSE_PREFAB) as PlayerCorpse;
                if (playerCorpse != null)
                {
                    playerCorpse.TakeFrom(new ItemContainer[] { Player.inventory.containerMain, Player.inventory.containerBelt });
                    playerCorpse.playerName = Player.displayName;
                    playerCorpse.playerSteamID = Player.userID;
                    playerCorpse.underwearSkin = Player.GetUnderwearSkin();
                    playerCorpse.Spawn();
                    playerCorpse.TakeChildren(Player);

                    playerCorpse.ResetRemovalTime(Configuration.Timer.Corpse);

                    Event.OnCorpseSpawned(playerCorpse);
                }
            }

            internal void DropWeapon()
            {
                Item item = Player.GetActiveItem();
                if (item != null)
                {
                    DroppedItem droppedItem = item.Drop(Player.transform.position, Vector3.up) as DroppedItem;
                    droppedItem.CancelInvoke(droppedItem.IdleDestroy);
                    droppedItem.Invoke(droppedItem.IdleDestroy, 30f);

                    RotateDroppedItem(droppedItem);

                    Event.OnWorldItemDropped(droppedItem);
                }
            }

            internal void DropAmmo()
            {
                Item item = Player.GetActiveItem();
                if (item != null)
                {
                    BaseProjectile baseProjectile = item.GetHeldEntity() as BaseProjectile;
                    if (baseProjectile != null && baseProjectile.primaryMagazine.contents > 0)
                    {
                        Item ammo = ItemManager.Create(baseProjectile.primaryMagazine.ammoType, baseProjectile.primaryMagazine.contents);

                        DroppedItem droppedItem = ammo.Drop(Player.transform.position, Vector3.up) as DroppedItem;
                        droppedItem.CancelInvoke(droppedItem.IdleDestroy);
                        droppedItem.Invoke(droppedItem.IdleDestroy, 30f);

                        baseProjectile.primaryMagazine.contents = 0;

                        RotateDroppedItem(droppedItem);

                        Event.OnWorldItemDropped(droppedItem);
                    }
                }
            }
            #endregion

            #region Networking
            internal void RemoveFromNetwork()
            {
                if (Net.sv.write.Start())
                {
                    Net.sv.write.PacketID(Network.Message.Type.EntityDestroy);
                    Net.sv.write.EntityID(Player.net.ID);
                    Net.sv.write.UInt8((byte)BaseNetworkable.DestroyMode.None);
                    Net.sv.write.Send(new SendInfo(Player.net.group.subscribers.Where(x => x.userid != Player.userID).ToList()));
                }
            }

            internal void AddToNetwork() => Player.SendFullSnapshot();
            #endregion

            #region Damage Contributors
            internal void OnTakeDamage(ulong attackerId)
            {
                _nextHealthRestoreTime = Time.time + Configuration.Event.Health.RestoreAfter;

                float time = Time.realtimeSinceStartup;
                if (time > _resetDamageTime)
                {
                    _resetDamageTime = time + 3f;
                    _damageContributors.Clear();
                }

                if (attackerId != 0U && attackerId != Player.userID)
                {
                    if (_damageContributors.Contains(attackerId))
                        _damageContributors.Remove(attackerId);
                    _damageContributors.Add(attackerId);
                }
            }

            internal List<ulong> DamageContributors => _damageContributors;
            #endregion

            #region Health Restoration
            protected void HealthRestoreTick()
            {
                if (Player == null || IsDead)
                    return;

                if (Time.time > _nextHealthRestoreTime && Player.health < Player.MaxHealth())
                {
                    Player.health = Mathf.Clamp(Player._health + Configuration.Event.Health.Amount, 0f, Player._maxHealth);
                    Player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }
            #endregion

            #region Spectating
            public void BeginSpectating()
            {
                if (Player.IsSpectating())
                    return;

                DestroyUI();

                Player.limitNetworking = true;
                RemoveFromNetwork();

                Player.StartSpectating();
                Player.ChatMessage(Message("Notification.SpectateCycle", Player.userID));

                UpdateSpectateTarget();
            }

            public void FinishSpectating()
            {
                if (!Player.IsSpectating())
                    return;

                Player.limitNetworking = false;
                AddToNetwork();

                Player.SetParent(null, false, true);
                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
                Player.gameObject.SetLayerRecursive(17);

                SpectateTarget = null;
                DestroyUI(ArenaUI.UI_SPECTATE);
            }

            public void SetSpectateTarget(BaseEventPlayer eventPlayer)
            {
                SpectateTarget = eventPlayer;

                if (eventPlayer != null && eventPlayer.Player != null)
                {
                    Event.BroadcastToPlayer(this, $"Spectating: {eventPlayer.Player.displayName}");
                    ArenaUI.DisplaySpectateScreen(this, eventPlayer.Player.displayName);

                    Player.SendEntitySnapshot(eventPlayer.Player);
                    Player.gameObject.Identity();
                    Player.SetParent(eventPlayer.Player, false, true);
                }
                else
                {
                    Event.BroadcastToPlayer(this, "Player spectating paused. Waiting for spectating targets...");
                    ArenaUI.DisplaySpectateScreen(this, "No one");

                    Player.gameObject.Identity();
                    Player.SetParent(null, true, true);
                }
            }

            public void UpdateSpectateTarget()
            {
                if (Event.SpectateTargets.Count == 0)
                {
                    Arena.ResetPlayer(Player);
                    Event.OnPlayerRespawn(this);
                }
                else
                {
                    _spectateIndex += 1;

                    if (_spectateIndex >= Event.SpectateTargets.Count)
                        _spectateIndex = 0;

                    SetSpectateTarget(Event.SpectateTargets.ElementAt(_spectateIndex));
                }
            }
            #endregion

            #region UI Management
            private List<string> _openPanels = Pool.GetList<string>();

            internal virtual void AddUI(string panel, CuiElementContainer container)
            {
                DestroyUI(panel);

                _openPanels.Add(panel);

                if (Player != null && Player.IsConnected)
                    CuiHelper.AddUi(Player, container);
            }

            internal virtual void DestroyUI()
            {
                _openPanels.ForEach((string s) =>
                {
                    if (Player != null && Player.IsConnected)
                        CuiHelper.DestroyUi(Player, s);
                });

                _openPanels.Clear();
            }

            internal virtual void DestroyUI(string panel)
            {
                if (_openPanels.Contains(panel))
                    _openPanels.Remove(panel);

                if (Player != null && Player.IsConnected)
                    CuiHelper.DestroyUi(Player, panel);
            }
            #endregion
        }

        #region Event Teleporter
        public class EventTeleporter : MonoBehaviour
        {
            public SphereEntity Entity { get; private set; }


            private Func<BasePlayer, bool> canEnterTrigger;

            private Action<BasePlayer> onEnterTrigger;

            private Func<string> getInformationString;


            private Vector3 position;

            private const string SPHERE_ENTITY = "assets/prefabs/visualization/sphere.prefab";

            private const float REFRESH_RATE = 2f;

            private bool isTriggerReady = false;

            private void Awake()
            {
                Entity = GetComponent<SphereEntity>();
                position = Entity.transform.position;
            }

            private void OnDestroy()
            {
                InvokeHandler.CancelInvoke(this, InformationTick);
                Entity.Kill();
                Destroy(gameObject);
            }

            private void OnTriggerEnter(Collider col)
            {
                if (!isTriggerReady)
                    return;

                BasePlayer player = col.gameObject?.ToBaseEntity()?.ToPlayer();
                if (player == null)
                    return;

                if (player.isMounted)
                    return;

                if (!canEnterTrigger(player))
                    return;

                onEnterTrigger(player);
            }

            public void Initialize(Func<BasePlayer, bool> canEnterTrigger, Action<BasePlayer> onEnterTrigger, Func<string> getInformationString)
            {
                this.canEnterTrigger = canEnterTrigger;
                this.onEnterTrigger = onEnterTrigger;
                this.getInformationString = getInformationString;

                InvokeHandler.InvokeRepeating(this, InformationTick, UnityEngine.Random.Range(0.1f, 2f), REFRESH_RATE);

                isTriggerReady = true;
            }

            private void InformationTick()
            {
                List<BasePlayer> list = Pool.GetList<BasePlayer>();
                Vis.Entities(position, 10f, list);

                if (list.Count > 0)
                {
                    string informationStr = getInformationString.Invoke();

                    for (int i = 0; i < list.Count; i++)
                    {
                        BasePlayer player = list[i];
                        if (player == null || player.IsDead() || player.isMounted)
                            continue;

                        if (player.IsAdmin)
                            player.SendConsoleCommand("ddraw.text", REFRESH_RATE, Color.white, position, informationStr);
                        else
                        {
                            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                            player.SendNetworkUpdateImmediate();
                            player.SendConsoleCommand("ddraw.text", REFRESH_RATE, Color.white, position, informationStr);
                            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                            player.SendNetworkUpdateImmediate();
                        }
                    }
                }

                Pool.FreeList(ref list);
            }

            public static EventTeleporter Create(Vector3 position, float radius)
            {
                SphereEntity sphereEntity = GameManager.server.CreateEntity(SPHERE_ENTITY, position, Quaternion.identity) as SphereEntity;
                sphereEntity.currentRadius = sphereEntity.lerpRadius = radius * 2f;

                sphereEntity.enableSaving = false;
                sphereEntity.Spawn();

                sphereEntity.gameObject.layer = (int)Rust.Layer.Reserved2;

                SphereCollider sphereCollider = sphereEntity.gameObject.AddComponent<SphereCollider>();
                sphereCollider.radius = radius;
                sphereCollider.isTrigger = true;

                return sphereEntity.gameObject.AddComponent<EventTeleporter>();
            }
        }

        #region API
        private Hash<Plugin, Hash<string, EventTeleporter>> _pluginTeleporters = new Hash<Plugin, Hash<string, EventTeleporter>>();

        private bool CreateEventTeleporter(Plugin plugin, string teleporterID, Vector3 position, float radius, Func<BasePlayer, bool> canEnterTrigger, Action<BasePlayer> onEnterTrigger, Func<string> getInformationString)
        {
            if (!Configuration.Lobby.LobbyEnabled || string.IsNullOrEmpty(Configuration.Lobby.LobbySpawnfile))
            {
                Debug.LogWarning($"Failed setting up event teleporter for {plugin.Name}\nTeleporters require the lobby to be enabled and a valid lobby spawn file");
                return false;
            }

            Hash<string, EventTeleporter> teleporters;
            if (!_pluginTeleporters.TryGetValue(plugin, out teleporters))
                teleporters = _pluginTeleporters[plugin] = new Hash<string, EventTeleporter>();

            EventTeleporter eventTeleporter;
            if (teleporters.TryGetValue(teleporterID, out eventTeleporter))
                UnityEngine.Object.Destroy(eventTeleporter.gameObject);

            eventTeleporter = EventTeleporter.Create(position, radius);
            eventTeleporter.Initialize(canEnterTrigger, onEnterTrigger, getInformationString);

            teleporters[teleporterID] = eventTeleporter;
            return true;
        }

        private void DestroyEventTeleporter(Plugin plugin, string teleporterID)
        {
            Hash<string, EventTeleporter> teleporters;
            if (!_pluginTeleporters.TryGetValue(plugin, out teleporters))
                return;

            EventTeleporter eventTeleporter;
            if (!teleporters.TryGetValue(teleporterID, out eventTeleporter))
                return;

            teleporters.Remove(teleporterID);
            UnityEngine.Object.Destroy(eventTeleporter.gameObject);
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            Hash<string, EventTeleporter> teleporters;
            if (!_pluginTeleporters.TryGetValue(plugin, out teleporters))
                return;

            foreach (EventTeleporter eventTeleporter in teleporters.Values)
                UnityEngine.Object.Destroy(eventTeleporter.gameObject);

            _pluginTeleporters.Remove(plugin);
        }

        private void DestroyTeleporters()
        {
            foreach(Hash<string, EventTeleporter> teleporters in _pluginTeleporters.Values)
            {
                foreach(EventTeleporter eventTeleporter in teleporters.Values)
                {
                    UnityEngine.Object.Destroy(eventTeleporter.gameObject);
                }
            }

            _pluginTeleporters.Clear();
        }
        #endregion
        #endregion

        #region Event Timer
        public class GameTimer
        {
            private BaseEventGame _owner = null;

            private string _message;
            private int _timeRemaining;
            private Action _callback;

            internal GameTimer(BaseEventGame owner)
            {
                _owner = owner;
            }

            internal void StartTimer(int time, string message = "", Action callback = null)
            {
                this._timeRemaining = time;
                this._message = message;
                this._callback = callback;

                InvokeHandler.InvokeRepeating(_owner, TimerTick, 1f, 1f);
            }

            internal void StopTimer()
            {
                InvokeHandler.CancelInvoke(_owner, TimerTick);

                for (int i = 0; i < _owner?.eventPlayers?.Count; i++)
                    _owner.eventPlayers[i]?.DestroyUI(ArenaUI.UI_TIMER);
            }

            private void TimerTick()
            {
                _timeRemaining--;
                if (_timeRemaining == 0)
                {
                    StopTimer();
                    _callback?.Invoke();
                }
                else UpdateTimer();
            }

            private void UpdateTimer()
            {
                string clockTime = string.Empty;

                TimeSpan dateDifference = TimeSpan.FromSeconds(_timeRemaining);
                int hours = dateDifference.Hours;
                int mins = dateDifference.Minutes;
                int secs = dateDifference.Seconds;

                if (hours > 0)
                    clockTime = string.Format("{0:00}:{1:00}:{2:00}", hours, mins, secs);
                else clockTime = string.Format("{0:00}:{1:00}", mins, secs);

                CuiElementContainer container = UI.Container(ArenaUI.UI_TIMER, "0.1 0.1 0.1 0.7", new UI4(0.46f, 0.92f, 0.54f, 0.95f), false, "Hud");

                UI.Label(container, ArenaUI.UI_TIMER, clockTime, 14, UI4.Full);

                if (!string.IsNullOrEmpty(_message))
                    UI.Label(container, ArenaUI.UI_TIMER, _message, 14, new UI4(-5f, 0f, -0.1f, 1), TextAnchor.MiddleRight);

                for (int i = 0; i < _owner.eventPlayers.Count; i++)
                {
                    BaseEventPlayer eventPlayer = _owner.eventPlayers[i];
                    if (eventPlayer == null)
                        continue;

                    eventPlayer.DestroyUI(ArenaUI.UI_TIMER);
                    eventPlayer.AddUI(ArenaUI.UI_TIMER, container);
                }

                for (int i = 0; i < _owner.joiningSpectators.Count; i++)
                {
                    BaseEventPlayer eventPlayer = _owner.joiningSpectators[i];
                    if (eventPlayer == null)
                        continue;

                    eventPlayer.DestroyUI(ArenaUI.UI_TIMER);
                    eventPlayer.AddUI(ArenaUI.UI_TIMER, container);
                }
            }
        }
        #endregion

        #region Spawn Management
        internal class SpawnSelector
        {
            protected List<Vector3> _defaultSpawns;
            protected List<Vector3> _availableSpawns;

            internal virtual int Count { get; private set; }

            internal SpawnSelector() { }
            internal SpawnSelector(string spawnFile)
            {
                _defaultSpawns = Instance.Spawns.Call("LoadSpawnFile", spawnFile) as List<Vector3>;
                _availableSpawns = Pool.GetList<Vector3>();
                _availableSpawns.AddRange(_defaultSpawns);

                Count = _availableSpawns.Count;
            }

            internal Vector3 GetSpawnPoint()
            {
                Vector3 point = _availableSpawns.GetRandom();
                _availableSpawns.Remove(point);

                if (_availableSpawns.Count == 0)
                    _availableSpawns.AddRange(_defaultSpawns);

                return point;
            }

            internal Vector3 ReserveSpawnPoint(int index)
            {
                Vector3 reserved = _defaultSpawns[index];
                _defaultSpawns.RemoveAt(index);

                _availableSpawns.Clear();
                _availableSpawns.AddRange(_defaultSpawns);

                return reserved;
            }

            internal void Destroy()
            {
                Pool.FreeList(ref _availableSpawns);
            }
        }
        #endregion

        #region Event Config
        public class EventConfig
        {
            public string EventName { get; set; } = string.Empty;
            public string EventType { get; set; } = string.Empty;

            public string ZoneID { get; set; } = string.Empty;
            public string Permission { get; set; } = string.Empty;

            public string EventIcon { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;

            public int TimeLimit { get; set; }
            public int ScoreLimit { get; set; }
            public int MinimumPlayers { get; set; }
            public int MaximumPlayers { get; set; }

            public bool UseEventBots { get; set; }
            public int MaximumBots { get; set; }

            public bool AllowClassSelection { get; set; }

            public bool IsDisabled { get; set; }

            public int RoundsToPlay { get; set; }

            public TeamConfig TeamConfigA { get; set; } = new TeamConfig();

            public TeamConfig TeamConfigB { get; set; } = new TeamConfig();

            public RewardOptions Rewards { get; set; } = new RewardOptions();

            public SerializedTeleporter Teleporter { get; set; }

            public Hash<string, object> AdditionalParams { get; set; } = new Hash<string, object>();

            public EventConfig() { }

            public EventConfig(string type, IEventPlugin eventPlugin)
            {
                this.EventType = type;
                this.Plugin = eventPlugin;

                if (eventPlugin.AdditionalParameters != null)
                {
                    for (int i = 0; i < eventPlugin.AdditionalParameters.Count; i++)
                    {
                        EventParameter eventParameter = eventPlugin.AdditionalParameters[i];

                        if (eventParameter.DefaultValue == null && eventParameter.IsList)
                            AdditionalParams[eventParameter.Field] = new List<string>();
                        else AdditionalParams[eventParameter.Field] = eventParameter.DefaultValue;
                    }
                }
            }

            public T GetParameter<T>(string key)
            {
                try
                {
                    object obj;
                    if (AdditionalParams.TryGetValue(key, out obj))
                        return (T)Convert.ChangeType(obj, typeof(T));
                }
                catch { }

                return default(T);
            }

            public string GetString(string fieldName)
            {
                switch (fieldName)
                {
                    case "teamASpawnfile":
                        return TeamConfigA.Spawnfile;
                    case "teamBSpawnfile":
                        return TeamConfigB.Spawnfile;
                    case "zoneID":
                        return ZoneID;
                    default:
                        object obj;
                        if (AdditionalParams.TryGetValue(fieldName, out obj) && obj is string)
                            return obj as string;
                        return null;
                }
            }

            public List<string> GetList(string fieldName)
            {
                switch (fieldName)
                {
                    case "teamAKits":
                        return TeamConfigA.Kits;
                    case "teamBKits":
                        return TeamConfigB.Kits;
                    default:
                        object obj;
                        if (AdditionalParams.TryGetValue(fieldName, out obj) && obj is List<string>)
                            return obj as List<string>;
                        return null;
                }
            }

            public string TeamName(Team team)
            {
                TeamConfig teamConfig = team == Team.B ? TeamConfigB : TeamConfigA;

                return string.IsNullOrEmpty(teamConfig.Name) ? (team == Team.B ? Plugin.TeamBName : Plugin.TeamAName) : teamConfig.Name;
            }

            public class RewardOptions
            {
                public int KillAmount { get; set; }

                public int WinAmount { get; set; }

                public int HeadshotAmount { get; set; }

                public string Type { get; set; } = "Scrap";
            }

            public class TeamConfig
            {
                public string Name { get; set; } = string.Empty;

                public string Color { get; set; } = string.Empty;

                public string Spawnfile { get; set; } = string.Empty;

                public string Clothing { get; set; } = string.Empty;

                public List<string> Kits { get; set; } = new List<string>();
            }

            public class SerializedTeleporter
            {
                public float X, Y, Z;

                public float Radius = 0.75f;

                [JsonIgnore]
                public Vector3 Position => new Vector3(X, Y, Z);

                public SerializedTeleporter() { }

                public SerializedTeleporter(Vector3 v)
                {
                    this.X = v.x;
                    this.Y = v.y;
                    this.Z = v.z;
                }
            }

            [JsonIgnore]
            public IEventPlugin Plugin { get; set; }
        }
        #endregion
        #endregion

        #region Rewards
        private void GiveReward(BaseEventPlayer baseEventPlayer, RewardType rewardType, int amount)
        {
            if (amount <= 0)
                return;

            switch (rewardType)
            {
                case RewardType.ServerRewards:
                    ServerRewards?.Call("AddPoints", baseEventPlayer.Player.UserIDString, amount);
                    break;
                case RewardType.Economics:
                    Economics?.Call("Deposit", baseEventPlayer.Player.UserIDString, (double)amount);
                    break;
                case RewardType.Scrap:
                    Restore.AddPrizeToData(baseEventPlayer.Player.userID, scrapItemId, amount);
                    break;
            }
        }

        private string[] GetRewardTypes() => new string[] { "Scrap", "ServerRewards", "Economics" };
        #endregion

        #region Enums
        public enum RewardType { ServerRewards, Economics, Scrap }

        public enum EventStatus { Finished, Open, Prestarting, Started }

        public enum Team { A, B, None }

        public enum DropOnDeath { Nothing, Ammo, Backpack, Corpse, Weapon }
        #endregion

        #region Helpers
        private static T ParseType<T>(string type)
        {
            try
            {
                return (T)Enum.Parse(typeof(T), type, true);
            }
            catch
            {
                return default(T);
            }
        }

        internal static BaseEventPlayer GetUser(BasePlayer player) => player?.GetComponent<BaseEventPlayer>();

        internal static void TeleportPlayer(BasePlayer player, Vector3 destination, bool sleep)
        {
            if (player.isMounted)
                player.GetMounted().DismountPlayer(player, true);

            if (player.GetParentEntity() != null)
                player.SetParent(null, true, true);

            player.RemoveFromTriggers();
            //if (player is global::HumanNPC)
            //{
            //    NavMeshHit navMeshHit;
            //    if (NavMesh.SamplePosition(player.transform.position + (Vector3.up * 1f), out navMeshHit, 20f, -1))
            //        destination = navMeshHit.position;

            //    (player as global::HumanNPC).NavAgent.Warp(destination);
            //    (player as global::HumanNPC).ServerPosition = player.transform.position = destination;

            //    player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            //    return;
            //}

            try
            {
                if (sleep && player.IsConnected)
                {
                    player.StartSleeping();
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

                    player.DisablePlayerCollider();
                    player.SetServerFall(true);
                }

                player.MovePosition(destination);
                player.ClientRPCPlayer(null, player, "ForcePositionTo", destination);

                if (player.IsConnected)
                {
                    player.UpdateNetworkGroup();
                    player.SendNetworkUpdateImmediate(false);

                    if (sleep)
                    {
                        player.ClearEntityQueue(null);
                        player.SendFullSnapshot();
                    }
                }
            }
            finally
            {
                player.EnablePlayerCollider();
                player.SetServerFall(false);
            }
        }

        internal static void LockClothingSlots(BasePlayer player)
        {
            if (player == null)
                return;

            if (!player.inventory.containerWear.HasFlag(ItemContainer.Flag.IsLocked))
            {
                player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, true);
                player.inventory.SendSnapshot();
            }
        }

        internal static void UnlockClothingSlots(BasePlayer player)
        {
            if (player == null)
                return;

            if (player.inventory.containerWear.HasFlag(ItemContainer.Flag.IsLocked))
            {
                player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, false);
                player.inventory.SendSnapshot();
            }
        }

        internal static void StripInventory(BasePlayer player)
        {
            Item[] allItems = player.inventory.AllItems();

            for (int i = allItems.Length - 1; i >= 0; i--)
            {
                Item item = allItems[i];
                item.RemoveFromContainer();
                item.Remove();
            }
        }

        internal static void ResetMetabolism(BasePlayer player)
        {
            player.health = player.MaxHealth();

            player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

            player.metabolism.calories.value = player.metabolism.calories.max;
            player.metabolism.hydration.value = player.metabolism.hydration.max;
            player.metabolism.heartrate.Reset();

            player.metabolism.bleeding.value = 0;
            player.metabolism.radiation_level.value = 0;
            player.metabolism.radiation_poison.value = 0;
            player.metabolism.SendChangesToClient();
        }

        internal static void GiveKit(BasePlayer player, string kitname) => Instance.Kits?.Call("GiveKit", player, kitname);

        internal static void ClearDamage(HitInfo hitInfo)
        {
            if (hitInfo == null)
                return;

            hitInfo.damageTypes.Clear();
            hitInfo.HitEntity = null;
            hitInfo.HitMaterial = 0;
            hitInfo.PointStart = Vector3.zero;
        }

        internal static void ResetPlayer(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (eventPlayer == null)
                return;

            if (player is ScientistNPC)
            {
                player.limitNetworking = false;

                player.health = player.MaxHealth();

                player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                eventPlayer.IsDead = false;
            }
            else
            {
                if (eventPlayer.Player.IsSpectating())
                    eventPlayer.FinishSpectating();

                player.limitNetworking = false;

                player.EnablePlayerCollider();

                player.health = player.MaxHealth();

                player.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);

                eventPlayer.IsDead = false;

                eventPlayer.AddToNetwork();
            }
        }

        internal static void RespawnPlayer(BaseEventPlayer eventPlayer)
        {
            if (!eventPlayer.IsDead)
                return;

            eventPlayer.DestroyUI(ArenaUI.UI_DEATH);
            eventPlayer.DestroyUI(ArenaUI.UI_RESPAWN);
            eventPlayer.DestroyUI(ArenaUI.UI_CLASS_SELECT);
            eventPlayer.DestroyUI(ArenaUI.UI_TEAM_SELECT);
            eventPlayer.DestroyUI(ArenaUI.UI_SPECTATE);

            ResetPlayer(eventPlayer.Player);

            eventPlayer.Event.OnPlayerRespawn(eventPlayer);
        }

        internal static string StripTags(string str)
        {
            if (str.StartsWith("[") && str.Contains("]") && str.Length > str.IndexOf("]"))
                str = str.Substring(str.IndexOf("]") + 1).Trim();

            if (str.StartsWith("[") && str.Contains("]") && str.Length > str.IndexOf("]"))
                StripTags(str);

            return str;
        }

        internal static string TrimToSize(string str, int size = 18)
        {
            if (str.Length > size)
                str = str.Substring(0, size);
            return str;
        }

        private object IsEventPlayer(BasePlayer player) => player.GetComponent<BaseEventPlayer>() != null ? (object)true : null;

        private object IsEventPlayerDead(BasePlayer player)
        {
            BaseEventPlayer eventPlayer = player.GetComponent<BaseEventPlayer>();
            if (eventPlayer != null)
                return eventPlayer.IsDead;
            return false;
        }

        private object isEventPlayer(BasePlayer player) => IsEventPlayer(player);
        #endregion

        #region Zone Management
        private void OnExitZone(string zoneId, BasePlayer player)
        {
            if (player == null)
                return;

            BaseEventPlayer eventPlayer = GetUser(player);

            if (!string.IsNullOrEmpty(Configuration.Lobby.LobbyZoneID) && zoneId == Configuration.Lobby.LobbyZoneID)
            {
                if (eventPlayer == null && Lobby.IsEnabled && Configuration.Lobby.KeepPlayersInLobby)
                {
                    if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION))
                        return;

                    player.ChatMessage(Message("Lobby.LeftZone", player.userID));
                    Lobby.ReturnToLobby(player);
                    return;
                }
            }

            if (eventPlayer == null || eventPlayer.IsDead)
                return;

            if (zoneId == eventPlayer.Event.Config.ZoneID)
                eventPlayer.IsOutOfBounds = true;
        }

        private void OnEnterZone(string zoneId, BasePlayer player)
        {
            BaseEventPlayer eventPlayer = GetUser(player);

            if (eventPlayer == null || eventPlayer.IsDead)
                return;

            if (zoneId == eventPlayer.Event.Config.ZoneID)
                eventPlayer.IsOutOfBounds = false;
        }

        private static void SetZoneOccupied(BaseEventGame baseEventGame, bool isOccupied)
        {
            if (string.IsNullOrEmpty(baseEventGame.Config.ZoneID))
                return;

            if (isOccupied)
                Instance.ZoneManager.Call("AddFlag", baseEventGame.Config.ZoneID, "Eject");
            else Instance.ZoneManager.Call("RemoveFlag", baseEventGame.Config.ZoneID, "Eject");
        }
        #endregion

        #region Rotating Pickups
        private static void RotateDroppedItem(WorldItem worldItem)
        {
            if (Instance.RotatingPickups && Configuration.Event.UseRotator)
            {
                if (worldItem != null)
                    Instance.RotatingPickups.Call("AddItemRotator", worldItem, true);
            }
        }
        #endregion

        #region File Validation
        internal object ValidateEventConfig(EventConfig eventConfig)
        {
            IEventPlugin plugin;

            if (string.IsNullOrEmpty(eventConfig.EventType) || !EventModes.TryGetValue(eventConfig.EventType, out plugin))
                return string.Concat("Event mode ", eventConfig.EventType, " is not currently loaded");

            if (!plugin.CanUseClassSelector && eventConfig.TeamConfigA.Kits.Count == 0)
                return "You must set atleast 1 kit";

            if (eventConfig.MinimumPlayers == 0)
                return "You must set the minimum players";

            if (eventConfig.MaximumPlayers == 0)
                return "You must set the maximum players";

            if (plugin.RequireTimeLimit && eventConfig.TimeLimit == 0)
                return "You must set a time limit";

            if (plugin.RequireScoreLimit && eventConfig.ScoreLimit == 0)
                return "You must set a score limit";

            object success;

            foreach (string kit in eventConfig.TeamConfigA.Kits)
            {
                success = ValidateKit(kit);
                if (success is string)
                    return $"Invalid kit: {kit}";
            }

            success = ValidateSpawnFile(eventConfig.TeamConfigA.Spawnfile);
            if (success is string)
                return $"Invalid spawn file: {eventConfig.TeamConfigA.Spawnfile}";

            if (plugin.IsTeamEvent)
            {
                success = ValidateSpawnFile(eventConfig.TeamConfigB.Spawnfile);
                if (success is string)
                    return $"Invalid second spawn file: {eventConfig.TeamConfigB.Spawnfile}";

                if (eventConfig.TeamConfigB.Kits.Count == 0)
                    return "You must set atleast 1 kit for Team B";

                foreach (string kit in eventConfig.TeamConfigB.Kits)
                {
                    success = ValidateKit(kit);
                    if (success is string)
                        return $"Invalid kit: {kit}";
                }
            }

            success = ValidateZoneID(eventConfig.ZoneID);
            if (success is string)
                return $"Invalid zone ID: {eventConfig.ZoneID}";

            for (int i = 0; i < plugin.AdditionalParameters?.Count; i++)
            {
                EventParameter eventParameter = plugin.AdditionalParameters[i];

                if (eventParameter.IsRequired)
                {
                    object value;
                    eventConfig.AdditionalParams.TryGetValue(eventParameter.Field, out value);

                    if (value == null)
                        return $"Missing event parameter: ({eventParameter.DataType}){eventParameter.Field}";
                    else
                    {
                        success = plugin.ParameterIsValid(eventParameter.Field, value);
                        if (success is string)
                            return (string)success;
                    }
                }
            }

            return null;
        }

        internal object ValidateSpawnFile(string name)
        {
            object success = Spawns?.Call("GetSpawnsCount", name);
            if (success is string)
                return (string)success;
            return null;
        }

        internal object ValidateZoneID(string name)
        {
            object success = ZoneManager?.Call("CheckZoneID", name);
            if (name is string && !string.IsNullOrEmpty((string)name))
                return null;
            return $"Zone \"{name}\" does not exist!";
        }

        internal object ValidateKit(string name)
        {
            object success = Kits?.Call("IsKit", name);
            if ((success is bool))
            {
                if (!(bool)success)
                    return $"Kit \"{name}\" does not exist!";
            }
            return null;
        }
        #endregion

        #region Scoring
        public struct ScoreEntry
        {
            internal int position;
            internal string displayName;
            internal float value1;
            internal float value2;
            internal Team team;

            internal ScoreEntry(BaseEventPlayer eventPlayer, int position, float value1, float value2)
            {
                this.position = position;
                this.displayName = StripTags(eventPlayer.Player.displayName);
                this.team = eventPlayer.Team;
                this.value1 = value1;
                this.value2 = value2;
            }

            internal ScoreEntry(BaseEventPlayer eventPlayer, float value1, float value2)
            {
                this.position = 0;
                this.displayName = StripTags(eventPlayer.Player.displayName);
                this.team = eventPlayer.Team;
                this.value1 = value1;
                this.value2 = value2;
            }

            internal ScoreEntry(float value1, float value2)
            {
                this.position = 0;
                this.displayName = string.Empty;
                this.team = Team.None;
                this.value1 = value1;
                this.value2 = value2;
            }
        }

        public class EventResults
        {
            public string EventName { get; private set; }

            public string EventType { get; private set; }

            public ScoreEntry TeamScore { get; private set; }

            public IEventPlugin Plugin { get; private set; }

            public List<ScoreEntry> Scores { get; private set; } = new List<ScoreEntry>();

            public bool IsValid => Plugin != null;

            public void UpdateFromEvent(BaseEventGame baseEventGame)
            {
                EventName = baseEventGame.Config.EventName;
                EventType = baseEventGame.Config.EventType;
                Plugin = baseEventGame.Plugin;

                if (Plugin.IsTeamEvent)
                    TeamScore = new ScoreEntry(baseEventGame.GetTeamScore(Team.A), baseEventGame.GetTeamScore(Team.B));
                else TeamScore = default(ScoreEntry);

                Scores.Clear();

                if (baseEventGame.scoreData.Count > 0)
                    Scores.AddRange(baseEventGame.scoreData);
            }
        }
        #endregion

        #region Lobby TP
        [ChatCommand("lobby")]
        private void cmdLobbyTP(BasePlayer player, string command, string[] args)
        {
            if (!Lobby.IsEnabled)
                return;

            Lobby.TeleportToLobbyCommand(player);
        }

        [ChatCommand("lobbyc")]
        private void cmdLobbyCancel(BasePlayer player, string command, string[] args)
        {
            if (!Lobby.IsEnabled)
                return;

            Lobby.CancelLobbyTeleportCommand(player);
        }

        private class LobbyHandler
        {
            private SpawnSelector _spawns;

            private Hash<ulong, Action> _pendingLobbyTP = new Hash<ulong, Action>();

            private Hash<ulong, double> _cooldownLobbyTP = new Hash<ulong, double>();

            internal bool IsEnabled { get; private set; }

            internal bool ForceLobbyRespawn => IsEnabled && Configuration.Lobby.ForceLobbyRespawn;

            internal LobbyHandler(string spawnFile)
            {
                if (string.IsNullOrEmpty(spawnFile))
                    return;

                _spawns = new SpawnSelector(spawnFile);
                if (_spawns.Count == 0)
                    return;

                IsEnabled = true;
            }

            internal void SendRespawnOptions(BasePlayer player)
            {
                if (player == null || player.IsNpc || !player.IsConnected)
                    return;

                using (ProtoBuf.RespawnInformation respawnInformation = Pool.Get<ProtoBuf.RespawnInformation>())
                {
                    respawnInformation.spawnOptions = Pool.Get<List<ProtoBuf.RespawnInformation.SpawnOptions>>();

                    ProtoBuf.RespawnInformation.SpawnOptions d = Pool.Get<ProtoBuf.RespawnInformation.SpawnOptions>();
                    d.id = LOBBY_BAG_ID;
                    d.name = Message("Lobby.RespawnButton", player.userID);
                    d.worldPosition = _spawns.GetSpawnPoint();
                    d.type = ProtoBuf.RespawnInformation.SpawnOptions.RespawnType.Bed;
                    d.unlockSeconds = 0;

                    respawnInformation.spawnOptions.Add(d);

                    respawnInformation.previousLife = player.previousLifeStory;
                    respawnInformation.fadeIn = (player.previousLifeStory == null ? false : player.previousLifeStory.timeDied > (Facepunch.Math.Epoch.Current - 5));

                    player.ClientRPCPlayer<ProtoBuf.RespawnInformation>(null, player, "OnRespawnInformation", respawnInformation);
                }
            }

            internal void RespawnAtLobby(BasePlayer player)
            {
                player.RespawnAt(_spawns.GetSpawnPoint(), Quaternion.identity);

                if (!Configuration.Server.RestorePlayers && !string.IsNullOrEmpty(Configuration.Lobby.LobbyKit))
                    GiveKit(player, Configuration.Lobby.LobbyKit);
            }

            internal void ReturnToLobby(BasePlayer player) => TeleportPlayer(player, _spawns.GetSpawnPoint(), false);

            internal void TeleportToLobbyCommand(BasePlayer player)
            {
                if (!Configuration.Lobby.TP.AllowLobbyTP)
                    return;

                if (GetUser(player) != null)
                {
                    player.ChatMessage(Message("Lobby.InEvent", player.userID));
                    return;
                }

                if (_pendingLobbyTP.ContainsKey(player.userID))
                {
                    player.ChatMessage(Message("Lobby.IsPending", player.userID));
                    return;
                }

                double time;
                if (_cooldownLobbyTP.TryGetValue(player.userID, out time))
                {
                    if (time > Time.realtimeSinceStartup)
                    {
                        player.ChatMessage(string.Format(Message("Lobby.OnCooldown", player.userID), Mathf.RoundToInt((float)time - Time.realtimeSinceStartup)));
                        return;
                    }
                }

                string str = MeetsLobbyTPRequirements(player);
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return;
                }

                if (Configuration.Lobby.TP.Timer == 0)
                {
                    TeleportToLobby(player);
                    return;
                }
                else
                {
                    Action action = new Action(() => TryTeleportToLobby(player));
                    player.Invoke(action, Configuration.Lobby.TP.Timer);
                    _pendingLobbyTP[player.userID] = action;
                    player.ChatMessage(string.Format(Message("Lobby.TPConfirmed", player.userID), Configuration.Lobby.TP.Timer));
                }
            }

            internal void CancelLobbyTeleportCommand(BasePlayer player)
            {
                if (!Configuration.Lobby.TP.AllowLobbyTP)
                    return;

                if (!HasPendingTeleports(player))
                {
                    player.ChatMessage(Message("Lobby.NoTPPending", player.userID));
                    return;
                }

                CancelPendingTeleports(player);
                player.ChatMessage(Message("Lobby.TPCancelled", player.userID));
            }

            private void TryTeleportToLobby(BasePlayer player)
            {
                _pendingLobbyTP.Remove(player.userID);

                string str = MeetsLobbyTPRequirements(player);
                if (!string.IsNullOrEmpty(str))
                {
                    player.ChatMessage(str);
                    return;
                }

                TeleportToLobby(player);

                if (Configuration.Lobby.TP.Cooldown > 0)
                    _cooldownLobbyTP[player.userID] = Time.realtimeSinceStartup + Configuration.Lobby.TP.Cooldown;
            }

            public void TeleportToLobby(BasePlayer player)
            {
                if (player == null)
                    return;

                BaseEventPlayer eventPlayer = GetUser(player);
                if (eventPlayer)
                    UnityEngine.Object.DestroyImmediate(eventPlayer);

                CancelPendingTeleports(player);

                ResetMetabolism(player);

                TeleportPlayer(player, _spawns.GetSpawnPoint(), true);

                if (!Configuration.Server.RestorePlayers && !string.IsNullOrEmpty(Configuration.Lobby.LobbyKit))
                    GiveKit(player, Configuration.Lobby.LobbyKit);
            }

            private bool HasPendingTeleports(BasePlayer player) => _pendingLobbyTP.ContainsKey(player.userID);

            private void CancelPendingTeleports(BasePlayer player)
            {
                if (player == null)
                    return;

                if (HasPendingTeleports(player))
                {
                    player.CancelInvoke(_pendingLobbyTP[player.userID]);
                    _pendingLobbyTP.Remove(player.userID);
                }
            }

            private string MeetsLobbyTPRequirements(BasePlayer player)
            {
                if (!string.IsNullOrEmpty(Configuration.Lobby.LobbyZoneID) && (bool)Instance.ZoneManager.Call("isPlayerInZone", Configuration.Lobby.LobbyZoneID, player))
                    return Message("Lobby.AlreadyThere", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromBuildBlock && !player.CanBuild())
                    return Message("Lobby.BuildBlocked", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromCargoShip && player.GetParentEntity() is CargoShip)
                    return Message("Lobby.Prevent.CargoShip", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromHotAirBalloon && player.GetParentEntity() is HotAirBalloon)
                    return Message("Lobby.Prevent.HAB", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromMounted && player.isMounted)
                    return Message("Lobby.Prevent.Mounted", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportFromOilRig && IsNearOilRig(player))
                    return Message("Lobby.Prevent.OilRig", player.userID);

                if (!Configuration.Lobby.TP.AllowTeleportWhilstBleeding && player.metabolism.bleeding.value > 0)
                    return Message("Lobby.Prevent.Bleeding", player.userID);

                if (IsRaidBlocked(player))
                    return Message("Lobby.Prevent.RaidBlocked", player.userID);

                if (IsCombatBlocked(player))
                    return Message("Lobby.Prevent.CombatBlocked", player.userID);

                string str = Interface.Oxide.CallHook("CanTeleport", player) as string;
                if (str != null)
                    return str;

                return string.Empty;
            }

            private bool IsNearOilRig(BasePlayer player)
            {
                for (int i = 0; i < TerrainMeta.Path.Monuments.Count; i++)
                {
                    MonumentInfo monumentInfo = TerrainMeta.Path.Monuments[i];

                    if (monumentInfo.gameObject.name.Contains("oilrig"))
                    {
                        if (Vector3Ex.Distance2D(player.transform.position, monumentInfo.transform.position) <= 100f)
                            return true;
                    }
                }

                return false;
            }

            private bool IsRaidBlocked(BasePlayer player)
            {
                if (Instance.NoEscape)
                {
                    if (Configuration.Lobby.TP.AllowTeleportWhilstRaidBlocked)
                    {
                        bool success = Instance.NoEscape.Call<bool>("IsRaidBlocked", player);
                        if (success)
                            return true;
                    }
                }
                return false;
            }

            private bool IsCombatBlocked(BasePlayer player)
            {
                if (Instance.NoEscape)
                {
                    if (Configuration.Lobby.TP.AllowTeleportWhilstCombatBlocked)
                    {
                        bool success = Instance.NoEscape.Call<bool>("IsCombatBlocked", player);
                        if (success)
                            return true;
                    }
                }
                return false;
            }
        }
        #endregion

        #region Create Teleporters
        [ChatCommand("teleporter")]
        private void cmdArenaTeleporter(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION))
                return;

            if (args.Length == 0)
            {
                SendReply(player, "<color=#ce422b>/teleporter add \"event name\"</color> - Create a arena teleporter for the specified event on your position");
                SendReply(player, "<color=#ce422b>/teleporter remove \"event name\"</color> - Remove the arena teleporter for the specified event");
                return;
            }

            switch (args[0].ToLower())
            {
                case "add":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter add \"event name\"</color>");
                            return;
                        }

                        string eventName = args[1];
                        if (!Events.events.ContainsKey(eventName))
                        {
                            player.ChatMessage($"Unable to find an event with the name {eventName}");
                            return;
                        }

                        if (Events.events[eventName].Teleporter != null)
                        {
                            player.ChatMessage($"This event already has a teleporter. You need to remove it before continuing");
                            return;
                        }

                        EventConfig.SerializedTeleporter serializedTeleporter = new EventConfig.SerializedTeleporter(player.transform.position + (Vector3.up * 1.5f));
                        Events.events[eventName].Teleporter = serializedTeleporter;

                        SaveEventData();

                        if (!Configuration.Lobby.LobbyEnabled || string.IsNullOrEmpty(Configuration.Lobby.LobbySpawnfile))
                        {
                            player.ChatMessage($"You have successfully setup a teleporter for event {eventName}, however teleporters require the lobby to be setup and enabled");
                            return;
                        }

                        if (ActiveEvents.ContainsKey(eventName))
                        {
                            ActiveEvents[eventName].CreateTeleporter(serializedTeleporter);
                            player.ChatMessage($"You have successfully setup a teleporter for event {eventName}");
                            return;
                        }

                        player.ChatMessage($"You have successfully setup a teleporter for event {eventName}, however the event is not currently running so it hasn't been created");
                    }
                    return;
                case "remove":
                    {
                        if (args.Length < 2)
                        {
                            player.ChatMessage("Invalid syntax! <color=#ce422b>/arenateleporter remove \"event name\"</color>");
                            return;
                        }

                        string eventName = args[1];
                        if (!Events.events.ContainsKey(eventName))
                        {
                            player.ChatMessage($"Unable to find an event with the name {eventName}");
                            return;
                        }

                        if (Events.events[eventName].Teleporter == null)
                        {
                            player.ChatMessage($"This event does not have a teleporter");
                            return;
                        }

                        Events.events[eventName].Teleporter = null;

                        SaveEventData();

                        if (ActiveEvents.ContainsKey(eventName))
                            ActiveEvents[eventName].DestroyTeleporter();

                        player.ChatMessage($"You have removed the teleporter for event {eventName}");
                    }
                    return;
                default:
                    player.ChatMessage("Invalid syntax");
                    break;
            }
        }
        #endregion

        #region Event Enable/Disable
        [ConsoleCommand("arena.enable")]
        private void ccmdEnableEvent(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (arg.Args == null || arg.Args.Length == 0)
            {
                SendReply(arg, "arena.enable <eventname> - Enable a previously disabled event");
                return;
            }

            string eventName = arg.Args[0];

            if (!Events.events.ContainsKey(eventName))
            {
                SendReply(arg, "Invalid event name entered");
                return;
            }

            if (ActiveEvents.ContainsKey(eventName))
            {
                SendReply(arg, "This event is already running");
                return;
            }

            if (!Events.events[eventName].IsDisabled)
            {
                SendReply(arg, "This event is not disabled");
                return;
            }

            Events.events[eventName].IsDisabled = false;
            SaveEventData();

            OpenEvent(eventName);

            SendReply(arg, $"{eventName} has been enabled");
        }

        [ConsoleCommand("arena.disable")]
        private void ccmdDisableEvent(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (arg.Args == null || arg.Args.Length == 0)
            {
                SendReply(arg, "arena.disable <eventname> - Disable a event");
                return;
            }

            string eventName = arg.Args[0];

            if (!Events.events.ContainsKey(eventName))
            {
                SendReply(arg, "Invalid event name entered");
                return;
            }

            if (Events.events[eventName].IsDisabled)
            {
                SendReply(arg, "This event is already disabled");
                return;
            }

            Events.events[eventName].IsDisabled = true;
            SaveEventData();

            ShutdownEvent(eventName);

            SendReply(arg, $"{eventName} has been disabled");
        }
        #endregion

        #region Config
        public class ConfigData
        {
            [JsonProperty(PropertyName = "Server Settings")]
            public ServerOptions Server { get; set; }

            [JsonProperty(PropertyName = "Event Options")]
            public EventOptions Event { get; set; }

            [JsonProperty(PropertyName = "Lobby Options")]
            public LobbyOptions Lobby { get; set; }

            [JsonProperty(PropertyName = "Timer Options")]
            public TimerOptions Timer { get; set; }

            [JsonProperty(PropertyName = "Message Options")]
            public MessageOptions Message { get; set; }

            public class ServerOptions
            {
                [JsonProperty(PropertyName = "Restore players when they leave an event")]
                public bool RestorePlayers { get; set; }

                [JsonProperty(PropertyName = "Disable server events (Patrol Helicopter, Cargo Ship, Airdrops etc)")]
                public bool DisableServerEvents { get; set; }

                [JsonProperty(PropertyName = "Use inbuilt chat manager")]
                public bool UseChat { get; set; }
            }

            public class EventOptions
            {
                [JsonProperty(PropertyName = "Create and add players to Rusts team system for team based events")]
                public bool AddToTeams { get; set; }

                [JsonProperty(PropertyName = "Drop on death (Nothing, Ammo, Backpack, Corpse, Weapon)")]
                public string DropOnDeath { get; set; }

                [JsonProperty(PropertyName = "Add rotator to dropped items (Requires RotatingPickups)")]
                public bool UseRotator { get; set; }

                [JsonProperty(PropertyName = "Invincibility time after respawn (seconds)")]
                public float InvincibilityTime { get; set; }

                [JsonProperty(PropertyName = "Blacklisted commands for event players")]
                public string[] CommandBlacklist { get; set; }

                [JsonProperty(PropertyName = "Restart the event when it finishes")]
                public bool StartOnFinish { get; set; }

                [JsonProperty(PropertyName = "Only send player to the arena when the event starts")]
                public bool SendToArenaOnStart { get; set; }

                [JsonProperty(PropertyName = "Automatic Health Restore Options")]
                public HealthRestore Health { get; set; }

                public class HealthRestore
                {
                    [JsonProperty(PropertyName = "Enable automatic health restoration")]
                    public bool Enabled { get; set; }

                    [JsonProperty(PropertyName = "Start restoring if no damage taken for x seconds")]
                    public float RestoreAfter { get; set; }

                    [JsonProperty(PropertyName = "Amount of health to restore every second")]
                    public float Amount { get; set; }
                }
            }

            public class LobbyOptions
            {
                [JsonProperty(PropertyName = "Force event access from a physical lobby")]
                public bool LobbyEnabled { get; set; }

                [JsonProperty(PropertyName = "Force all respawns to be in the lobby (Event only servers! Do not enable on a server with regular gameplay)")]
                public bool ForceLobbyRespawn { get; set; }

                [JsonProperty(PropertyName = "Lobby spawnfile")]
                public string LobbySpawnfile { get; set; }

                [JsonProperty(PropertyName = "Lobby zone ID")]
                public string LobbyZoneID { get; set; }

                [JsonProperty(PropertyName = "Keep players in the lobby zone (Event only servers! Do not enable on a server with regular gameplay)")]
                public bool KeepPlayersInLobby { get; set; }

                [JsonProperty(PropertyName = "Lobby kit (only applies if event only server)")]
                public string LobbyKit { get; set; }

                [JsonProperty(PropertyName = "Lobby teleportation options")]
                public Teleportation TP { get; set; }

                public class Teleportation
                {
                    [JsonProperty(PropertyName = "Allow teleportation to the lobby (Requires a lobby spawn file)")]
                    public bool AllowLobbyTP { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if Raid Blocked (NoEscape)")]
                    public bool AllowTeleportWhilstRaidBlocked { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if Combat Blocked (NoEscape)")]
                    public bool AllowTeleportWhilstCombatBlocked { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if bleeding")]
                    public bool AllowTeleportWhilstBleeding { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if building blocked")]
                    public bool AllowTeleportFromBuildBlock { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if on the CargoShip")]
                    public bool AllowTeleportFromCargoShip { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if in a HotAirBalloon")]
                    public bool AllowTeleportFromHotAirBalloon { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if on the OilRig")]
                    public bool AllowTeleportFromOilRig { get; set; }

                    [JsonProperty(PropertyName = "Allow teleportation if mounted")]
                    public bool AllowTeleportFromMounted { get; set; }

                    [JsonProperty(PropertyName = "Teleportation countdown timer")]
                    public int Timer { get; set; }

                    [JsonProperty(PropertyName = "Teleportation cooldown timer")]
                    public int Cooldown { get; set; }
                }
            }

            public class TimerOptions
            {
                [JsonProperty(PropertyName = "Match pre-start timer (seconds)")]
                public int Prestart { get; set; }

                [JsonProperty(PropertyName = "Round interval timer (seconds)")]
                public int RoundInterval { get; set; }

                [JsonProperty(PropertyName = "Backpack/corpse despawn timer (seconds)")]
                public int Corpse { get; set; }
            }

            public class MessageOptions
            {
                [JsonProperty(PropertyName = "Broadcast when a player joins an event to event players")]
                public bool BroadcastJoiners { get; set; }

                [JsonProperty(PropertyName = "Broadcast when a player leaves an event to event players")]
                public bool BroadcastLeavers { get; set; }

                [JsonProperty(PropertyName = "Broadcast the name(s) of the winning player(s) to chat")]
                public bool BroadcastWinners { get; set; }

                [JsonProperty(PropertyName = "Broadcast kills to event players")]
                public bool BroadcastKills { get; set; }

                [JsonProperty(PropertyName = "Chat icon Steam ID")]
                public ulong ChatIcon { get; set; }
            }


            public Oxide.Core.VersionNumber Version { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = Config.ReadObject<ConfigData>();

            if (Configuration.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(Configuration, true);
        }

        protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                Server = new ConfigData.ServerOptions
                {
                    RestorePlayers = true,
                    DisableServerEvents = false,
                    UseChat = false
                },
                Event = new ConfigData.EventOptions
                {
                    AddToTeams = true,
                    DropOnDeath = "Backpack",
                    Health = new ConfigData.EventOptions.HealthRestore
                    {
                        Enabled = false,
                        RestoreAfter = 5f,
                        Amount = 2.5f
                    },
                    InvincibilityTime = 3f,
                    UseRotator = false,
                    StartOnFinish = false,
                    CommandBlacklist = new string[] { "s", "tp" },
                    SendToArenaOnStart = false,
                },
                Lobby = new ConfigData.LobbyOptions
                {
                    LobbyEnabled = false,
                    ForceLobbyRespawn = false,
                    LobbyKit = string.Empty,
                    LobbySpawnfile = string.Empty,
                    LobbyZoneID = string.Empty,
                    TP = new ConfigData.LobbyOptions.Teleportation
                    {
                        AllowLobbyTP = false,
                        AllowTeleportFromBuildBlock = false,
                        AllowTeleportFromCargoShip = false,
                        AllowTeleportFromHotAirBalloon = false,
                        AllowTeleportFromMounted = false,
                        AllowTeleportFromOilRig = false,
                        AllowTeleportWhilstBleeding = false,
                        AllowTeleportWhilstCombatBlocked = false,
                        AllowTeleportWhilstRaidBlocked = false,
                        Cooldown = 60,
                        Timer = 10
                    }
                },
                Message = new ConfigData.MessageOptions
                {
                    BroadcastJoiners = true,
                    BroadcastLeavers = true,
                    BroadcastWinners = true,
                    BroadcastKills = true,
                    ChatIcon = 76561198403299915
                },
                Timer = new ConfigData.TimerOptions
                {
                    Prestart = 10,
                    RoundInterval = 10,
                    Corpse = 30
                },
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            if (Configuration.Version < new VersionNumber(2, 0, 3))
            {
                Configuration.Event.SendToArenaOnStart = false;
            }

            Configuration.Version = Version;

            PrintWarning("Config update completed!");
        }
        #endregion

        #region Data Management
        private DynamicConfigFile restorationData, eventData;

        internal void SaveEventData() => eventData.WriteObject(Events);

        private void SaveRestoreData() => restorationData.WriteObject(Restore);

        private void LoadData()
        {
            restorationData = Interface.Oxide.DataFileSystem.GetFile("Arena/restoration_data");

            eventData = Interface.Oxide.DataFileSystem.GetFile("Arena/event_data");

            Events = eventData.ReadObject<EventData>();
            if (Events == null)
                Events = new EventData();

            Restore = restorationData.ReadObject<RestoreData>();
            if (Restore?.Restore == null)
                Restore = new RestoreData();
        }

        public static void SaveEventConfig(EventConfig eventConfig)
        {
            ShutdownEvent(eventConfig.EventName);

            Instance.Events.events[eventConfig.EventName] = eventConfig;
            Instance.SaveEventData();

            object success = Instance.OpenEvent(eventConfig.EventName);
            if (success is string)
                Debug.LogWarning($"[Arena] - {(string)success}");
        }
        #endregion

        public class EventData
        {
            public Hash<string, EventConfig> events = new Hash<string, EventConfig>();
        }

        public class EventParameter
        {
            public string Name; // The name shown in the UI
            public InputType Input; // The type of input used to select the value in the UI

            public string Field; // The name of the custom field stored in the event config
            public string DataType; // The type of the field (string, int, float, bool, List<string>)

            public bool IsRequired; // Is this field required to complete event creation?

            public string SelectorHook; // The hook that is called to gather the options that can be selected. This should return a string[] (ex. GetZoneIDs from ZoneManager, GetAllKits from Kits)
            public bool SelectMultiple; // Allows the user to select multiple elements when using the selector

            public object DefaultValue; // Set the default value for this field

            [JsonIgnore]
            public bool IsList => Input == InputType.Selector && DataType.Equals("List<string>", StringComparison.OrdinalIgnoreCase);

            public enum InputType { InputField, Toggle, Selector }
        }

        #region Player Restoration
        public class RestoreData
        {
            public Hash<ulong, PlayerData> Restore = new Hash<ulong, PlayerData>();

            internal void AddData(BasePlayer player)
            {
                Restore[player.userID] = new PlayerData(player);
            }

            public void AddPrizeToData(ulong playerId, int itemId, int amount)
            {
                PlayerData playerData;
                if (Restore.TryGetValue(playerId, out playerData))
                {
                    ItemData itemData = FindItem(playerData, itemId);
                    if (itemData != null)
                        itemData.amount += amount;
                    else
                    {
                        Array.Resize<ItemData>(ref playerData.containerMain, playerData.containerMain.Length + 1);
                        playerData.containerMain[playerData.containerMain.Length - 1] = new ItemData() { amount = amount, condition = 100, contents = new ItemData[0], itemid = itemId, position = -1, skin = 0UL };
                    }
                }
            }

            private ItemData FindItem(PlayerData playerData, int itemId)
            {
                for (int i = 0; i < playerData.containerMain.Length; i++)
                {
                    ItemData itemData = playerData.containerMain[i];
                    if (itemData.itemid.Equals(itemId))
                        return itemData;
                }

                for (int i = 0; i < playerData.containerBelt.Length; i++)
                {
                    ItemData itemData = playerData.containerBelt[i];
                    if (itemData.itemid.Equals(itemId))
                        return itemData;
                }

                return null;
            }

            internal bool HasRestoreData(ulong playerId) => Restore.ContainsKey(playerId);

            internal void RestorePlayer(BasePlayer player)
            {
                if (player == null || player.IsDead())
                    return;

                PlayerData playerData;
                if (Restore.TryGetValue(player.userID, out playerData))
                {
                    StripInventory(player);

                    player.metabolism.Reset();

                    if (player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
                    {
                        Instance.timer.Once(1, () => RestorePlayer(player));
                        return;
                    }

                    if (player.currentTeam != 0UL && player.currentTeam != playerData.teamId)
                    {
                        RelationshipManager.PlayerTeam currentTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                        if (currentTeam != null)
                        {
                            currentTeam.members.Remove(player.userID);
                            RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);

                            player.ClearTeam();
                            player.BroadcastAppTeamRemoval();

                            if (currentTeam.teamLeader == player.userID)
                            {
                                if (currentTeam.members.Count <= 0)
                                    currentTeam.teamLeader = 0UL;
                                else currentTeam.teamLeader = currentTeam.members[0];
                            }
                            currentTeam.MarkDirty();
                        }

                        RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance.FindTeam(playerData.teamId);
                        if (playerTeam == null)
                        {
                            playerTeam = RelationshipManager.ServerInstance.CreateTeam();
                            playerTeam.invites.Clear();
                            playerTeam.members.Clear();
                            playerTeam.onlineMemberConnections.Clear();
                        }

                        player.currentTeam = playerTeam.teamID;
                        playerTeam.members.Add(player.userID);

                        RelationshipManager.ServerInstance.playerToTeam.Remove(player.userID);
                        RelationshipManager.ServerInstance.playerToTeam.Add(player.userID, playerTeam);

                        playerTeam.MarkDirty();
                    }

                    playerData.RestorePlayerMetabolism(player);

                    if (Configuration.Lobby.LobbyEnabled && !string.IsNullOrEmpty(Configuration.Lobby.LobbySpawnfile))
                        Instance.TeleportPlayer(player, Configuration.Lobby.LobbySpawnfile, true);
                    else TeleportPlayer(player, playerData.position, true);

                    RestoreItems(player, playerData.containerBelt, Container.Belt);
                    RestoreItems(player, playerData.containerWear, Container.Wear);
                    RestoreItems(player, playerData.containerMain, Container.Main);

                    Restore.Remove(player.userID);

                    player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }

            private void RestoreItems(BasePlayer player, ItemData[] itemData, Container type)
            {
                ItemContainer container = type == Container.Belt ? player.inventory.containerBelt : type == Container.Wear ? player.inventory.containerWear : player.inventory.containerMain;

                for (int i = 0; i < itemData.Length; i++)
                {
                    ItemData data = itemData[i];
                    if (data.amount < 1)
                        continue;

                    Item item = data.Create();

                    if (data.position == -1)
                    {
                        if (!item.MoveToContainer(container))
                            item.Drop(player.transform.position, Vector3.zero);
                    }
                    else item.SetParent(container);
                }
            }

            public class PlayerData
            {
                public float[] stats;
                public Vector3 position;
                public ulong teamId;

                public ItemData[] containerMain;
                public ItemData[] containerWear;
                public ItemData[] containerBelt;

                public PlayerData() { }

                public PlayerData(BasePlayer player)
                {
                    stats = GetPlayerMetabolism(player);
                    position = player.transform.position;
                    teamId = player.currentTeam;

                    containerBelt = GetItems(player.inventory.containerBelt).ToArray();
                    containerMain = GetItems(player.inventory.containerMain).ToArray();
                    containerWear = GetItems(player.inventory.containerWear).ToArray();
                }

                private IEnumerable<ItemData> GetItems(ItemContainer container)
                {
                    return container.itemList.Select(item => SerializeItem(item));
                }

                private float[] GetPlayerMetabolism(BasePlayer player) => new float[] { player.health, player.metabolism.hydration.value, player.metabolism.calories.value };

                internal void RestorePlayerMetabolism(BasePlayer player)
                {
                    player.health = stats[0];
                    player.metabolism.hydration.value = stats[1];
                    player.metabolism.calories.value = stats[2];
                    player.metabolism.SendChangesToClient();
                }
            }
            private enum Container { Belt, Main, Wear }
        }
        #endregion

        #region Serialized Items
        internal static ItemData SerializeItem(Item item)
        {
            return new ItemData
            {
                itemid = item.info.itemid,
                amount = item.amount,
                ammo = item.GetHeldEntity() is BaseProjectile ? (item.GetHeldEntity() as BaseProjectile).primaryMagazine.contents :
                       item.GetHeldEntity() is FlameThrower ? (item.GetHeldEntity() as FlameThrower).ammo :
                       item.GetHeldEntity() is Chainsaw ? (item.GetHeldEntity() as Chainsaw).ammo : 0,
                ammotype = (item.GetHeldEntity() as BaseProjectile)?.primaryMagazine.ammoType.shortname ?? null,
                position = item.position,
                skin = item.skin,
                condition = item.condition,
                maxCondition = item.maxCondition,
                frequency = ItemModAssociatedEntity<PagerEntity>.GetAssociatedEntity(item)?.GetFrequency() ?? -1,
                instanceData = new ItemData.InstanceData(item),
                contents = item.contents?.itemList.Select(item1 => new ItemData
                {
                    itemid = item1.info.itemid,
                    amount = item1.amount,
                    condition = item1.condition
                }).ToArray()
            };
        }

        public class ItemData
        {
            public int itemid;
            public ulong skin;
            public int amount;
            public float condition;
            public float maxCondition;
            public int ammo;
            public string ammotype;
            public int position;
            public int frequency;
            public InstanceData instanceData;
            public ItemData[] contents;

            public Item Create()
            {
                Item item = ItemManager.CreateByItemID(itemid, amount, skin);
                item.condition = condition;
                item.maxCondition = maxCondition;

                if (frequency > 0)
                {
                    ItemModRFListener rfListener = item.info.GetComponentInChildren<ItemModRFListener>();
                    if (rfListener != null)
                    {
                        PagerEntity pagerEntity = BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) as PagerEntity;
                        if (pagerEntity != null)
                            pagerEntity.ChangeFrequency(frequency);
                    }
                }

                if (instanceData?.IsValid() ?? false)
                    instanceData.Restore(item);

                BaseProjectile weapon = item.GetHeldEntity() as BaseProjectile;
                if (weapon != null)
                {
                    if (!string.IsNullOrEmpty(ammotype))
                        weapon.primaryMagazine.ammoType = ItemManager.FindItemDefinition(ammotype);
                    weapon.primaryMagazine.contents = ammo;
                }

                FlameThrower flameThrower = item.GetHeldEntity() as FlameThrower;
                if (flameThrower != null)
                    flameThrower.ammo = ammo;

                Chainsaw chainsaw = item.GetHeldEntity() as Chainsaw;
                if (chainsaw != null)
                    chainsaw.ammo = ammo;

                if (contents != null)
                {
                    foreach (ItemData contentData in contents)
                    {
                        Item newContent = ItemManager.CreateByItemID(contentData.itemid, contentData.amount);
                        if (newContent != null)
                        {
                            newContent.condition = contentData.condition;
                            newContent.MoveToContainer(item.contents);
                        }
                    }
                }

                item.position = position;

                item.MarkDirty();

                return item;
            }

            public class InstanceData
            {
                public int dataInt;
                public int blueprintTarget;
                public int blueprintAmount;
                public uint subEntity;

                public InstanceData() { }
                public InstanceData(Item item)
                {
                    if (item.instanceData == null)
                        return;

                    dataInt = item.instanceData.dataInt;
                    blueprintAmount = item.instanceData.blueprintAmount;
                    blueprintTarget = item.instanceData.blueprintTarget;
                }

                public void Restore(Item item)
                {
                    if (item.instanceData == null)
                        item.instanceData = new ProtoBuf.Item.InstanceData();

                    item.instanceData.ShouldPool = false;

                    item.instanceData.blueprintAmount = blueprintAmount;
                    item.instanceData.blueprintTarget = blueprintTarget;
                    item.instanceData.dataInt = dataInt;

                    item.MarkDirty();
                }

                public bool IsValid()
                {
                    return dataInt != 0 || blueprintAmount != 0 || blueprintTarget != 0;
                }
            }
        }
        #endregion

        #region Localization
        public static string Message(string key, ulong playerId = 0U) => Instance.lang.GetMessage(key, Instance, playerId != 0U ? playerId.ToString() : null);

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["Notification.NotEnoughToContinue"] = "There are not enough players to continue the event...",
            ["Notification.NotEnoughToStart"] = "There is not enough players to start the event...",
            ["Notification.EventOpen"] = "The event <color=#9b2021>{0}</color> (<color=#9b2021>{1}</color>) is open for players\nIt will start in <color=#9b2021>{2} seconds</color>\nType <color=#9b2021>/event</color> to join",
            ["Notification.EventClosed"] = "The event has been closed to new players",
            ["Notification.EventFinished"] = "The event has finished",
            ["Notification.MaximumPlayers"] = "The event is already at maximum capacity",
            ["Notification.PlayerJoined"] = "<color=#9b2021>{0}</color> has joined the <color=#9b2021>{1}</color> event!",
            ["Notification.PlayerLeft"] = "<color=#9b2021>{0}</color> has left the <color=#9b2021>{1}</color> event!",
            ["Notification.RoundStartsIn"] = "Round starts in",
            ["Notification.EventWin"] = "<color=#9b2021>{0}</color> won the event!",
            ["Notification.EventWin.Multiple"] = "The following players won the event; <color=#9b2021>{0}</color>",
            ["Notification.EventWin.Multiple.Team"] = "<color={0}>Team {1}</color> won the event",
            ["Notification.Teams.Unbalanced"] = "The teams are unbalanced. Shuffling players...",
            ["Notification.Teams.TeamChanged"] = "You were moved to team <color=#9b2021>{0}</color>",
            ["Notification.OutOfBounds"] = "You are out of the playable area. <color=#9b2021>Return immediately</color> or you will be killed!",
            ["Notification.OutOfBounds.Time"] = "You have <color=#9b2021>{0} seconds</color> to return...",
            ["Notification.Death.Suicide"] = "<color=#9b2021>{0}</color> killed themselves...",
            ["Notification.Death.OOB"] = "<color=#9b2021>{0}</color> tried to run away...",
            ["Notification.Death.Killed"] = "<color=#9b2021>{0}</color> was killed by <color=#9b2021>{1}</color>",
            ["Notification.Suvival.Remain"] = "(<color=#9b2021>{0}</color> players remain)",
            ["Notification.SpectateCycle"] = "Press <color=#9b2021>JUMP</color> to cycle spectate targets",
            ["Notification.NextRoundStartsIn"] = "Round {0} has finished! The next round starts in {1} seconds",
            ["Notification.NextEventStartsIn"] = "The next event starts in {0} seconds",
            ["Notification.WaitingForPlayers"] = "Waiting for atleast {0} more players to start...",
            ["Notification.JoinerSpectate"] = "You are spectating until the event allows more players",

            ["Lobby.InEvent"] = "You can not teleport to the lobby whilst in an event",
            ["Lobby.IsPending"] = "You already have a pending TP to the lobby",
            ["Lobby.OnCooldown"] = "You must wait another {0} seconds before you can use this command again",
            ["Lobby.TPConfirmed"] = "You will be teleported to the lobby in {0} seconds",
            ["Lobby.NoTPPending"] = "You do not have a pending TP request to the lobby",
            ["Lobby.TPCancelled"] = "You have cancelled your request to TP to the lobby",
            ["Lobby.AlreadyThere"] = "You are already in the lobby zone",
            ["Lobby.BuildBlocked"] = "You can not TP to the lobby when building blocked",
            ["Lobby.Prevent.CargoShip"] = "You can not TP to the lobby whilst on the cargo ship",
            ["Lobby.Prevent.HAB"] = "You can not TP to the lobby whilst in a hot air balloon",
            ["Lobby.Prevent.Mounted"] = "You can not TP to the lobby whilst mounted",
            ["Lobby.Prevent.OilRig"] = "You can not TP to the lobby whilst on a oil rig",
            ["Lobby.Prevent.Bleeding"] = "You can not TP to the lobby whilst bleeding",
            ["Lobby.Prevent.RaidBlocked"] = "You can not TP to the lobby whilst raid blocked",
            ["Lobby.Prevent.CombatBlocked"] = "You can not TP to the lobby whilst combat blocked",
            ["Lobby.RespawnButton"] = "Event Lobby",
            ["Lobby.LeftZone"] = "You are not allowed to leave the lobby area",

            ["Info.Event.Current"] = "Current Event: {0} ({1})",
            ["Info.Event.Players"] = "\n{0} / {1} Players",
            ["Info.Event.Status"] = "Status : {0}",
            ["UI.NextGameStartsIn"] = "The next game starts in",
            ["UI.SelectClass"] = "Select a class to continue...",
            ["UI.Death.Killed"] = "You were killed by {0}",
            ["UI.Death.Suicide"] = "You are dead...",
            ["UI.Death.OOB"] = "Don't wander off...",

            ["Timer.NextRoundStartsIn"] = "The next round starts in",

            ["UI.EventWin"] = "<color=#9b2021>{0}</color> won the event!",
            ["UI.EventWin.Multiple"] = "Multiple players won the event!",
            ["UI.EventWin.Multiple.Team"] = "<color={0}>Team {1}</color> won the event",

            ["Error.CommandBlacklisted"] = "You can not run that command whilst playing an event",
            ["Error.Blacklisted"] = "You are blacklisted from joining events",
            ["Error.NoPermission"] = "This event is for <color=#ce422b>donators</color> only!",
            ["Info.Teleporter"] = "<size=25>{0}</size><size=16>\nGame: {1}\nPlayers: {2} / {3}\nRound: {4} / {5}\nStatus: {6}\n{7}\nKit: {8}</size>",
            ["Info.Teleporter.Score"] = "Score Limit: {0}",
            ["Info.Teleporter.Time"] = "Time Limit: {0}",


        };
        #endregion
    }

    namespace ArenaEx
    {
        public interface IEventPlugin
        {
            string EventName { get; }

            bool InitializeEvent(Arena.EventConfig config);

            void FormatScoreEntry(Arena.ScoreEntry scoreEntry, ulong langUserId, out string score1, out string score2);

            List<Arena.EventParameter> AdditionalParameters { get; }

            string ParameterIsValid(string fieldName, object value);

            bool CanUseClassSelector { get; }

            bool RequireTimeLimit { get; }

            bool RequireScoreLimit { get; }

            bool UseScoreLimit { get; }

            bool UseTimeLimit { get; }

            bool IsTeamEvent { get; }

            bool CanSelectTeam { get; }

            bool CanUseRustTeams { get; }

            bool IsRoundBased { get; }

            bool CanUseBots { get; }

            string EventIcon { get; }

            string TeamAName { get; }

            string TeamBName { get; }
        }
    }
}

