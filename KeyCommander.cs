using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Core.Translations;
using System.Text.Json.Serialization;
using MySqlConnector;
using Dapper;
using System;
using Microsoft.Extensions.Logging;

namespace KeyCommander;

public class KeyConfig : BasePluginConfig
{
    [JsonPropertyName("CommandGroups")]
    public Dictionary<string, List<string>> CommandGroups { get; set; } = new()
    {
        ["VIP"] = new List<string>
        {
            "vip_remove {userid}",
            "vip_give \"{userid}\" \"{time}\" \"{item_group}\""
            
        }
    };

    [JsonPropertyName("DatabaseHost")]
    public string DatabaseHost { get; set; } = "";

    [JsonPropertyName("DatabasePort")]
    public int DatabasePort { get; set; } = 3306;

    [JsonPropertyName("DatabaseUser")]
    public string DatabaseUser { get; set; } = "";

    [JsonPropertyName("DatabasePassword")]
    public string DatabasePassword { get; set; } = "";

    [JsonPropertyName("DatabaseName")]
    public string DatabaseName { get; set; } = "";
}

public class KeyCommanderPlugin : BasePlugin, IPluginConfig<KeyConfig>
{
    public override string ModuleName => "KeyCommander";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "DoctorishHD";
    public override string ModuleDescription => "Key generation and activation plugin with command groups";

    public KeyConfig Config { get; set; } = new();

    private string _dbConnectionString = string.Empty;

