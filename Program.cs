using Discord;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

public class Program
{
    private static readonly CancellationTokenSource _cancellationTokenSource = new();
    static void Main(string[] args)
    {
        Console.WriteLine("Starting...");

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; 
            Log.Information("Shutdown requested.");
            _cancellationTokenSource.Cancel();
        };

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                path: "logs/PuckGtaxBot-log-.txt",
                rollingInterval: RollingInterval.Day, 
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Console.TreatControlCAsInput = false;

        try
        {
            // --- Dependency Injection container ---
            var services = new ServiceCollection();

            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();
            services.AddSingleton<IConfiguration>(config);

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });

            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<GoalieTaxBot>>();

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    logger.LogInformation("Starting PuckGtaxBot...");
                    var bot = new GoalieTaxBot(logger);

                    bot.RunAsync(
                        config["Discord:Token"],
                        _cancellationTokenSource.Token
                    )
                    .GetAwaiter()
                    .GetResult();

                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        logger.LogWarning("Bot exited unexpectedly — restarting in 5 seconds...");
                        Task.Delay(5000, _cancellationTokenSource.Token)
                            .GetAwaiter()
                            .GetResult();
                    }
                }
                catch (TaskCanceledException)
                {
                    logger.LogInformation("Shutdown requested — stopping main loop.");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Fatal error in bot loop — restarting in 5 seconds");
                    Task.Delay(5000, _cancellationTokenSource.Token)
                        .GetAwaiter()
                        .GetResult();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.Information("Shutdown complete. Closing logs...");
            Log.CloseAndFlush();
        }
    }
}

public class GoalieTaxBot
{
    private DiscordSocketClient _client;
    private readonly ILogger<GoalieTaxBot> _logger;
    private Dictionary<ulong, GuildConfig> _guildConfigs = new();
    private Dictionary<ulong, MatchInfo> _matchInfo = new();
    private const string ConfigPath = "guildConfig.json";

