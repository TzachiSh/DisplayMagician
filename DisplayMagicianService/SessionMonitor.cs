using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace DisplayMagicianService;

/// <summary>
/// Extends WindowsServiceLifetime to receive session lock/unlock events natively.
/// Zero polling — purely event-driven via ServiceBase.OnSessionChange.
/// </summary>
public class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionMonitor _monitor;

    public SessionAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptions,
        SessionMonitor monitor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptions)
    {
        _monitor = monitor;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _monitor.HandleSessionChange(changeDescription);
        base.OnSessionChange(changeDescription);
    }
}

/// <summary>
/// Tracks session lock/unlock state and triggers profile revert on lock.
/// </summary>
public class SessionMonitor
{
    private readonly ILogger<SessionMonitor> _logger;
    private readonly ProfileService _profileService;
    private readonly object _lock = new();
    private string? _revertProfile;
    private long _revertChatId;
    private Func<long, string, Task>? _onReverted;

    public bool IsMonitoring
    {
        get { lock (_lock) { return _revertProfile != null; } }
    }

    public string? RevertProfile
    {
        get { lock (_lock) { return _revertProfile; } }
    }

    public SessionMonitor(ILogger<SessionMonitor> logger, ProfileService profileService)
    {
        _logger = logger;
        _profileService = profileService;
    }

    public void StartRevertOnLock(string revertToProfile, long chatId = 0, Func<long, string, Task>? onReverted = null)
    {
        Cancel();
        lock (_lock)
        {
            _revertProfile = revertToProfile;
            _revertChatId = chatId;
            _onReverted = onReverted;
        }
        _logger.LogInformation("SessionMonitor: Will revert to '{Profile}' on session lock (event-driven)", revertToProfile);
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (_revertProfile != null)
            {
                _logger.LogInformation("SessionMonitor: Monitoring cancelled");
                _revertProfile = null;
                _onReverted = null;
            }
        }
    }

    public void HandleSessionChange(SessionChangeDescription change)
    {
        _logger.LogInformation("SessionMonitor: Event={Reason}, SessionId={Id}", change.Reason, change.SessionId);

        string? profile;
        long chatId;
        Func<long, string, Task>? callback;

        lock (_lock)
        {
            if (change.Reason != SessionChangeReason.SessionLock || _revertProfile == null)
                return;

            _logger.LogInformation("SessionMonitor: Session LOCKED! Reverting to '{Profile}'", _revertProfile);

            profile = _revertProfile;
            chatId = _revertChatId;
            callback = _onReverted;
            _revertProfile = null;
            _onReverted = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _profileService.SwitchProfile(profile);

                if (result.Success)
                {
                    _logger.LogInformation("SessionMonitor: Reverted to '{Profile}'", profile);
                    if (callback != null) await callback(chatId, $"✅ Session locked — switched to *{profile}*");
                }
                else
                {
                    _logger.LogError("SessionMonitor: Revert failed: {Message}", result.Message);
                    if (callback != null) await callback(chatId, $"❌ Revert failed: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SessionMonitor: Error during revert");
            }
        });
    }
}
