// Requires: Arena
using Newtonsoft.Json;
using Oxide.Plugins.ArenaEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Arena Team Deathmatch", "EsC1337", "1.0.1"), Description("Team Deathmatch event mode for Arena")]
    class ArenaTDM : RustPlugin, IEventPlugin
    {
        #region Oxide Hooks
        private void OnServerInitialized()
        {
            Arena.RegisterEvent(EventName, this);

            GetMessage = Message;
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void Unload()
        {
            if (!Arena.IsUnloading)
                Arena.UnregisterEvent(EventName);

            Configuration = null;
        }
        #endregion

        #region Event Checks
        public string EventName => "Team Deathmatch";

        public string EventIcon => Configuration.EventIcon;

        public bool InitializeEvent(Arena.EventConfig config) => Arena.InitializeEvent<TeamDeathmatchEvent>(this, config);

        public bool CanUseClassSelector => true;

        public bool RequireTimeLimit => true;

        public bool RequireScoreLimit => false;

        public bool UseScoreLimit => true;

        public bool UseTimeLimit => true;

        public bool IsTeamEvent => true;

        public bool CanSelectTeam => true;

        public bool CanUseRustTeams => true;

        public bool IsRoundBased => false;

        public bool CanUseBots => true;

        public string TeamAName => "Team A";

        public string TeamBName => "Team B";

        public void FormatScoreEntry(Arena.ScoreEntry scoreEntry, ulong langUserId, out string score1, out string score2)
        {
            score1 = string.Format(Message("Score.Kills", langUserId), scoreEntry.value1);
            score2 = string.Format(Message("Score.Deaths", langUserId), scoreEntry.value2);
        }

        public List<Arena.EventParameter> AdditionalParameters { get; } = new List<Arena.EventParameter>
        {
            new Arena.EventParameter
            {
                DataType = "bool",
                Field = "closeOnStart",
                Input = Arena.EventParameter.InputType.Toggle,
                IsRequired = false,
                Name = "Close Event On Start",
                DefaultValue = false
            }
        };

        public string ParameterIsValid(string fieldName, object value) => null;
        #endregion

        #region Event Classes
        public class TeamDeathmatchEvent : Arena.BaseEventGame
        {
            public Arena.Team winningTeam;

            private bool closeOnStart;

            private int teamAScore;
            private int teamBScore;

            internal override void InitializeEvent(IEventPlugin plugin, Arena.EventConfig config)
            {
                closeOnStart = config.GetParameter<bool>("closeEventOnStart");

                base.InitializeEvent(plugin, config);
            }

            protected override void StartEvent()
            {
                BalanceTeams();
                base.StartEvent();

                if (closeOnStart)
                    CloseEvent();
            }

            protected override void StartNextRound()
            {
                winningTeam = Arena.Team.None;
                teamAScore = 0;
                teamBScore = 0;

                BalanceTeams();

                base.StartNextRound();
            }

            protected override Arena.Team GetPlayerTeam()
            {
                if (GetTeamCount(Arena.Team.A) > GetTeamCount(Arena.Team.B))
                    return Arena.Team.B;
                return Arena.Team.A;
            }

            protected override void CreateEventPlayer(BasePlayer player, Arena.Team team = Arena.Team.None)
            {
                base.CreateEventPlayer(player, team);

                Arena.LockClothingSlots(player);
            }

            internal override bool CanDropActiveItem() => true;

            internal override int GetTeamScore(Arena.Team team) => team == Arena.Team.B ? teamBScore : teamAScore;

            protected override float GetDamageModifier(Arena.BaseEventPlayer eventPlayer, Arena.BaseEventPlayer attackerPlayer)
            {
                if (attackerPlayer != null && eventPlayer.Team == attackerPlayer.Team && Configuration.FriendlyFireModifier != 1f)
                    return Configuration.FriendlyFireModifier;

                return 1f;
            }

            internal override void OnEventPlayerDeath(Arena.BaseEventPlayer victim, Arena.BaseEventPlayer attacker = null, HitInfo info = null)
            {
                if (victim == null)
                    return;

                victim.OnPlayerDeath(attacker, Configuration.RespawnTime);

                if (attacker != null && victim != attacker && victim.Team != attacker.Team)
                {
                    int score;
                    if (attacker.Team == Arena.Team.B)
                        score = teamBScore += 1;
                    else score = teamAScore += 1;

                    attacker.OnKilledPlayer(info);

                    if (Config.ScoreLimit > 0 && score >= Config.ScoreLimit)
                    {
                        winningTeam = attacker.Team;
                        InvokeHandler.Invoke(this, EndRound, 0.1f);
                        return;
                    }
                }

                UpdateScoreboard();
                base.OnEventPlayerDeath(victim, attacker);
            }

            protected override void GetWinningPlayers(ref List<Arena.BaseEventPlayer> winners)
            {
                if (winningTeam < Arena.Team.None)
                {
                    if (eventPlayers.Count > 0)
                    {
                        for (int i = 0; i < eventPlayers.Count; i++)
                        {
                            Arena.BaseEventPlayer eventPlayer = eventPlayers[i];
                            if (eventPlayer == null)
                                continue;

                            if (eventPlayer.Team == winningTeam)
                                winners.Add(eventPlayer);
                        }
                    }
                }
            }

            #region Scoreboards
            protected override void BuildScoreboard()
            {
                scoreContainer = ArenaUI.CreateScoreboardBase(this);

                int index = -1;
                if (Config.RoundsToPlay > 0)
                    ArenaUI.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Round.Limit", 0UL), RoundNumber, Config.RoundsToPlay), index += 1);

                ArenaUI.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Team", 0UL), teamAScore, TeamA.Color, TeamB.Color, teamBScore), index += 1);

                if (Config.ScoreLimit > 0)
                    ArenaUI.CreatePanelEntry(scoreContainer, string.Format(GetMessage("Score.Limit", 0UL), Config.ScoreLimit), index += 1);

                ArenaUI.CreateScoreEntry(scoreContainer, string.Empty, "K", "D", index += 1);

                for (int i = 0; i < Mathf.Min(scoreData.Count, 15); i++)
                {
                    Arena.ScoreEntry score = scoreData[i];
                    ArenaUI.CreateScoreEntry(scoreContainer, $"<color={(score.team == Arena.Team.A ? TeamA.Color : TeamB.Color)}>{score.displayName}</color>", ((int)score.value1).ToString(), ((int)score.value2).ToString(), i + index + 1);
                }
            }

            protected override float GetFirstScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Kills;

            protected override float GetSecondScoreValue(Arena.BaseEventPlayer eventPlayer) => eventPlayer.Deaths;

            protected override void SortScores(ref List<Arena.ScoreEntry> list)
            {
                list.Sort(delegate (Arena.ScoreEntry a, Arena.ScoreEntry b)
                {
                    int primaryScore = a.value1.CompareTo(b.value1) * -1;

                    if (primaryScore == 0)
                        return a.value2.CompareTo(b.value2);

                    return primaryScore;
                });
            }
            #endregion
        }
        #endregion

        #region Config
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Respawn time (seconds)")]
            public int RespawnTime { get; set; }

            [JsonProperty(PropertyName = "Friendly fire damage modifier (0.0 is no damage, 1.0 is normal damage)")]
            public float FriendlyFireModifier { get; set; }

            public string EventIcon { get; set; }

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
                EventIcon = "https://www.rustedit.io/images/arena/arena_tdm.png",
                RespawnTime = 5,
                FriendlyFireModifier = 1.0f,
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (Configuration.Version < new Core.VersionNumber(0, 4, 1))
                Configuration.FriendlyFireModifier = baseConfig.FriendlyFireModifier;

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion

        #region Localization
        public string Message(string key, ulong playerId = 0U) => lang.GetMessage(key, this, playerId != 0U ? playerId.ToString() : null);

        private static Func<string, ulong, string> GetMessage;

        private readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["Score.Kills"] = "Kills: {0}",
            ["Score.Deaths"] = "Deaths: {0}",
            ["Score.Name"] = "Kills",
            ["Score.Limit"] = "Score Limit : {0}",
            ["Score.Team"] = "{0} : <color={1}>Team A</color> | <color={2}>Team B</color> : {3}",
            ["Round.Limit"] = "Round : {0} / {1}"
        };
        #endregion
    }
}
