using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using IksAdminApi;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace IksAdminTime
{
    public partial class IksAdminTime : BasePlugin, IPluginConfig<IksAdminTimeConfig>
    {
        public IksAdminTimeConfig Config { get; set; } = new();
        internal static DataBaseService? _dataBaseService;

        public override string ModuleName => "[IKS] Admin Time [MOD]";
        public override string ModuleAuthor => "E!N & ShadowRipper";
        public override string ModuleVersion => "v1.1";

        private readonly IIksAdminApi _adminApi = AdminModule.Api;
        private int _serverId;
        private readonly ConcurrentDictionary<string, int> _spectatorJoinTime = new();
        private readonly ConcurrentDictionary<string, (DateTime JoinTime, string Name, int SpecTime)> _adminJoinTimes = new();
        public override void OnAllPluginsLoaded(bool hotReload)
        {
            _adminApi.OnFullConnect += OnFullConnect;
            RegisterListener<Listeners.OnClientDisconnect>(OnPlayerDisconnect);
            RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamChange);
        }

        public async void OnConfigParsed(IksAdminTimeConfig config)
        {
            Config = config;
            _serverId = config.ServerID;
            _dataBaseService = new DataBaseService(_adminApi);
            await _dataBaseService.InitializeDatabase();

            if (string.IsNullOrEmpty(Config.DiscordWebHookUrl))
            {
                Logger.LogWarning("Discord webhook URL is not configured. Discord notifications will be disabled.");
            }
        }

        private HookResult OnPlayerTeamChange(EventPlayerTeam @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;
            if (player == null || !player.IsValid || !AdminUtils.IsAdmin(player))
                return HookResult.Continue;

            string steamId = player.GetSteamId();
            if (string.IsNullOrEmpty(steamId))
                return HookResult.Continue;

            CsTeam newTeam = (CsTeam)@event.Team;
            CsTeam oldTeam = (CsTeam)@event.Oldteam;

            if (newTeam == CsTeam.Spectator)
            {
                _spectatorJoinTime[steamId] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else if (oldTeam == CsTeam.Spectator)
            {
                if (_spectatorJoinTime.TryRemove(steamId, out int joinTime))
                {
                    int duration = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - joinTime;

                    // Обновляем общее время в спектаторах за сессию
                    _adminJoinTimes.AddOrUpdate(steamId,
                        (DateTime.UtcNow, player.PlayerName, duration),
                        (key, oldValue) => (
                            oldValue.JoinTime,
                            oldValue.Name,
                            oldValue.SpecTime + duration
                        ));
                }
            }
            return HookResult.Continue;
        }

        private async void OnFullConnect(string steamId, string ip)
        {
            if (!ulong.TryParse(steamId, out ulong steamId64))
            {
                return;
            }

            if (_adminApi.ServerAdmins.TryGetValue(steamId64, out Admin? admin))
            {
                await _dataBaseService!.OnAdminConnect(steamId64, admin.CurrentName, _serverId);
                _adminJoinTimes.TryAdd(steamId,
                    (JoinTime: DateTime.UtcNow,
                     Name: admin.CurrentName,
                     SpecTime: 0)
                );

                await SendDiscordNotificationAsync(admin.CurrentName, steamId, true);
            }
        }

        private async void OnPlayerDisconnect(int playerSlot)
        {
            try
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
                if (player == null || !player.IsValid || !AdminUtils.IsAdmin(player))
                {
                    return;
                }

                string steamId = player.GetSteamId();
                if (steamId == null)
                {
                    return;  
                }

                await _dataBaseService!.OnAdminDisconnect(steamId, _serverId);

                if (_spectatorJoinTime.TryRemove(steamId, out int specJoinTime))
                {
                    int duration = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - specJoinTime;
                    await _dataBaseService.AddSpectatorTimeAsync(steamId, _serverId, duration);
                }

                if (_adminJoinTimes.TryRemove(steamId, out var joinData))
                {
                    var sessionDuration = DateTime.UtcNow - joinData.JoinTime;
                    int specSeconds = joinData.SpecTime;
                    await SendDiscordNotificationAsync(joinData.Name, steamId, false, sessionDuration, specSeconds);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"OnPlayerDisconnect: {ex.Message}");
            }
        }
        private async Task SendDiscordNotificationAsync(
            string adminName,
            string steamId,
            bool isConnect,
            TimeSpan? duration = null,
            int specSec = 0)
        {
            if (_adminJoinTimes.TryGetValue(steamId, out var sessionData))
            {
                specSec = sessionData.SpecTime;
            }
            if (string.IsNullOrEmpty(Config.DiscordWebHookUrl)) return;

            try
            {
                using var httpClient = new HttpClient();
                string spectatorTime = TimeSpan.FromSeconds(specSec).ToString(@"hh\:mm\:ss");
                string durationText = "Недоступно";
                if (duration.HasValue)
                {
                    durationText = $"{duration.Value.Hours} ч. {duration.Value.Minutes} мин. {duration.Value.Seconds} сек.";
                }
                var embed = new
                {
                    title = "📊 Статистика администратора",
                    color = isConnect ? 0x43B581 : 0xF04747,
                    author = new{name = adminName,url = $"https://steamcommunity.com/profiles/{ConvertToSteamId64(steamId)}"},
                    thumbnail = new { url = $"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/{GetSteamAvatarHash(steamId)}" },
                    fields = new[]
                    {
                new { name = "🕒 Статус", value = isConnect ? "✅ Подключился" : "❌ Отключился", inline = false },
                new { name = "🌐 Сервер", value = Config.ServerName, inline = false },
                new { name = "⏳ Время сессии", value = durationText, inline = true },
                new { name = "👀 В спектаторах", value = spectatorTime, inline = true }
            },
                    footer = new { text = $"Время события: {DateTime.UtcNow.AddHours(3):dd.MM.yyyy HH:mm:ss} По Киеву" }
                };

                var payload = new { embeds = new[] { embed } };
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await httpClient.PostAsync(Config.DiscordWebHookUrl, content);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка Discord: {ex.Message}");
            }
        }
        private string ConvertToSteamId64(string steamId)
        {
            if (ulong.TryParse(steamId, out ulong steamId64))
                return steamId64.ToString();
            return steamId;
        }

        private string GetSteamAvatarHash(string steamId)
        {
            return ConvertToSteamId64(steamId).TakeLast(8).Aggregate("", (s, c) => s + c) + "_full.jpg";
        }
    }
}