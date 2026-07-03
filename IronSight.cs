using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Newtonsoft.Json;
using ConVar;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("IronSight", "Panado", "1.3.3")]
    [Description("Iron Sight API.")]
    public class IronSight : CovalencePlugin
    {
        private const int MaxQueued = 200;
        private const float RetryDelay = 20f;

        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);
        private const int MaxBlacklistScanLength = 512;

        private string ApiUrl    = "";
        private string ApiToken  = "";
        private bool _enableLogs = false;

        private class QueueItem
        {
            public string Path;
            public string Json;
        }

        private class ChatPayload
        {
            [JsonProperty("message")]      public string Message;
            [JsonProperty("steam_id")]     public string SteamId;
            [JsonProperty("player_name")]  public string PlayerName;
            [JsonProperty("team_message")] public bool TeamMessage;
        }

        private class PvpPayload
        {
            [JsonProperty("killer_steam_id")] public string KillerSteamId;
            [JsonProperty("victim_name")]     public string VictimName;
            [JsonProperty("victim_steam_id", NullValueHandling = NullValueHandling.Ignore)] public string VictimSteamId;
            [JsonProperty("combatlog_cache")] public object CombatlogCache;
        }

        private class ReportPayload
        {
            [JsonProperty("report_type")]        public string ReportType;
            [JsonProperty("report_reason")]      public string ReportReason;
            [JsonProperty("report_description")] public string ReportDescription;
            [JsonProperty("reporter_name")]      public string ReporterName;
            [JsonProperty("reporter_steam_id")]  public string ReporterSteamId;
            [JsonProperty("reported_steam_id")]  public string ReportedSteamId;
        }

        private class ConnectPayload
        {
            [JsonProperty("steam_id")]    public string SteamId;
            [JsonProperty("ip")]          public string Ip;
            [JsonProperty("player_name")] public string PlayerName;
        }

        private class DisconnectPayload
        {
            [JsonProperty("steam_id")]    public string SteamId;
            [JsonProperty("player_name")] public string PlayerName;
        }

        private class TeamInfoPayload
        {
            [JsonProperty("event_type")]   public string EventType;
            [JsonProperty("team_leader")]  public string TeamLeader;
            [JsonProperty("team_members")] public List<string> TeamMembers;
            [JsonProperty("target_player", NullValueHandling = NullValueHandling.Ignore)] public string TargetPlayer;
            [JsonProperty("event_time")]   public string EventTime;
        }

        private class MuteSyncRequest
        {
            [JsonProperty("steam_ids")] public List<string> SteamIds;
        }

        private class MuteSyncResponse
        {
            [JsonProperty("active_mutes")] public Dictionary<string, long?> ActiveMutes;
        }

        private class MuteInfo
        {
            public bool IsMuted;
            public bool IsPermanent;
            public DateTime? ExpiresAt;
        }

        private class Coordinates
        {
            [JsonProperty("x")] public float X;
            [JsonProperty("y")] public float Y;
            [JsonProperty("z")] public float Z;

            public static Coordinates From(Vector3 v) => new Coordinates { X = v.x, Y = v.y, Z = v.z };
        }

        private class ServerLogPayload
        {
            [JsonProperty("event_type")]                                                     public string EventType;
            [JsonProperty("admin_steam_id",  NullValueHandling = NullValueHandling.Ignore)] public string AdminSteamId;
            [JsonProperty("admin_name",      NullValueHandling = NullValueHandling.Ignore)] public string AdminName;
            [JsonProperty("target_steam_id", NullValueHandling = NullValueHandling.Ignore)] public string TargetSteamId;
            [JsonProperty("target_name",     NullValueHandling = NullValueHandling.Ignore)] public string TargetName;
            [JsonProperty("command",         NullValueHandling = NullValueHandling.Ignore)] public string Command;
            [JsonProperty("coordinates",     NullValueHandling = NullValueHandling.Ignore)] public Coordinates Coords;
            [JsonProperty("details",         NullValueHandling = NullValueHandling.Ignore)] public object Details;
        }

        private readonly Queue<QueueItem> _queue = new Queue<QueueItem>();
        private bool _sending = false;
        private readonly Dictionary<string, MuteInfo> _muteCache = new Dictionary<string, MuteInfo>();

        private readonly Dictionary<string, (string Id, string Name)> _pendingBanAdmin   = new Dictionary<string, (string, string)>();
        private readonly Dictionary<string, (string Id, string Name)> _pendingKickAdmin  = new Dictionary<string, (string, string)>();
        private readonly Dictionary<string, (string Id, string Name)> _pendingUnbanAdmin = new Dictionary<string, (string, string)>();

        private List<string> _blacklistedWords = new List<string>();
        private Regex _fluffyRegex = null;
        private Regex _spacedRegex = null;

        // ----------------------------- CONFIG -----------------------------

        protected override void LoadDefaultConfig()
        {
            Config["api_url"]     = "https://ironsight.archipel.gg/api";
            Config["api_token"]   = "your-api-token-here";
            Config["enable_logs"] = false;
            SaveConfig();
        }

        void Init()
        {
            ApiUrl      = Config["api_url"]?.ToString() ?? "";
            ApiToken    = Config["api_token"]?.ToString() ?? "";
            _enableLogs = Config["enable_logs"] is bool b && b;

            if (string.IsNullOrEmpty(ApiUrl))
                Puts("WARNING: Set api_url in config/IronSight.json");

            if (string.IsNullOrEmpty(ApiToken) || ApiToken == "your-api-token-here")
                Puts("WARNING: Set api_token in config/IronSight.json");

            Puts($"IronSight loaded. (logging {(_enableLogs ? "enabled" : "disabled")})");
            timer.Every(60f, CheckServerHealth);
            timer.Every(60f, () =>
            {
                var online = new List<string>();
                foreach (var p in BasePlayer.activePlayerList)
                    online.Add(p.UserIDString);
                SyncMutes(online);
            });
            FetchBlacklistedWords();

            var onlineSteamIds = new List<string>();
            foreach (var p in BasePlayer.activePlayerList)
                onlineSteamIds.Add(p.UserIDString);
            SyncMutes(onlineSteamIds);
        }

        // ----------------------------- Hooks -----------------------------

        void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            SyncMutes(new List<string> { player.UserIDString });

            string raw   = player.Connection?.ipaddress ?? "";
            int colon    = raw.LastIndexOf(':');
            string ip    = colon >= 0 ? raw.Substring(0, colon) : raw;

            Enqueue("/ingest/connect", new ConnectPayload
            {
                SteamId    = Truncate(player.UserIDString, 64),
                Ip         = ip,
                PlayerName = Truncate(player.displayName, 128)
            });

            if (player.IsAdmin)
            {
                EnqueueServerLog("ADMIN_CONNECT",
                    adminSteamId: player.UserIDString,
                    adminName:    player.displayName,
                    details:      new { ip_address = ip });
            }
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _muteCache.Remove(player.UserIDString);

            Enqueue("/ingest/disconnect", new DisconnectPayload
            {
                SteamId    = Truncate(player.UserIDString, 64),
                PlayerName = Truncate(player.displayName, 128)
            });

            if (player.IsAdmin)
            {
                EnqueueServerLog("ADMIN_DISCONNECT",
                    adminSteamId: player.UserIDString,
                    adminName:    player.displayName,
                    details:      new { reason });
            }
        }

        object OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            if (channel != Chat.ChatChannel.Global && channel != Chat.ChatChannel.Team) return null;
            if (player == null) return null;

            bool isTeamChat = channel == Chat.ChatChannel.Team;
            bool isCommand  = message.StartsWith("/");

            if (!isTeamChat && !isCommand && IsPlayerMuted(player.UserIDString))
            {
                string muteMsg = "You are permanently muted.";
                if (_muteCache.TryGetValue(player.UserIDString, out var muteInfo) && !muteInfo.IsPermanent && muteInfo.ExpiresAt.HasValue)
                {
                    TimeSpan remaining = muteInfo.ExpiresAt.Value - DateTime.UtcNow;
                    muteMsg = $"You are muted for {FormatTimeSpan(remaining)}.";
                }
                player.ChatMessage(muteMsg);
                return true;
            }

            if (ContainsBlacklistedWord(message))
            {
                player.ChatMessage("Your message contained words we don't allow");
                return true;
            }

            Enqueue("/ingest/chat", new ChatPayload
            {
                Message     = Truncate(message, 1000),
                SteamId     = Truncate(player.UserIDString, 64),
                PlayerName  = Truncate(player.displayName, 128),
                TeamMessage = isTeamChat
            });
            return null;
        }

        void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || player.IsNpc) return;
            var killer = info.Initiator as BasePlayer;
            if (killer == null || killer.IsNpc || killer == player) return;

            var cache = new Dictionary<string, object>
            {
                ["weapon"]    = info.WeaponPrefab?.ShortPrefabName ?? "",
                ["bodypart"]  = StringPool.Get(info.HitBone),
                ["distance"]  = Math.Round(Vector3.Distance(killer.transform.position, player.transform.position), 1),
                ["hp_before"] = Math.Round(info.damageTypes.Total(), 1),
                ["hp_after"]  = 0
            };

            Enqueue("/ingest/pvp", new PvpPayload
            {
                KillerSteamId  = Truncate(killer.UserIDString, 64),
                VictimName     = Truncate(player.displayName, 128),
                VictimSteamId  = Truncate(player.UserIDString, 64),
                CombatlogCache = cache
            });
        }

        void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type)
        {
            if (reporter == null) return;

            Enqueue("/ingest/reports", new ReportPayload
            {
                ReportType        = type ?? "",
                ReportReason      = subject ?? "",
                ReportDescription = message ?? "",
                ReporterName      = Truncate(reporter.displayName, 128),
                ReporterSteamId   = Truncate(reporter.UserIDString, 64),
                ReportedSteamId   = Truncate(targetId ?? "", 64)
            });
        }

        void OnTeamCreated(BasePlayer player, RelationshipManager.PlayerTeam team)
        {
            if (team == null) return;
            EnqueueTeamEvent("created", team);
        }

        void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (team == null) return;
            EnqueueTeamEvent("joined", team);
        }

        void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (team == null) return;
            EnqueueTeamEvent("left", team);
        }

        void OnTeamInvite(BasePlayer inviter, BasePlayer target)
        {
            if (inviter == null || target == null) return;
            var team = inviter.Team;
            if (team == null) return;
            EnqueueTeamEvent("invited", team, inviter.UserIDString, target.UserIDString);
        }

        // Chat commands typed by an admin with /
        void OnUserCommand(IPlayer player, string name, string[] args)
        {
            if (player == null || !player.IsAdmin) return;

            string lower = name.ToLower();
            TrackPendingAdminAction(lower, args, player.Id, player.Name);

            // Outcome hooks handle these — skip to avoid double-logging
            if (lower == "ban" || lower == "banid" || lower == "ban.steamid") return;
            if (lower == "kick" || lower == "kickid") return;
            if (lower == "unban") return; // OnUserUnbanned emits

            // Dedicated state-change hooks handle these
            if (lower == "noclip" || lower == "god" || lower == "godmode") return;

            switch (lower)
            {
                case "killplayer":
                    LogKillPlayerChat(player, args);
                    return;

                case "mute":
                case "unmute":
                    LogMuteChat(player, lower, args);
                    return;

                case "spectate":
                    LogSpectateChat(player, args);
                    return;

                case "teleport":
                case "tp":
                case "teleportpos":
                case "tppos":
                case "teleport2me":
                case "tpme":
                case "tphere":
                    LogTeleportChat(player, lower, args);
                    return;

                case "give":
                case "giveid":
                case "givearm":
                case "giveto":
                case "giveall":
                    LogGiveChat(player, lower, args);
                    return;

                case "spawn":
                case "spawnat":
                case "spawnhere":
                case "spawnitem":
                    EnqueueServerLog("SPAWN",
                        adminSteamId: player.Id,
                        adminName:    player.Name,
                        command:      BuildChatCommand(name, args),
                        details:      new { action = lower, prefab = args != null && args.Length > 0 ? args[0] : null });
                    return;

                case "vanish":
                case "invisible":
                    EnqueueServerLog("VANISH",
                        adminSteamId: player.Id,
                        adminName:    player.Name,
                        command:      BuildChatCommand(name, args));
                    return;

                case "freeze":
                case "unfreeze":
                case "freezeall":
                case "unfreezeall":
                    LogFreezeChat(player, lower, args);
                    return;

                case "kickall":
                    EnqueueServerLog("KICK_ALL",
                        adminSteamId: player.Id,
                        adminName:    player.Name,
                        command:      BuildChatCommand(name, args));
                    return;
            }

            EnqueueServerLog("ADMIN_COMMAND",
                adminSteamId: player.Id,
                adminName:    player.Name,
                command:      BuildChatCommand(name, args));
        }

        // Console / RCON commands
        void OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.IsAdmin) return;

            string cmdName  = (arg.cmd?.Name     ?? "").ToLower();
            string fullName =  arg.cmd?.FullName  ?? "";

            if (fullName == "chat.say" || fullName == "chat.teamsay") return;

            var    conn      = arg.Connection;
            string adminId   = conn?.userid.ToString();
            string adminName = conn?.username;

            TrackPendingAdminAction(cmdName, arg.Args != null ? Array.ConvertAll(arg.Args, sv => sv.ToString()) : null, adminId, adminName);

            // Outcome hooks handle these — skip to avoid double-logging
            if (cmdName == "ban" || cmdName == "banid" || cmdName == "ban.steamid") return;
            if (cmdName == "kick" || cmdName == "kickid") return;
            if (cmdName == "unban") return; // OnUserUnbanned emits

            // Dedicated state-change hooks handle these
            if (cmdName == "noclip" || cmdName == "god" || cmdName == "godmode") return;

            switch (cmdName)
            {
                case "kickall":
                    EnqueueServerLog("KICK_ALL",
                        adminSteamId: adminId,
                        adminName:    adminName,
                        command:      "kickall");
                    return;

                case "killplayer":
                    LogKillPlayer(arg, adminId, adminName);
                    return;

                case "mute":
                case "unmute":
                    LogMuteCommand(arg, adminId, adminName);
                    return;

                case "spectate":
                    LogSpectate(arg, adminId, adminName);
                    return;

                case "teleport":
                case "teleportpos":
                case "teleport2me":
                    LogTeleport(arg, adminId, adminName);
                    return;

                case "give":
                case "giveid":
                case "givearm":
                case "giveto":
                case "giveall":
                    LogGive(arg, adminId, adminName);
                    return;

                case "spawn":
                case "spawnat":
                case "spawnhere":
                case "spawnitem":
                    LogSpawn(arg, adminId, adminName);
                    return;

                case "entid":
                    LogEntity(arg, adminId, adminName);
                    return;

                case "freeze":
                case "unfreeze":
                case "freezeall":
                case "unfreezeall":
                    LogFreezeCommand(arg, adminId, adminName);
                    return;
            }

            switch (fullName)
            {
                case "heli.call":
                case "heli.calltome":
                case "global.drop":
                case "drop":
                case "supply.call":
                case "supply.drop":
                    LogServerEvent(arg, adminId, adminName, fullName);
                    return;
            }

        }

        void OnUserBanned(string name, string id, string ipAddress, string reason)
        {
            _pendingBanAdmin.TryGetValue(id ?? "", out var admin);
            _pendingBanAdmin.Remove(id ?? "");
            ulong.TryParse(id, out ulong banUid);
            var bp = banUid != 0 ? BasePlayer.FindByID(banUid) : null;
            EnqueueServerLog("BAN",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: id,
                targetName:    name,
                coordinates:   bp != null ? Coordinates.From(bp.transform.position) : null,
                details:       new { reason, ip_address = ipAddress });
        }

        void OnUserUnbanned(string name, string id)
        {
            _pendingUnbanAdmin.TryGetValue(id ?? "", out var admin);
            _pendingUnbanAdmin.Remove(id ?? "");
            if (admin.Id == null)
            {
                _pendingUnbanAdmin.TryGetValue(name ?? "", out admin);
                _pendingUnbanAdmin.Remove(name ?? "");
            }
            EnqueueServerLog("UNBAN",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: id,
                targetName:    name);
        }

        void OnUserKicked(IPlayer player, string reason)
        {
            if (player == null) return;
            _pendingKickAdmin.TryGetValue(player.Id ?? "", out var admin);
            _pendingKickAdmin.Remove(player.Id ?? "");
            var bp = player.Object as BasePlayer;
            EnqueueServerLog("KICK",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: player.Id,
                targetName:    player.Name,
                coordinates:   bp != null ? Coordinates.From(bp.transform.position) : null,
                details:       new { reason });
        }

        void OnPlayerNoclipToggle(BasePlayer player, bool enabled)
        {
            if (player == null) return;
            EnqueueServerLog("NOCLIP_TOGGLE",
                adminSteamId: player.UserIDString,
                adminName:    player.displayName,
                details:      new { enabled });
        }

        void OnPlayerGodmodeToggled(BasePlayer player, bool enabled)
        {
            if (player == null) return;
            EnqueueServerLog("GODMODE_TOGGLE",
                adminSteamId: player.UserIDString,
                adminName:    player.displayName,
                details:      new { enabled });
        }

        void OnPlayerSpectateEnd(BasePlayer player, string spectateFilter)
        {
            if (player == null) return;
            EnqueueServerLog("SPECTATE_END",
                adminSteamId: player.UserIDString,
                adminName:    player.displayName,
                details:      new { filter = spectateFilter });
        }

        // ----------------------------- Health Check -----------------------------

        private void CheckServerHealth()
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {ApiToken}",
                ["x-api-key"]     = ApiToken
            };

            webrequest.Enqueue($"{ApiUrl}/server-health-check", null, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    //Puts($"Health check failed (HTTP {code})");
                }
                else
                    Log($"Health check → HTTP {code}");
            }, this, RequestMethod.GET, headers);
        }

        // ----------------------------- Mute -----------------------------

        private bool IsPlayerMuted(string steamId)
        {
            if (!_muteCache.TryGetValue(steamId, out var info) || !info.IsMuted) return false;
            if (info.IsPermanent) return true;
            if (info.ExpiresAt.HasValue && info.ExpiresAt.Value <= DateTime.UtcNow)
            {
                _muteCache[steamId] = new MuteInfo { IsMuted = false };
                return false;
            }
            return true;
        }

        private void SyncMutes(List<string> steamIds)
        {
            if (steamIds.Count == 0) return;
            if (steamIds.Count > 200)
                steamIds = steamIds.GetRange(0, 200);

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"]  = "application/json",
                ["Authorization"] = $"Bearer {ApiToken}",
                ["x-api-key"]     = ApiToken
            };

            var body = JsonConvert.SerializeObject(new MuteSyncRequest { SteamIds = steamIds });
            var url  = $"{ApiUrl}/ingest/mute-sync";
            Log($"POST {url} | {steamIds.Count} id(s)");

            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    Puts($"Mute sync failed (HTTP {code})");
                    return;
                }

                try
                {
                    var data = JsonConvert.DeserializeObject<MuteSyncResponse>(response);
                    if (data == null) return;

                    foreach (var id in steamIds)
                        _muteCache[id] = new MuteInfo { IsMuted = false };

                    if (data.ActiveMutes != null)
                    {
                        foreach (var kvp in data.ActiveMutes)
                        {
                            DateTime? expiresAt = null;
                            if (kvp.Value.HasValue)
                                expiresAt = DateTimeOffset.FromUnixTimeSeconds(kvp.Value.Value).UtcDateTime;

                            _muteCache[kvp.Key] = new MuteInfo
                            {
                                IsMuted     = true,
                                IsPermanent = !kvp.Value.HasValue,
                                ExpiresAt   = expiresAt
                            };
                            Log($"Player {kvp.Key} muted. Permanent: {!kvp.Value.HasValue}, Expires: {expiresAt?.ToString("O") ?? "never"}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Puts($"Error parsing mute sync response: {ex.Message}");
                }
            }, this, RequestMethod.POST, headers);
        }

        // ----------------------------- Blacklist -----------------------------

        private void FetchBlacklistedWords()
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {ApiToken}",
                ["x-api-key"]     = ApiToken
            };

            var url = $"{ApiUrl}/blacklisted-words";
            Log($"GET {url}");

            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    Puts($"Blacklist fetch failed (HTTP {code})");
                    return;
                }

                _blacklistedWords = new List<string>();
                if (!string.IsNullOrEmpty(response))
                {
                    foreach (var word in response.Split(';'))
                    {
                        var w = word.Trim();
                        if (!string.IsNullOrEmpty(w))
                            _blacklistedWords.Add(w);
                    }
                }

                BuildBlacklistRegex();
                Puts($"Blacklist loaded: {_blacklistedWords.Count} word(s)");
            }, this, RequestMethod.GET, headers);
        }

        private void BuildBlacklistRegex()
        {
            if (_blacklistedWords.Count == 0)
            {
                _fluffyRegex = null;
                _spacedRegex = null;
                return;
            }

            var fluffyParts = new List<string>();
            var spacedParts = new List<string>();

            foreach (var word in _blacklistedWords)
            {
                var fluffy = new List<string>();
                foreach (char c in word)
                    fluffy.Add(Regex.Escape(c.ToString()) + "{1,5}");
                fluffyParts.Add(string.Join("", fluffy));
                spacedParts.Add(Regex.Escape(word).Replace(@"\ ", @"\s+"));
            }

            _fluffyRegex = new Regex(@"\b(?:" + string.Join("|", fluffyParts) + @")\b", RegexOptions.IgnoreCase, RegexMatchTimeout);
            _spacedRegex = new Regex(@"\b(?:" + string.Join("|", spacedParts) + @")\b", RegexOptions.IgnoreCase, RegexMatchTimeout);
        }

        private bool ContainsBlacklistedWord(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            if (_fluffyRegex == null && _spacedRegex == null) return false;
            if (message.Length > MaxBlacklistScanLength)
                message = message.Substring(0, MaxBlacklistScanLength);

            try
            {
                if (_fluffyRegex != null && _fluffyRegex.IsMatch(message)) return true;
                if (_spacedRegex != null && _spacedRegex.IsMatch(message)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                Puts("Blacklist match timed out — message allowed through.");
            }
            return false;
        }

        // ----------------------------- Admin Command Logging -----------------------------

        private void LogKillPlayer(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            var target = covalence.Players.FindPlayer(arg.GetString(0));
            ulong.TryParse(target?.Id, out ulong killUid1);
            var bp = killUid1 != 0 ? BasePlayer.FindByID(killUid1) : null;
            EnqueueServerLog("KILL_PLAYER",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: target?.Id,
                targetName:    target?.Name ?? arg.GetString(0),
                command:       $"killplayer {arg.FullString}".Trim(),
                coordinates:   bp != null ? Coordinates.From(bp.transform.position) : null);
        }

        private void LogKillPlayerChat(IPlayer admin, string[] args)
        {
            string targetSteamId = null, targetName = null;
            Coordinates coords   = null;
            if (args != null && args.Length > 0)
            {
                var target = covalence.Players.FindPlayer(args[0]);
                ulong.TryParse(target?.Id, out ulong killUid2);
                var bp        = killUid2 != 0 ? BasePlayer.FindByID(killUid2) : null;
                targetSteamId = target?.Id;
                targetName    = target?.Name ?? args[0];
                coords        = bp != null ? Coordinates.From(bp.transform.position) : null;
            }
            EnqueueServerLog("KILL_PLAYER",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       BuildChatCommand("killplayer", args),
                coordinates:   coords);
        }

        private void LogMuteCommand(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            string cmdName   = arg.cmd.Name.ToLower();
            var target       = covalence.Players.FindPlayer(arg.GetString(0));
            string eventType = cmdName == "unmute" ? "UNMUTE" : "MUTE";
            EnqueueServerLog(eventType,
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: target?.Id,
                targetName:    target?.Name ?? arg.GetString(0),
                command:       $"{cmdName} {arg.FullString}".Trim());
        }

        private void LogMuteChat(IPlayer admin, string cmdName, string[] args)
        {
            string eventType = cmdName == "unmute" ? "UNMUTE" : "MUTE";
            string targetSteamId = null, targetName = null;
            if (args != null && args.Length > 0)
            {
                var target    = covalence.Players.FindPlayer(args[0]);
                targetSteamId = target?.Id;
                targetName    = target?.Name ?? args[0];
            }
            EnqueueServerLog(eventType,
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       BuildChatCommand(cmdName, args));
        }

        private void LogSpectate(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            string targetSteamId = null, targetName = null;
            object details       = null;

            if (arg.Args != null && arg.Args.Length > 0)
            {
                var target    = covalence.Players.FindPlayer(arg.GetString(0));
                targetSteamId = target?.Id;
                targetName    = target?.Name ?? arg.GetString(0);
                details       = new { filter = arg.GetString(0) };
            }

            EnqueueServerLog("SPECTATE_START",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       $"spectate {arg.FullString}".Trim(),
                details:       details);
        }

        private void LogSpectateChat(IPlayer admin, string[] args)
        {
            string targetSteamId = null, targetName = null;
            object details       = null;

            if (args != null && args.Length > 0)
            {
                var target    = covalence.Players.FindPlayer(args[0]);
                targetSteamId = target?.Id;
                targetName    = target?.Name ?? args[0];
                details       = new { filter = args[0] };
            }

            EnqueueServerLog("SPECTATE_START",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       BuildChatCommand("spectate", args),
                details:       details);
        }

        private void LogTeleport(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            string cmdName        = arg.cmd.Name.ToLower();
            string targetSteamId  = null, targetName = null;
            object details        = null;

            switch (cmdName)
            {
                case "teleport":
                    if (arg.Args.Length == 1)
                    {
                        var t         = covalence.Players.FindPlayer(arg.GetString(0));
                        targetSteamId = t?.Id;
                        targetName    = t?.Name ?? arg.GetString(0);
                        details       = new { action = "tp_self_to_player", destination = targetName, destination_steam_id = targetSteamId };
                    }
                    else if (arg.Args.Length >= 2)
                    {
                        var p1  = covalence.Players.FindPlayer(arg.GetString(0));
                        var p2  = covalence.Players.FindPlayer(arg.GetString(1));
                        details = new { action = "tp_player_to_player", player = p1?.Name ?? arg.GetString(0), player_steam_id = p1?.Id, destination = p2?.Name ?? arg.GetString(1), destination_steam_id = p2?.Id };
                    }
                    break;
                case "teleportpos":
                    details = new { action = "tp_self_to_position", position = arg.FullString.ToString() };
                    break;
                case "teleport2me":
                    var t2        = covalence.Players.FindPlayer(arg.GetString(0));
                    targetSteamId = t2?.Id;
                    targetName    = t2?.Name ?? arg.GetString(0);
                    details       = new { action = "tp_player_to_admin", target_player = targetName, target_steam_id = targetSteamId };
                    break;
            }

            EnqueueServerLog("TELEPORT",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       $"{cmdName} {arg.FullString}".Trim(),
                details:       details);
        }

        private void LogTeleportChat(IPlayer admin, string cmdName, string[] args)
        {
            string targetSteamId = null, targetName = null;
            object details       = null;

            if (args != null && args.Length > 0)
            switch (cmdName)
            {
                case "teleport":
                case "tp":
                    if (args.Length == 1)
                    {
                        var t         = covalence.Players.FindPlayer(args[0]);
                        targetSteamId = t?.Id;
                        targetName    = t?.Name ?? args[0];
                        details       = new { action = "tp_self_to_player", destination = targetName, destination_steam_id = targetSteamId };
                    }
                    else if (args.Length >= 2)
                    {
                        var p1  = covalence.Players.FindPlayer(args[0]);
                        var p2  = covalence.Players.FindPlayer(args[1]);
                        details = new { action = "tp_player_to_player", player = p1?.Name ?? args[0], player_steam_id = p1?.Id, destination = p2?.Name ?? args[1], destination_steam_id = p2?.Id };
                    }
                    break;
                case "teleportpos":
                case "tppos":
                    details = new { action = "tp_self_to_position", position = string.Join(" ", args) };
                    break;
                case "teleport2me":
                case "tpme":
                case "tphere":
                    var t2        = covalence.Players.FindPlayer(args[0]);
                    targetSteamId = t2?.Id;
                    targetName    = t2?.Name ?? args[0];
                    details       = new { action = "tp_player_to_admin", target_player = targetName, target_steam_id = targetSteamId };
                    break;
            }

            EnqueueServerLog("TELEPORT",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       BuildChatCommand(cmdName, args),
                details:       details);
        }

        private void LogGive(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            string cmdName       = arg.cmd.Name.ToLower();
            string itemName      = null, amount = "1";
            string targetSteamId = null, targetName = null;

            try
            {
                switch (cmdName)
                {
                    case "give":
                        itemName = ItemManager.FindItemDefinition(arg.GetInt(0))?.shortname ?? arg.GetString(0);
                        if (arg.Args.Length >= 2) amount = arg.GetString(1);
                        break;
                    case "giveid":
                        if (arg.Args.Length <= 2)
                        {
                            itemName = ItemManager.FindItemDefinition(arg.GetInt(0))?.shortname ?? arg.GetString(0);
                            if (arg.Args.Length == 2) amount = arg.GetString(1);
                        }
                        else
                        {
                            var t         = covalence.Players.FindPlayer(arg.GetString(0));
                            targetSteamId = t?.Id;
                            targetName    = t?.Name ?? arg.GetString(0);
                            itemName      = ItemManager.FindItemDefinition(arg.GetInt(1))?.shortname ?? arg.GetString(1);
                            amount        = arg.GetString(2);
                        }
                        break;
                    case "givearm":
                        itemName = ItemManager.FindItemDefinition(arg.GetInt(0))?.shortname ?? arg.GetString(0);
                        break;
                    case "giveto":
                        if (arg.Args.Length < 2) return;
                        var target    = covalence.Players.FindPlayer(arg.GetString(0));
                        targetSteamId = target?.Id;
                        targetName    = target?.Name ?? arg.GetString(0);
                        itemName      = ItemManager.FindItemDefinition(arg.GetInt(1))?.shortname ?? arg.GetString(1);
                        if (arg.Args.Length >= 3) amount = arg.GetString(2);
                        break;
                    case "giveall":
                        itemName = ItemManager.FindItemDefinition(arg.GetInt(0))?.shortname ?? arg.GetString(0);
                        if (arg.Args.Length >= 2) amount = arg.GetString(1);
                        break;
                }
            }
            catch { /* item lookup failed, raw strings used instead */ }

            EnqueueServerLog("GIVE",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       $"{cmdName} {arg.FullString}".Trim(),
                details:       new { action = cmdName, item = itemName, amount, recipient = targetName ?? "self" });
        }

        private void LogGiveChat(IPlayer admin, string cmdName, string[] args)
        {
            string item = args != null && args.Length > 0 ? args[0] : null;
            string amt  = args != null && args.Length > 1 ? args[1] : "1";
            EnqueueServerLog("GIVE",
                adminSteamId: admin.Id,
                adminName:    admin.Name,
                command:      BuildChatCommand(cmdName, args),
                details:      new { action = cmdName, item, amount = amt });
        }

        private void LogSpawn(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            string cmdName    = arg.cmd.Name.ToLower();
            string prefab     = arg.GetString(0);
            BasePlayer player = arg.Connection?.player as BasePlayer;

            EnqueueServerLog("SPAWN",
                adminSteamId: adminId,
                adminName:    adminName,
                command:      $"{cmdName} {arg.FullString}".Trim(),
                coordinates:  player != null ? Coordinates.From(player.transform.position) : null,
                details:      new { action = cmdName, prefab });
        }

        private void LogEntity(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            if (arg.Args == null || arg.Args.Length < 2) return;
            string action = arg.GetString(0);

            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(arg.GetUInt64(1)));
            if (entity == null) return;

            string entityType   = entity.ShortPrefabName;
            string ownerSteamId = null, ownerName = null;

            if (entity is BaseEntity bentity && bentity.OwnerID != 0)
            {
                var owner   = covalence.Players.FindPlayerById(bentity.OwnerID.ToString());
                ownerSteamId = owner?.Id ?? bentity.OwnerID.ToString();
                ownerName    = owner?.Name;
            }

            Coordinates entityCoords = null;
            if (entity.transform != null)
                entityCoords = Coordinates.From(entity.transform.position);

            EnqueueServerLog("ENTITY",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: ownerSteamId,
                targetName:    ownerName,
                command:       $"entid {arg.FullString}".Trim(),
                coordinates:   entityCoords,
                details:       new { action, entity_type = entityType, owner_steam_id = ownerSteamId, owner_name = ownerName });
        }

        private void LogFreezeCommand(ConsoleSystem.Arg arg, string adminId, string adminName)
        {
            string cmdName = arg.cmd.Name.ToLower();

            if (cmdName == "freezeall" || cmdName == "unfreezeall")
            {
                EnqueueServerLog(cmdName == "freezeall" ? "FREEZE_ALL" : "UNFREEZE_ALL",
                    adminSteamId: adminId,
                    adminName:    adminName,
                    command:      cmdName);
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0) return;
            var target = covalence.Players.FindPlayer(arg.GetString(0));
            EnqueueServerLog(cmdName == "freeze" ? "FREEZE" : "UNFREEZE",
                adminSteamId:  adminId,
                adminName:     adminName,
                targetSteamId: target?.Id,
                targetName:    target?.Name ?? arg.GetString(0),
                command:       $"{cmdName} {arg.GetString(0)}");
        }

        private void LogFreezeChat(IPlayer admin, string cmdName, string[] args)
        {
            if (cmdName == "freezeall" || cmdName == "unfreezeall")
            {
                EnqueueServerLog(cmdName == "freezeall" ? "FREEZE_ALL" : "UNFREEZE_ALL",
                    adminSteamId: admin.Id,
                    adminName:    admin.Name,
                    command:      BuildChatCommand(cmdName, args));
                return;
            }

            string targetSteamId = null, targetName = null;
            if (args != null && args.Length > 0)
            {
                var target    = covalence.Players.FindPlayer(args[0]);
                targetSteamId = target?.Id;
                targetName    = target?.Name ?? args[0];
            }
            EnqueueServerLog(cmdName == "freeze" ? "FREEZE" : "UNFREEZE",
                adminSteamId:  admin.Id,
                adminName:     admin.Name,
                targetSteamId: targetSteamId,
                targetName:    targetName,
                command:       BuildChatCommand(cmdName, args));
        }

        private void LogServerEvent(ConsoleSystem.Arg arg, string adminId, string adminName, string fullName)
        {
            BasePlayer player = arg.Connection?.player as BasePlayer;

            string action;
            switch (fullName)
            {
                case "heli.call":     action = "heli_call"; break;
                case "heli.calltome": action = "heli_call_to_me"; break;
                case "global.drop":
                case "drop":          action = "heli_drop"; break;
                case "supply.call":   action = "supply_drop_random"; break;
                case "supply.drop":   action = "supply_drop_position"; break;
                default:              action = fullName; break;
            }

            EnqueueServerLog("SERVER_EVENT",
                adminSteamId: adminId,
                adminName:    adminName,
                command:      fullName,
                coordinates:  player != null ? Coordinates.From(player.transform.position) : null,
                details:      new { action });
        }

        // ----------------------------- Helpers -----------------------------

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
            {
                int days  = (int)ts.TotalDays;
                int hours = ts.Hours;
                return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
            }
            if (ts.TotalHours >= 1)
            {
                int hours   = (int)ts.TotalHours;
                int minutes = ts.Minutes;
                return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
            }
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m";
            return $"{Math.Max(1, (int)ts.TotalSeconds)}s";
        }

        private void EnqueueTeamEvent(string eventType, RelationshipManager.PlayerTeam team, string teamLeader = null, string targetPlayer = null)
        {
            var members = new List<string>();
            foreach (var id in team.members)
                members.Add(id.ToString());
            if (members.Count > 100)
                members = members.GetRange(0, 100);

            Enqueue("/ingest/teaminfo", new TeamInfoPayload
            {
                EventType    = eventType,
                TeamLeader   = teamLeader ?? team.teamLeader.ToString(),
                TeamMembers  = members,
                TargetPlayer = targetPlayer,
                EventTime    = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
            });
        }

        private void TrackPendingAdminAction(string cmdName, string[] args, string adminId, string adminName)
        {
            if (string.IsNullOrEmpty(adminId) || args == null || args.Length == 0) return;
            string targetArg = args[0].Trim();
            if (string.IsNullOrEmpty(targetArg)) return;

            Dictionary<string, (string, string)> dict;
            switch (cmdName)
            {
                case "ban":
                case "banid":
                case "ban.steamid":
                    dict = _pendingBanAdmin; break;
                case "kick":
                case "kickid":
                    dict = _pendingKickAdmin; break;
                case "unban":
                    dict = _pendingUnbanAdmin; break;
                default:
                    return;
            }

            var admin = (adminId, adminName);
            // Store by the raw typed arg (may be a name)
            dict[targetArg] = admin;
            // Also store by resolved Steam ID so outcome hooks that look up by ID always find it
            var resolved = covalence.Players.FindPlayer(targetArg);
            if (resolved != null && resolved.Id != targetArg)
                dict[resolved.Id] = admin;
        }

        private static string BuildChatCommand(string name, string[] args)
        {
            return args != null && args.Length > 0
                ? $"/{name} {string.Join(" ", args)}"
                : $"/{name}";
        }

        private void EnqueueServerLog(string eventType, string adminSteamId = null, string adminName = null,
            string targetSteamId = null, string targetName = null, string command = null,
            Coordinates coordinates = null, object details = null)
        {
            Enqueue("/ingest/server-log", new ServerLogPayload
            {
                EventType     = eventType,
                AdminSteamId  = adminSteamId  != null ? Truncate(adminSteamId,  64)  : null,
                AdminName     = adminName     != null ? Truncate(adminName,     128) : null,
                TargetSteamId = targetSteamId != null ? Truncate(targetSteamId,  64) : null,
                TargetName    = targetName    != null ? Truncate(targetName,    128) : null,
                Command       = command       != null ? Truncate(command,       500) : null,
                Coords        = coordinates,
                Details       = details
            });
        }

        private static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) : s;
        }

        private void Log(string msg)
        {
            if (_enableLogs) Puts(msg);
        }

        // ----------------------------- Queue -----------------------------

        private void Enqueue<T>(string path, T payload)
        {
            if (_queue.Count >= MaxQueued)
            {
                _queue.Dequeue();
                Puts("Queue full — dropping oldest entry.");
            }

            var json = JsonConvert.SerializeObject(payload);
            _queue.Enqueue(new QueueItem { Path = path, Json = json });
            TrySend();
        }

        private void TrySend()
        {
            if (_sending || _queue.Count == 0) return;
            _sending = true;

            var item    = _queue.Peek();
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"]  = "application/json",
                ["Authorization"] = $"Bearer {ApiToken}",
                ["x-api-key"]     = ApiToken
            };

            var url = $"{ApiUrl}{item.Path}";

            webrequest.Enqueue(url, item.Json, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    //Puts($"API send failed (HTTP {code}) — retrying in {RetryDelay}s");
                    Log($"Response ({code}): {response}");
                    _sending = false;
                    timer.Once(RetryDelay, TrySend);
                    return;
                }

                Log($"Response ({code}): {response}");
                _queue.Dequeue();
                _sending = false;
                TrySend();
            }, this, RequestMethod.POST, headers);
        }
    }
}