    public void OnConfigParsed(KeyConfig config)
    {
        Config = config;
        if (Config.DatabaseHost.Length < 1 || Config.DatabaseName.Length < 1 || Config.DatabaseUser.Length < 1)
        {
            Logger.LogError("Database configuration is incomplete. Plugin will not connect to database.");
            return;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = Config.DatabaseHost,
            Database = Config.DatabaseName,
            UserID = Config.DatabaseUser,
            Password = Config.DatabasePassword,
            Port = (uint)Config.DatabasePort,
        };
        _dbConnectionString = builder.ConnectionString;

        Task.Run(async () =>
        {
            try
            {
                await using var connection = new MySqlConnection(_dbConnectionString);
                await connection.OpenAsync();
                // создать таблицу если нет
                string createTable = @"CREATE TABLE IF NOT EXISTS `keys` (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    key_string VARCHAR(50) UNIQUE,
                    time_seconds INT,
                    group_name VARCHAR(50),
                    item_group VARCHAR(50) DEFAULT '',
                    activated TINYINT DEFAULT 0,
                    activated_by VARCHAR(64) NULL,
                    activated_at DATETIME NULL
                )";
                await connection.ExecuteAsync(createTable);
                // добавить столбцы если их нет (игнорируем ошибки)
                try { await connection.ExecuteAsync("ALTER TABLE `keys` ADD COLUMN activated TINYINT DEFAULT 0"); } catch { }
                try { await connection.ExecuteAsync("ALTER TABLE `keys` ADD COLUMN activated_by VARCHAR(64) NULL"); } catch { }
                try { await connection.ExecuteAsync("ALTER TABLE `keys` ADD COLUMN activated_at DATETIME NULL"); } catch { }
                try { await connection.ExecuteAsync("ALTER TABLE `keys` ADD COLUMN item_group VARCHAR(50) DEFAULT ''"); } catch { }
                Logger.LogInformation("Database connection established and table verified.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to connect to database: {ex.Message}");
            }
        });
    }

    public override void Load(bool hotReload)
    {
        Logger.LogInformation(Localizer["Key.Loaded"]);
    }

    public override void Unload(bool hotReload)
    {
        // соединения закрываются автоматически благодаря await using
    }

    private static string GenerateKey(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    [ConsoleCommand("css_addkey", "Generate keys")]
    [CommandHelper(minArgs: 4, usage: "[count] [configGroup] [time] [itemGroup]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnAddKey(CCSPlayerController? player, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(1), out int count) || count <= 0)
        {
            Logger.LogError("Invalid count parameter");
            return;
        }
        string configGroup = command.ArgByIndex(2);
        if (string.IsNullOrEmpty(configGroup))
        {
            Logger.LogError("Invalid config group parameter");
            return;
        }
        if (!int.TryParse(command.ArgByIndex(3), out int time) || time < 0)
        {
            Logger.LogError("Invalid time parameter (must be >= 0)");
            return;
        }
        string itemGroup = command.ArgByIndex(4);
        if (string.IsNullOrEmpty(itemGroup))
        {
            Logger.LogError("Invalid item group parameter");
            return;
        }

        if (string.IsNullOrEmpty(_dbConnectionString))
        {
            Logger.LogError("Database connection is not initialized");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await using var connection = new MySqlConnection(_dbConnectionString);
                await connection.OpenAsync();
                for (int i = 0; i < count; i++)
                {
                    string key = GenerateKey(32);
                    await connection.ExecuteAsync(
                        "INSERT INTO `keys` (key_string, time_seconds, group_name, item_group) VALUES (@key, @time, @configGroup, @itemGroup)",
                        new { key, time, configGroup, itemGroup });
                    Logger.LogInformation(Localizer["Key.Generated", key]);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to generate keys: {ex.Message}");
            }
        });
    }

    [ConsoleCommand("css_key", "Activate key")]
    [CommandHelper(minArgs: 1, usage: "[key]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnActivateKey(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        string key = command.ArgByIndex(1);
        if (string.IsNullOrEmpty(key))
        {
            player.PrintToChat(Localizer["Key.Invalid"]);
            return;
        }

        if (string.IsNullOrEmpty(_dbConnectionString))
        {
            player.PrintToChat(Localizer["Key.DatabaseNotInitialized"]);
            return;
        }

        // захватываем данные игрока в главном потоке
        string userid = player.UserId.ToString();
        string steamid = player.SteamID.ToString();
        string accountid = player.AuthorizedSteamID?.AccountId.ToString() ?? "0";
        string nickname = player.PlayerName;
        var playerSlot = player.Slot; // для возможной будущей проверки

        Task.Run(async () =>
        {
            try
            {
                await using var connection = new MySqlConnection(_dbConnectionString);
                await connection.OpenAsync();
                var result = await connection.QueryFirstOrDefaultAsync<(int time_seconds, string group_name, string item_group)?>(
                    "SELECT time_seconds, group_name, item_group FROM `keys` WHERE key_string = @key AND activated = 0",
                    new { key });
                if (result.HasValue)
                {
                    int time = result.Value.time_seconds;
                    string configGroup = result.Value.group_name;
                    string itemGroup = result.Value.item_group ?? "";
                    // обновить ключ как активированный
                    await connection.ExecuteAsync(
                        "UPDATE `keys` SET activated = 1, activated_by = @steamid, activated_at = NOW() WHERE key_string = @key",
                        new { steamid, key });
                    // логируем значения для отладки
                    Logger.LogInformation($"Debug: userid={userid}, steamid={steamid}, accountid={accountid}, nickname={nickname}, configGroup={configGroup}, itemGroup={itemGroup}");
                    
                    // получить команды из группы конфига
                    if (!Config.CommandGroups.TryGetValue(configGroup, out var commands) || commands == null || commands.Count == 0)
                    {
                        Logger.LogError($"No commands defined for config group '{configGroup}'");
                        Server.NextFrame(() =>
                        {
                            var target = Utilities.GetPlayerFromSlot(playerSlot);
                            if (target != null && target.IsValid)
                                target.PrintToChat(Localizer["Key.ConfigGroupNotFound"]);
                        });
                        return;
                    }

                    // подготовить словарь замен плейсхолдеров
                    var replacements = new Dictionary<string, string>
                    {
                        ["{userid}"] = userid,
                        ["{steamid}"] = steamid,
                        ["{accountid}"] = accountid,
                        ["{nickname}"] = nickname,
                        ["{time}"] = time.ToString(),
                        ["{group}"] = configGroup,
                        ["{item_group}"] = itemGroup
                    };

                    // выполнить команды на основном потоке
                    Server.NextFrame(() =>
                    {
                        var target = Utilities.GetPlayerFromSlot(playerSlot);
                        if (target != null && target.IsValid)
                        {
                            foreach (var cmdTemplate in commands)
                            {
                                string finalCmd = cmdTemplate;
                                foreach (var kv in replacements)
                                    finalCmd = finalCmd.Replace(kv.Key, kv.Value);
                                Logger.LogInformation($"Executing command: {finalCmd}");
                                Server.ExecuteCommand(finalCmd);
                            }
                            target.PrintToChat(Localizer["Key.ActivatedWithGroup", key, configGroup, time]);
                        }
                        else
                        {
                            Logger.LogWarning("Player left before activation");
                        }
                    });
                }
                else
                {
                    Server.NextFrame(() =>
                    {
                        var target = Utilities.GetPlayerFromSlot(playerSlot);
                        if (target != null && target.IsValid)
                            target.PrintToChat(Localizer["Key.InvalidOrActivated"]);
                    });
                }
            
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to activate key: {ex.Message}");
                Server.NextFrame(() =>
                {
                    var target = Utilities.GetPlayerFromSlot(playerSlot);
                    if (target != null && target.IsValid)
                        target.PrintToChat(Localizer["Key.Error"]);
                });
            }
        });
    }

    [ConsoleCommand("css_keydel", "Delete keys by group name")]
    [CommandHelper(minArgs: 1, usage: "[groupName]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnDeleteKeys(CCSPlayerController? player, CommandInfo command)
    {
        string groupName = command.ArgByIndex(1);
        if (string.IsNullOrEmpty(groupName))
        {
            command.ReplyToCommand(Localizer["Key.DeleteMissingGroup"]);
            return;
        }

        if (string.IsNullOrEmpty(_dbConnectionString))
        {
            command.ReplyToCommand(Localizer["Key.DatabaseNotInitialized"]);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await using var connection = new MySqlConnection(_dbConnectionString);
                await connection.OpenAsync();
                int deleted = await connection.ExecuteAsync(
                    "DELETE FROM `keys` WHERE group_name = @groupName",
                    new { groupName });
                if (deleted > 0)
                {
                    command.ReplyToCommand(Localizer["Key.DeleteSuccess", deleted, groupName]);
                    Logger.LogInformation($"Deleted {deleted} keys with group '{groupName}'");
                }
                else
                {
                    command.ReplyToCommand(Localizer["Key.DeleteNoKeys", groupName]);
                }
            }
            catch (Exception ex)
            {
                command.ReplyToCommand(Localizer["Key.DeleteError", ex.Message]);
                Logger.LogError($"Failed to delete keys: {ex.Message}");
            }
        });
    }
}
