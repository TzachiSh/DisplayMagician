using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace DisplayMagicianService;

public class TelegramBotWorker : BackgroundService
{
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly ProfileService _profileService;
    private readonly SessionMonitor _sessionMonitor;
    private TelegramBotClient? _bot;
    private readonly HashSet<long> _allowedUsers = new();
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<long, (int count, DateTimeOffset window)> _rateLimits = new();
    private const int MaxRequestsPerMinute = 10;

    private readonly List<UserProfileConfig> _userProfiles = new();

    public TelegramBotWorker(ILogger<TelegramBotWorker> logger, ProfileService profileService,
        SessionMonitor sessionMonitor, IConfiguration config)
    {
        _logger = logger;
        _profileService = profileService;
        _sessionMonitor = sessionMonitor;
        _config = config;

        // Load user profiles from config
        var section = config.GetSection("UserProfiles");
        if (section.Exists())
        {
            foreach (var child in section.GetChildren())
            {
                _userProfiles.Add(new UserProfileConfig
                {
                    Name = child["Name"] ?? "",
                    WindowsUser = child["WindowsUser"] ?? "",
                    DisplayProfile = child["DisplayProfile"] ?? "",
                    LaunchApp = child["LaunchApp"] ?? "",
                    CloseApps = bool.TryParse(child["CloseApps"], out var ca) && ca,
                    LogoutUser = bool.TryParse(child["LogoutUser"], out var lu) && lu,
                    LogoutTarget = child["LogoutTarget"] ?? "",
                    RevertDisplayProfile = child["RevertDisplayProfile"] ?? "",
                    HasPassword = bool.TryParse(child["HasPassword"], out var hp) && hp
                });
            }
            _logger.LogInformation("Loaded {Count} user profiles from config", _userProfiles.Count);
        }
    }

    private class UserProfileConfig
    {
        public string Name { get; set; } = "";
        public string WindowsUser { get; set; } = "";
        public string DisplayProfile { get; set; } = "";
        public string LaunchApp { get; set; } = "";
        public bool CloseApps { get; set; }
        public bool LogoutUser { get; set; }
        public string LogoutTarget { get; set; } = "";
        public string RevertDisplayProfile { get; set; } = "";
        public bool HasPassword { get; set; }
    }

    private const uint NO_SESSION = 0xFFFFFFFF;