    public GoalieTaxBot(ILogger<GoalieTaxBot> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string botToken, CancellationToken cancellationToken)
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Verbose,
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
        });

        _client.Log += LogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += HandleSlashCommandAsync;
        _client.ButtonExecuted += HandleButtonAsync;

        LoadConfig();
        try
        {
            await _client.LoginAsync(TokenType.Bot, botToken);
            await _client.StartAsync();

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            
        }
        finally
        {
            _logger.LogInformation("Stopping Discord client...");

            try
            {
                await _client.StopAsync();
            } catch { }
            
            _client.Dispose();
            _logger.LogInformation("Discord client disposed. Shutdown complete.");
        }   
    }

    private Task LogAsync(LogMessage logMessage)
    {
        // Convert Discord.Net's LogSeverity to Microsoft's LogLevel
        var logLevel = logMessage.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };

        _logger.Log(
            logLevel,
            logMessage.Exception,
            "[{Source}] {Message}",
            logMessage.Source,
            logMessage.Message);
        
        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        Console.WriteLine($"✅ Logged in as {_client.CurrentUser}");

        // Register slash commands in every guild
        foreach (var guild in _client.Guilds)
        {
            await RegisterCommands(guild);
        }

        // Restore persistent queue messages
        foreach (var kv in _guildConfigs)
        {
            var guildId = kv.Key;
            var cfg = kv.Value;
            var guild = _client.GetGuild(guildId);
            if (guild == null) continue;
        }
    }

    private async Task RegisterCommands(SocketGuild guild)
    {
        // Uncomment below to remove deprecated slash commands
        //await guild.DeleteApplicationCommandsAsync();

        var setup = new SlashCommandBuilder()
            .WithName("setup")
            .WithDescription("Setup the hockey bot queue channel")
            .AddOption("channel", ApplicationCommandOptionType.Channel, "Channel to host the queue", true);

        var purge = new SlashCommandBuilder()
            .WithName("purge")
            .WithDescription("Purge channel messages")
            .AddOption("amount", ApplicationCommandOptionType.Number, "Amount of messages to purge", true);

        var generatetax = new SlashCommandBuilder()
            .WithName("generatetax")
            .WithDescription("Generate gtax from latest match info");

        try
        {
            await guild.CreateApplicationCommandAsync(setup.Build());
            await guild.CreateApplicationCommandAsync(purge.Build());
            await guild.CreateApplicationCommandAsync(generatetax.Build());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error registering commands in {guild.Name}: {ex.Message}");
        }
    }

    private async Task HandleSlashCommandAsync(SocketSlashCommand cmd)
    {
        if (cmd.GuildId == null) return;
        var guildId = cmd.GuildId.Value;

        var user = (SocketGuildUser)cmd.User;
        var hasPerms = user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator;

        if (cmd.CommandName == "setup")
        {
            if (!hasPerms) { await cmd.RespondAsync("No permission."); return; }
            var channel = (SocketTextChannel)cmd.Data.Options.First().Value;
            if (!_guildConfigs.ContainsKey(guildId))
                _guildConfigs[guildId] = new GuildConfig { StatsChannelId = channel.Id };
            _guildConfigs[guildId].StatsChannelId = channel.Id;
            SaveConfig();
            await cmd.RespondAsync($"Setup complete. Goalie tax will read from {channel.Mention}.");
        }

        if (cmd.CommandName == "purge")
        {
            if (!hasPerms) { await cmd.RespondAsync("No permission."); return; }
            await cmd.DeferAsync(ephemeral: true);
            var amount = Convert.ToInt32(cmd.Data.Options.First().Value);
            if (amount < 1 || amount > 100)
            {
                await cmd.FollowupAsync("You can only purge between 1 and 100 messages at a time.", ephemeral: true);
                return;
            }
            var channel = (SocketTextChannel)cmd.Channel;

            var messages = await channel.GetMessagesAsync(amount).FlattenAsync();
            await (channel as ITextChannel).DeleteMessagesAsync(messages);

            await cmd.FollowupAsync($"Successfully purged {messages.Count()} messages.", ephemeral: true);
        }

        if (cmd.CommandName == "generatetax")
        {
            if (_guildConfigs[guildId].StatsChannelId == 0) { await cmd.RespondAsync("Setup a channel first."); return; }

            var channel = (SocketTextChannel)_client.GetChannel(_guildConfigs[guildId].StatsChannelId);
            var messages = await channel.GetMessagesAsync(10).FlattenAsync();

            IMessage matchMessage = null;
            foreach (var msg in messages)
            {
                if (msg.Embeds.Count == 0)
                    continue;

                var embed = msg.Embeds.First();
                if (embed.Fields.Any(f => f.Name.Contains("Red", StringComparison.OrdinalIgnoreCase)) &&
                    embed.Fields.Any(f => f.Name.Contains("Blue", StringComparison.OrdinalIgnoreCase)))
                {
                    matchMessage = msg;
                    break; // found a valid match
                }
            }

            if (matchMessage == null) { await cmd.RespondAsync("No messages found."); return; }

            string queueId = null;
            if (!string.IsNullOrEmpty(matchMessage.Embeds.First().Title))
            {
                var match = Regex.Match(matchMessage.Embeds.First().Title, @"Queue#(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                    queueId = match.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(queueId))
                await cmd.RespondAsync("/generatetax failed", ephemeral: true);

            var rotationEmbed = GenerateGoalieRotation(matchMessage.Embeds, queueId);
            await cmd.RespondAsync(embed: rotationEmbed, components: BuildButtons().Build());

            var matchChannel = _client.GetGuild(guildId).Channels.OfType<ITextChannel>()
                .SingleOrDefault(c => c.Name.Equals($"queue-{queueId}", System.StringComparison.OrdinalIgnoreCase));
            if (matchChannel != null && cmd.Channel.Id != matchChannel.Id)
                await matchChannel.SendMessageAsync(embed: rotationEmbed, components: BuildButtons().Build());
        }
    }

    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        if (component.GuildId == null) return;
        var guildId = component.GuildId.Value;
        var userId = component.User.Id;

        switch (component.Data.CustomId)
        {
            case "goalie":
                MatchInfo liveMatch = GetMatchInfo(_matchInfo.Keys.Max());
                if (!liveMatch.Players.ContainsKey(userId))
                    await component.RespondAsync("You aren't in this match", ephemeral: true);
                else
                {
                    var rotationEmbed = DeclareGoalie(liveMatch, userId);
                    await component.RespondAsync(embed: rotationEmbed, components: BuildButtons().Build());
                    var matchChannel = _client.GetGuild(guildId).Channels.OfType<ITextChannel>()
                        .SingleOrDefault(c => c.Name.Equals($"queue-{liveMatch.MatchId}", System.StringComparison.OrdinalIgnoreCase));
                    if (matchChannel != null && component.Channel.Id != matchChannel.Id)
                        await matchChannel.SendMessageAsync(embed: rotationEmbed, components: BuildButtons().Build());
                }
                break;
            case "reroll":
                await component.RespondAsync("Fuck off 🖕", ephemeral: true);
                break;
        }
    }

    private Embed GenerateGoalieRotation(IReadOnlyCollection<IEmbed> embeds, string queueId)
    {
        ulong ulongQueueId;
        ulong.TryParse(queueId, out ulongQueueId);

        MatchInfo currentMatch = GetMatchInfo(ulongQueueId);
        if (currentMatch.GoalieRotation != null)
            return BuildMatchEmbed(currentMatch);

        var redField = embeds.FirstOrDefault().Fields.FirstOrDefault(f => f.Name.Contains("Red"));
        var blueField = embeds.FirstOrDefault().Fields.FirstOrDefault(f => f.Name.Contains("Blue"));
        var redLines = redField.Value.Split('\n') ?? Array.Empty<string>();
        var blueLines = blueField.Value.Split('\n') ?? Array.Empty<string>();

        var redPlayers = new List<string>();
        var bluePlayers = new List<string>();
        var redGoalies = new List<string>();
        var blueGoalies = new List<string>();

        foreach (var line in redLines)
        {

            var match = Regex.Match(line, @"<@?\w+>.*\((Goalie|Skater)\)");
            if (!match.Success) continue;
            redPlayers.Add(match.Value);
            if (line.Contains("(Goalie)")) redGoalies.Add(match.Value);
        }
        foreach (var line in blueLines)
        {
            var match = Regex.Match(line, @"<@?\w+>.*\((Goalie|Skater)\)");
            if (!match.Success) continue;
            bluePlayers.Add(match.Value);
            if (line.Contains("(Goalie)")) blueGoalies.Add(match.Value);
        }

        foreach (var player in redPlayers)
        {
            var match = Regex.Match(player, @"<@!?(\d+)>");
            if (!match.Success) continue;
            if (ulong.TryParse(match.Groups[1].Value, out var userId))
                currentMatch.Players.Add(userId, "red");
        }
        
        foreach (var player in bluePlayers)
        {
            var match = Regex.Match(player, @"<@!?(\d+)>");
            if (!match.Success) continue;
             if (ulong.TryParse(match.Groups[1].Value, out var userId))
                currentMatch.Players.Add(userId, "blue");
        }

        var rnd = new Random();
        var goalieRotation = new Dictionary<int, (string Blue, string Red)>();
        goalieRotation[1] = (blueGoalies[0], redGoalies[0]);
        for (int period = 2; period <= 4; period++)
        {
            string blueG = bluePlayers.Where(l => !l.Contains("(Goalie)")).OrderBy(_ => rnd.Next()).First();
            bluePlayers.Remove(blueG);
            string redG = redPlayers.Where(l => !l.Contains("(Goalie)")).OrderBy(_ => rnd.Next()).First();
            redPlayers.Remove(redG);
            goalieRotation[period] = (blueG, redG);
        }

        currentMatch.GoalieRotation = goalieRotation;
        return BuildMatchEmbed(currentMatch);
    }

    private Embed DeclareGoalie(MatchInfo match, ulong userId)
    {
        var declaringGoalie = match.Players.SingleOrDefault(p => p.Key == userId);
        for (int period = 1; period <= 4; period++)
        {
            if (declaringGoalie.Value == "blue")
                match.GoalieRotation[period] = ($"<@!{declaringGoalie.Key}> (Declared Goalie)", match.GoalieRotation[period].Red);
            else if (declaringGoalie.Value == "red")
                match.GoalieRotation[period] = (match.GoalieRotation[period].Blue, $"<@!{declaringGoalie.Key}> (Declared Goalie)");
            else
                continue;
        }
        return BuildMatchEmbed(match);
    }
    
    private Embed BuildMatchEmbed(MatchInfo match)
    {
        var eb = new EmbedBuilder()
            .WithTitle("🧤 Goalie Tax Rotation")
            .WithDescription($"Queue#{match.MatchId}")
            .WithColor(Color.Blue);

        foreach (var kv in match.GoalieRotation)
            eb.AddField($"Period {(kv.Key == 4 ? "OT" : kv.Key)}", $"🔵 {kv.Value.Blue}\n🔴 {kv.Value.Red}");

        return eb.Build();
    }

    private ComponentBuilder BuildButtons() =>
        new ComponentBuilder()
            .WithButton("🧤 Declare Goalie", "goalie", ButtonStyle.Primary)
            .WithButton("🎲 Reroll Gtax", "reroll", ButtonStyle.Secondary);
            

    private MatchInfo GetMatchInfo(ulong matchId)
    {
        if (!_matchInfo.ContainsKey(matchId))
            _matchInfo[matchId] = new MatchInfo();
            _matchInfo[matchId].MatchId = matchId;
        return _matchInfo[matchId];
    }

    private void SaveConfig() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_guildConfigs, new JsonSerializerOptions { WriteIndented = true }));
    private void LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            _guildConfigs = JsonSerializer.Deserialize<Dictionary<ulong, GuildConfig>>(File.ReadAllText(ConfigPath)) ?? new();
        }
    }
}

public class MatchInfo
{
    public ulong MatchId { get; set; } = new();
    public Dictionary<ulong, string> Players { get; set; } = new();
    public Dictionary<int, (string Blue, string Red)> GoalieRotation { get; set; } = null;
}

public class GuildConfig
{
    public ulong StatsChannelId { get; set; }
}