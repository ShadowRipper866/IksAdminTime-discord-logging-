using Dapper;
using IksAdminApi;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace IksAdminTime
{
    public class DataBaseService
    {
        private readonly ILogger<DataBaseService> _logger;
        private readonly string _connectionString;

        public DataBaseService(IIksAdminApi api)
        {
            _connectionString = api.DbConnectionString;
            ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<DataBaseService>();
        }

        public async Task InitializeDatabase()
        {
            try
            {
                await using MySqlConnection connection = await GetOpenConnectionAsync();
                await CreateTablesAsync(connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed");
                throw;
            }
        }

        private async Task CreateTablesAsync(MySqlConnection connection)
        {
            const string createTableQuery = @"
CREATE TABLE IF NOT EXISTS `iks_admin_time` (
    `id` INT PRIMARY KEY AUTO_INCREMENT,
    `admin_id` VARCHAR(32) NOT NULL,
    `admin_name` VARCHAR(64) NOT NULL,
    `connect_time` INT NOT NULL,
    `disconnect_time` INT NOT NULL DEFAULT -1 COMMENT '-1 = Admin online',
    `played_time` INT NOT NULL DEFAULT 0,
    `spectator_time` INT NOT NULL DEFAULT 0,
    `server_id` VARCHAR(32) NOT NULL,
    UNIQUE KEY `uk_admin_server` (`admin_id`, `server_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

            _ = await connection.ExecuteAsync(createTableQuery);
        }

        public async Task OnAdminConnect(ulong steamId, string name, int serverId)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();

            const string query = @"
INSERT INTO `iks_admin_time` 
    (admin_id, admin_name, connect_time, server_id)
VALUES 
    (@SteamId, @Name, @Time, @ServerId)
ON DUPLICATE KEY UPDATE 
    admin_name = VALUES(admin_name),
    connect_time = VALUES(connect_time),
    disconnect_time = -1;";

            _ = await connection.ExecuteAsync(query, new
            {
                SteamId = steamId,
                Name = name,
                Time = GetCurrentUnixTime(),
                ServerId = serverId
            });
        }

        public async Task OnAdminDisconnect(string steamId, int serverId)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();

            const string query = @"
UPDATE `iks_admin_time`
SET 
    disconnect_time = @Time,
    played_time = played_time + (@Time - connect_time)
WHERE 
    admin_id = @SteamId 
    AND server_id = @ServerId
    AND disconnect_time = -1;";

            _ = await connection.ExecuteAsync(query, new
            {
                SteamId = steamId,
                ServerId = serverId,
                Time = GetCurrentUnixTime()
            });
        }

        public async Task AddSpectatorTimeAsync(string steamId, int serverId, int duration)
        {
            await using MySqlConnection connection = await GetOpenConnectionAsync();

            const string query = @"
UPDATE `iks_admin_time`
SET spectator_time = spectator_time + @Duration
WHERE admin_id = @SteamId AND server_id = @ServerId;";

            _ = await connection.ExecuteAsync(query, new
            {
                SteamId = steamId,
                ServerId = serverId,
                Duration = duration
            });
        }

        private async Task<MySqlConnection> GetOpenConnectionAsync()
        {
            MySqlConnection connection = new(_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        private static int GetCurrentUnixTime()
        {
            return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}