    private static bool IsValidUsername(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_.\-]+$");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _config["Telegram:BotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
        var allowedUsersStr = _config["Telegram:AllowedUsers"] ?? Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USERS") ?? "";

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("TELEGRAM_BOT_TOKEN not set. Telegram bot disabled.");
            return;
        }

        if (!string.IsNullOrEmpty(allowedUsersStr))
        {
            foreach (var id in allowedUsersStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(id.Trim(), out var userId))
                    _allowedUsers.Add(userId);
            }
            _logger.LogInformation("Telegram bot: {Count} allowed users configured", _allowedUsers.Count);
        }
        else
        {
            _logger.LogWarning("TELEGRAM_ALLOWED_USERS not set. Send /myid to the bot to get your user ID.");
        }

        _bot = new TelegramBotClient(token);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        try
        {
            var me = await _bot.GetMeAsync(stoppingToken);
            _logger.LogInformation("Telegram bot started: @{Username}", me.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram bot: Invalid token or connection error. Bot disabled.");
            return;
        }

        _bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            stoppingToken
        );

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private bool IsAuthorized(long userId)
    {
        if (_allowedUsers.Count == 0) return false;
        return _allowedUsers.Contains(userId);
    }

    private bool IsRateLimited(long userId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_rateLimits.TryGetValue(userId, out var limit))
        {
            if ((now - limit.window).TotalMinutes >= 1)
            {
                _rateLimits[userId] = (1, now);
                return false;
            }
            if (limit.count >= MaxRequestsPerMinute)
            {
                _logger.LogWarning("Telegram: Rate limited user {UserId}", userId);
                return true;
            }
            _rateLimits[userId] = (limit.count + 1, limit.window);
            return false;
        }
        _rateLimits[userId] = (1, now);

        // Prune old entries every ~100 calls
        if (_rateLimits.Count > 50)
        {
            var stale = _rateLimits.Where(kv => (now - kv.Value.window).TotalMinutes > 2).Select(kv => kv.Key).ToList();
            foreach (var key in stale) _rateLimits.TryRemove(key, out _);
        }

        return false;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            // Ignore group messages
            if (update.Message?.Chat.Type != ChatType.Private && update.CallbackQuery?.Message?.Chat.Type != ChatType.Private)
                return;

            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await HandleCallback(update.CallbackQuery, ct);
                return;
            }

            if (update.Message?.Text == null) return;

            var message = update.Message;
            var userId = message.From?.Id ?? 0;
            var text = message.Text.Trim();

            // Ignore old messages
            var messageAge = DateTimeOffset.UtcNow - message.Date.ToUniversalTime();
            if (messageAge.TotalSeconds > 30)
                return;

            _logger.LogInformation("Telegram: [{UserId}] {Text}", userId, text);

            if (text == "/myid")
            {
                await bot.SendTextMessageAsync(message.Chat.Id,
                    $"Your Telegram user ID: `{userId}`\n\nAdd this to TELEGRAM\\_ALLOWED\\_USERS to authorize.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            if (!IsAuthorized(userId))
                return;

            if (IsRateLimited(userId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "Too many requests. Wait a minute.", cancellationToken: ct);
                return;
            }

            if (text == "/start" || text == "/help")
                await SendMainMenu(message.Chat.Id, ct);
            else if (text == "/profiles")
                await SendProfileList(message.Chat.Id, ct);
            else if (text == "/status")
                await SendStatus(message.Chat.Id, ct);
            else if (text == "/cancel")
            {
                _sessionMonitor.Cancel();
                await bot.SendTextMessageAsync(message.Chat.Id, "Auto-revert cancelled.", cancellationToken: ct);
            }
            else if (text.StartsWith("/create "))
            {
                var profileName = text["/create ".Length..].Trim();
                if (string.IsNullOrEmpty(profileName))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "Usage: `/create ProfileName`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                else
                {
                    var createResult = _profileService.CreateProfile(profileName);
                    var cEmoji = createResult.Success ? "✅" : "❌";
                    await bot.SendTextMessageAsync(message.Chat.Id, $"{cEmoji} {createResult.Message}", cancellationToken: ct);
                }
            }
            else
                await SendMainMenu(message.Chat.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram: Error handling update");
        }
    }

    private async Task HandleCallback(CallbackQuery callback, CancellationToken ct)
    {
        if (_bot == null || callback.Data == null) return;

        var userId = callback.From.Id;
        var chatId = callback.Message?.Chat.Id ?? 0;

        _logger.LogInformation("Telegram callback: [{UserId}] {Data}", userId, callback.Data);

        if (!IsAuthorized(userId))
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
            return;
        }

        if (IsRateLimited(userId))
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, "Too many requests", cancellationToken: ct);
            return;
        }

        var parts = callback.Data.Split(':');
        var action = parts[0];

        switch (action)
        {
            case "switch":
                if (parts.Length < 2) { await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct); break; }
                var profileName = parts[1];
                await _bot.AnswerCallbackQueryAsync(callback.Id, $"Switching to {profileName}...", cancellationToken: ct);
                var result = await _profileService.SwitchProfile(profileName);
                var emoji = result.Success ? "✅" : "❌";
                await _bot.SendTextMessageAsync(chatId, $"{emoji} {result.Message}", cancellationToken: ct);
                break;

            case "switch_revert":
                if (parts.Length < 3) { await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct); break; }
                var profile = parts[1];
                var revertTo = parts[2];
                _sessionMonitor.Cancel();
                await _bot.AnswerCallbackQueryAsync(callback.Id, $"Switching to {profile}...", cancellationToken: ct);
                var switchResult = await _profileService.SwitchProfile(profile);
                if (switchResult.Success)
                {
                    var botCapture = _bot;
                    _sessionMonitor.StartRevertOnLock(revertTo, chatId, async (cid, msg) =>
                    {
                        try { await botCapture.SendTextMessageAsync(cid, msg, parseMode: ParseMode.Markdown); }
                        catch { }
                    });
                    await _bot.SendTextMessageAsync(chatId,
                        $"✅ Switched to {profile}\n🔒 Will revert to {revertTo} when PC is locked",
                        cancellationToken: ct);
                }
                else
                {
                    await _bot.SendTextMessageAsync(chatId, $"❌ {switchResult.Message}", cancellationToken: ct);
                }
                break;

            case "cancel_revert":
                _sessionMonitor.Cancel();
                await _bot.AnswerCallbackQueryAsync(callback.Id, "Cancelled", cancellationToken: ct);
                await _bot.SendTextMessageAsync(chatId, "✅ Auto-revert cancelled.", cancellationToken: ct);
                break;

            case "status":
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                await SendStatus(chatId, ct);
                break;

            case "profiles":
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                await SendProfileList(chatId, ct);
                break;

            case "menu":
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                await SendMainMenu(chatId, ct);
                break;

            case "menu_profiles":
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                await SendProfilesMenu(chatId, ct);
                break;

            case "menu_automation":
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                await SendAutomationMenu(chatId, ct);
                break;

            case "create":
                await _bot.AnswerCallbackQueryAsync(callback.Id, "Opening profile creator...", cancellationToken: ct);
                await _bot.SendTextMessageAsync(chatId,
                    "To create a profile, send the name:\n`/create MyProfileName`",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "user_switch":
                if (parts.Length < 2) { await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct); break; }
                try
                {
                    var upName = parts[1];
                    var up = _userProfiles.FirstOrDefault(p => p.Name == upName);
                    if (up == null)
                    {
                        await _bot.AnswerCallbackQueryAsync(callback.Id, "Profile not found", cancellationToken: ct);
                        break;
                    }

                    if (!string.IsNullOrEmpty(up.WindowsUser) && !IsValidUsername(up.WindowsUser))
                    {
                        await _bot.AnswerCallbackQueryAsync(callback.Id, "Invalid username", cancellationToken: ct);
                        _logger.LogWarning("user_switch: Invalid WindowsUser '{User}'", up.WindowsUser);
                        break;
                    }

                    _sessionMonitor.Cancel();
                    await _bot.AnswerCallbackQueryAsync(callback.Id, $"Switching to {up.Name}...", cancellationToken: ct);
                    await _bot.SendTextMessageAsync(chatId, $"⏳ Switching to *{up.Name}*...", parseMode: ParseMode.Markdown, cancellationToken: ct);

                    // Step 1: Apply display profile FIRST (before user switch, so it runs in current session with access to profiles)
                    _logger.LogInformation("user_switch: Applying display profile '{Profile}'", up.DisplayProfile);
                    var dpResult = await _profileService.SwitchProfile(up.DisplayProfile);
                    _logger.LogInformation("user_switch: Display switch result: {Success} - {Message}", dpResult.Success, dpResult.Message);

                    // Step 2: Switch Windows user (skip if empty)
                    bool userSuccess = true;
                    string userMsg = "No user switch needed";
                    if (!string.IsNullOrEmpty(up.WindowsUser))
                    {
                        _logger.LogInformation("user_switch: Switching to Windows user '{User}'", up.WindowsUser);
                        (userSuccess, userMsg) = UserSwitcher.SwitchToUser(up.WindowsUser, up.HasPassword);
                        _logger.LogInformation("user_switch: User switch result: {Success} - {Message}", userSuccess, userMsg);

                        // Wait for session to be ready after user switch
                        if (userSuccess)
                        {
                            _logger.LogDebug("user_switch: Waiting for user '{User}' session to be ready...", up.WindowsUser);
                            for (int i = 0; i < 20; i++) // max 10 seconds
                            {
                                await Task.Delay(500);
                                var sid = UserSwitcher.FindUserSession(up.WindowsUser);
                                if (sid != NO_SESSION)
                                {
                                    _logger.LogDebug("user_switch: Session {Id} ready for '{User}'", sid, up.WindowsUser);
                                    await Task.Delay(1000); // extra 1s for session to fully initialize
                                    break;
                                }
                            }
                        }
                    }

                    // Step 3: Close all apps gracefully if configured
                    if (up.CloseApps)
                    {
                        var closeTarget = !string.IsNullOrEmpty(up.LogoutTarget) ? up.LogoutTarget : up.WindowsUser;
                        if (!string.IsNullOrEmpty(closeTarget) && IsValidUsername(closeTarget))
                        {
                            _logger.LogInformation("user_switch: Closing all apps for user '{User}'", closeTarget);
                            try
                            {
                                await CloseAllUserApps(_logger, closeTarget);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "user_switch: Error closing apps");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("user_switch: Skipping CloseApps — invalid or empty target user");
                        }
                    }

                    // Step 4: Logout target user if configured
                    if (up.LogoutUser && !string.IsNullOrEmpty(up.LogoutTarget))
                    {
                        if (IsValidUsername(up.LogoutTarget))
                        {
                            _logger.LogInformation("user_switch: Logging out user '{User}'", up.LogoutTarget);
                            try
                            {
                                var targetSessionId = UserSwitcher.FindUserSession(up.LogoutTarget);
                                if (targetSessionId != NO_SESSION)
                                {
                                    var (logoffOk, logoffMsg) = UserSwitcher.LogoffSession(targetSessionId);
                                    _logger.LogInformation("user_switch: Logoff result: {Success} - {Message}", logoffOk, logoffMsg);
                                }
                                else
                                {
                                    _logger.LogInformation("user_switch: User '{User}' has no active session to log off", up.LogoutTarget);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "user_switch: Error logging out user");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("user_switch: Skipping LogoutUser — invalid LogoutTarget '{Target}'", up.LogoutTarget);
                        }
                    }

                    // Step 5: Launch app if configured
                    string appMsg = "";
                    if (!string.IsNullOrEmpty(up.LaunchApp))
                    {
                        _logger.LogInformation("user_switch: Launching app '{App}'", up.LaunchApp);
                        try
                        {
                            string launchExe;
                            string launchArgs;

                            if (up.LaunchApp.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                // Direct exe: "C:\path\app.exe --args"
                                // Find end of exe path (after .exe)
                                var exeIdx = up.LaunchApp.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
                                launchExe = up.LaunchApp[..exeIdx].Trim();
                                launchArgs = exeIdx < up.LaunchApp.Length ? up.LaunchApp[exeIdx..].Trim() : "";
                            }
                            else
                            {
                                // Protocol URL (steam://, origin2://)
                                launchExe = "cmd.exe";
                                launchArgs = $"/c start \"\" \"{up.LaunchApp}\"";
                            }

                            var (appExit, appSuccess, appError) = UserSessionLauncher.RunInUserSession(
                                launchExe, launchArgs, 5000, fireAndForget: true);
                            appMsg = appSuccess ? $"\n🚀 Launched" : $"\n⚠️ App launch failed";
                            _logger.LogInformation("user_switch: App launch result: {Success} - {Error}", appSuccess, appError);
                        }
                        catch (Exception ex)
                        {
                            appMsg = $"\n⚠️ App launch error";
                            _logger.LogError(ex, "user_switch: App launch error");
                        }
                    }

                    // Step 6: Set up revert on lock if configured
                    if (!string.IsNullOrEmpty(up.RevertDisplayProfile))
                    {
                        var botCapture = _bot;
                        var revertProfile = up.RevertDisplayProfile;
                        _sessionMonitor.StartRevertOnLock(revertProfile, chatId, async (cid, msg) =>
                        {
                            try
                            {
                                _logger.LogInformation("Revert: Switching display to '{Profile}'", revertProfile);
                                await botCapture.SendTextMessageAsync(cid, msg, parseMode: ParseMode.Markdown);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Revert callback error");
                            }
                        });
                    }

                    var statusLines = new List<string>();
                    if (!string.IsNullOrEmpty(up.WindowsUser))
                        statusLines.Add(userSuccess ? $"✅ User: *{up.WindowsUser}*" : $"❌ User switch failed");
                    if (up.LogoutUser && !string.IsNullOrEmpty(up.LogoutTarget))
                        statusLines.Add($"✅ Logged out *{up.LogoutTarget}*");
                    statusLines.Add(dpResult.Success ? $"✅ Display: *{up.DisplayProfile}*" : $"❌ Display failed");
                    if (!string.IsNullOrEmpty(appMsg))
                        statusLines.Add(appMsg.Trim());
                    if (!string.IsNullOrEmpty(up.RevertDisplayProfile))
                        statusLines.Add($"🔒 Revert display to *{up.RevertDisplayProfile}* on lock");
                    await _bot.SendTextMessageAsync(chatId, string.Join("\n", statusLines), parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "user_switch error");
                    await _bot.SendTextMessageAsync(chatId, "❌ An error occurred. Check service logs.", cancellationToken: ct);
                }
                break;

            case "remove":
                if (parts.Length < 2) { await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct); break; }
                var removeName = parts[1];
                var removeResult = await _profileService.RemoveProfile(removeName);
                await _bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                var rEmoji = removeResult.Success ? "✅" : "❌";
                await _bot.SendTextMessageAsync(chatId, $"{rEmoji} {removeResult.Message}", cancellationToken: ct);
                break;
        }
    }

    private async Task SendMainMenu(long chatId, CancellationToken ct)
    {
        if (_bot == null) return;

        var rows = new List<List<InlineKeyboardButton>>
        {
            new() { InlineKeyboardButton.WithCallbackData("📋 Profiles", "menu_profiles") },
            new() { InlineKeyboardButton.WithCallbackData("🤖 Automation", "menu_automation") },
            new() { InlineKeyboardButton.WithCallbackData("📊 Status", "status") },
        };

        var keyboard = new InlineKeyboardMarkup(rows);
        await _bot.SendTextMessageAsync(chatId, "🖥 *DisplayMagician Remote*\nChoose a section:",
            parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendProfilesMenu(long chatId, CancellationToken ct)
    {
        if (_bot == null) return;

        var profiles = await _profileService.GetProfiles();
        var rows = new List<List<InlineKeyboardButton>>();

        foreach (var p in profiles)
        {
            var otherProfile = profiles.FirstOrDefault(x => x.Name != p.Name)?.Name ?? p.Name;
            rows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"🖥 {p.Name}", $"switch:{p.Name}"),
                InlineKeyboardButton.WithCallbackData($"🔒 {p.Name} (revert on lock)", $"switch_revert:{p.Name}:{otherProfile}"),
            });
        }

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("📋 All Profiles", "profiles"),
        });
        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("⬅️ Back", "menu"),
        });

        var keyboard = new InlineKeyboardMarkup(rows);
        await _bot.SendTextMessageAsync(chatId, "📋 *Display Profiles*\nSwitch display profile:",
            parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendAutomationMenu(long chatId, CancellationToken ct)
    {
        if (_bot == null) return;

        var rows = new List<List<InlineKeyboardButton>>();

        foreach (var up in _userProfiles)
        {
            var revertLabel = string.IsNullOrEmpty(up.RevertDisplayProfile) ? "" : " + revert on lock";
            rows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"👤 {up.Name} (user + profile{revertLabel})", $"user_switch:{up.Name}"),
            });
        }

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("❌ Cancel Revert", "cancel_revert"),
        });
        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("📊 Status", "status"),
            InlineKeyboardButton.WithCallbackData("⬅️ Back", "menu"),
        });

        var keyboard = new InlineKeyboardMarkup(rows);
        await _bot.SendTextMessageAsync(chatId, "🤖 *Automation*\nSwitch user \\+ display profile:",
            parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendProfileList(long chatId, CancellationToken ct)
    {
        if (_bot == null) return;

        var profiles = await _profileService.GetProfiles();
        var sb = new StringBuilder("📋 *Profiles:*\n\n");
        var rows = new List<List<InlineKeyboardButton>>();

        foreach (var p in profiles)
        {
            sb.Append($"• *{p.Name}*\n  `{p.UUID}`\n");
            rows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"🖥 Switch to {p.Name}", $"switch:{p.Name}"),
                InlineKeyboardButton.WithCallbackData($"🗑 Remove {p.Name}", $"remove:{p.Name}"),
            });
        }

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("➕ Create Profile", "create"),
            InlineKeyboardButton.WithCallbackData("⬅️ Back", "menu"),
        });

        var keyboard = new InlineKeyboardMarkup(rows);
        await _bot.SendTextMessageAsync(chatId, sb.ToString(), parseMode: ParseMode.Markdown,
            replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendStatus(long chatId, CancellationToken ct)
    {
        if (_bot == null) return;

        var revertStatus = _sessionMonitor.IsMonitoring ? "⏱ Pending" : "None";
        var revertInfo = _sessionMonitor.RevertProfile != null
            ? $"\nRevert to: *{_sessionMonitor.RevertProfile}*\nTrigger: *session lock*" : "";

        var text = $"📊 *Status*\n\n"
            + $"Auto-revert: *{revertStatus}*{revertInfo}";

        await _bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    /// <summary>
    /// Close user apps. Runs taskkill directly as SYSTEM (works cross-session).
    /// Pass 1: graceful (WM_CLOSE). Pass 2: force kill.
    /// </summary>
    private static async Task CloseAllUserApps(ILogger logger, string? targetUser = null)
    {
        if (string.IsNullOrEmpty(targetUser)) return;

        // Pass 1: Graceful close (sends WM_CLOSE)
        logger.LogInformation("CloseAllUserApps: Graceful close for user '{User}'", targetUser);
        RunTaskkill(logger, $"/fi \"USERNAME eq {targetUser}\" /fi \"IMAGENAME ne explorer.exe\"");

        // Wait for graceful shutdown
        await Task.Delay(3000);

        // Pass 2: Force kill remaining
        logger.LogInformation("CloseAllUserApps: Force killing remaining for '{User}'", targetUser);
        RunTaskkill(logger, $"/f /fi \"USERNAME eq {targetUser}\" /fi \"IMAGENAME ne explorer.exe\"");
    }

    private static void RunTaskkill(ILogger logger, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(15000);
            logger.LogDebug("taskkill {Args}: exit={Code}", args, proc?.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "taskkill error");
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram bot error");
        return Task.CompletedTask;
    }
}
