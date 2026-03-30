using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DisplayMagicianService;

public class ProfileService
{
    private readonly ILogger<ProfileService> _logger;
    private readonly string _consolePath;
    private readonly string _profilesPath;
    private readonly string _dataPath;
    private readonly Dictionary<string, string> _envVars;

    private static readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ProfileService(ILogger<ProfileService> logger, IConfiguration config)
    {
        _logger = logger;

        // Find DisplayMagicianConsole.exe relative to service exe
        var baseDir = AppContext.BaseDirectory;
        _consolePath = Path.Combine(baseDir, "DisplayMagicianConsole.exe");

        // If not found next to service, check common locations
        if (!File.Exists(_consolePath))
        {
            var sourceDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "DisplayMagicianConsole", "bin", "Release"));
            _consolePath = Path.Combine(sourceDir, "DisplayMagicianConsole.exe");
        }

        // Read profiles path from config, fallback to default
        _profilesPath = config["Service:ProfilesPath"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DisplayMagician", "Profiles", "DisplayProfiles.json");

        // Derive data path from profiles path (go up two directories: Profiles/DisplayProfiles.json -> DisplayMagician)
        _dataPath = Path.GetDirectoryName(Path.GetDirectoryName(_profilesPath)) ?? "";
        _envVars = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(_dataPath))
            _envVars["DISPLAYMAGICIAN_DATA_PATH"] = _dataPath;

        _logger.LogInformation("ProfileService initialized. Console path: {Path}", _consolePath);
        _logger.LogInformation("ProfileService initialized. Profiles path: {Path}", _profilesPath);
        _logger.LogInformation("ProfileService initialized. Data path: {Path}", _dataPath);
    }

    public async Task<List<ProfileInfo>> GetProfiles()
    {
        _logger.LogDebug("GetProfiles: Reading profiles from {Path}", _profilesPath);
        var profiles = new List<ProfileInfo>();

        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_profilesPath))
            {
                _logger.LogWarning("GetProfiles: Profiles file not found at {Path}", _profilesPath);
                return profiles;
            }

            var json = await File.ReadAllTextAsync(_profilesPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Profiles", out var profilesArray))
            {
                foreach (var profile in profilesArray.EnumerateArray())
                {
                    var uuid = profile.GetProperty("UUID").GetString() ?? "";
                    var name = profile.GetProperty("Name").GetString() ?? "";
                    profiles.Add(new ProfileInfo { UUID = uuid, Name = name });
                    _logger.LogDebug("GetProfiles: Found profile {Name} ({UUID})", name, uuid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProfiles: Error reading profiles file");
        }
        finally
        {
            _fileLock.Release();
        }

        return profiles;
    }

    public async Task<ProfileSwitchResult> SwitchProfile(string nameOrUuid)
    {
        _logger.LogInformation("SwitchProfile: Switching to profile '{Profile}'", nameOrUuid);

        if (!File.Exists(_consolePath))
        {
            _logger.LogError("SwitchProfile: Console exe not found at {Path}", _consolePath);
            return new ProfileSwitchResult
            {
                Success = false,
                Message = $"DisplayMagicianConsole.exe not found at {_consolePath}"
            };
        }

        return await Task.Run(() =>
        {
            try
            {
                var arguments = $"ChangeProfile \"{nameOrUuid}\"";
                _logger.LogDebug("SwitchProfile: Running in user session: {Exe} {Args}", _consolePath, arguments);

                var (exitCode, success, error) = UserSessionLauncher.RunInUserSession(_consolePath, arguments, 60000, _envVars);

                _logger.LogInformation("SwitchProfile: Exit code {Code}, success={Success}", exitCode, success);

                return new ProfileSwitchResult
                {
                    Success = success,
                    ExitCode = (int)exitCode,
                    Message = success
                        ? $"Successfully switched to profile '{nameOrUuid}'"
                        : $"Failed to switch profile. {error}",
                    Output = ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SwitchProfile: Exception while switching profile");
                return new ProfileSwitchResult { Success = false, Message = ex.Message };
            }
        });
    }

    public async Task<ProfileSwitchResult> GetCurrentProfile()
    {
        _logger.LogInformation("GetCurrentProfile: Getting current active profile");
        return await RunConsole("CurrentProfile --parseable");
    }

    public ProfileSwitchResult CreateProfile(string name)
    {
        _logger.LogInformation("CreateProfile: Creating profile '{Name}' from current display config", name);

        try
        {
            var profiles = GetProfiles().GetAwaiter().GetResult();
            if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return new ProfileSwitchResult { Success = false, Message = $"Profile '{name}' already exists" };
            }

            // Launch DisplayMagician.exe CreateProfile in user session — opens the save dialog
            var dmPath = _consolePath.Replace("DisplayMagicianConsole.exe", "DisplayMagician.exe");
            if (File.Exists(dmPath))
            {
                var (exitCode, success, error) = UserSessionLauncher.RunInUserSession(dmPath, "CreateProfile", 120000);
                return new ProfileSwitchResult
                {
                    Success = success,
                    ExitCode = (int)exitCode,
                    Message = success ? "Profile creation window opened. Save the profile in the UI." : $"Failed: {error}"
                };
            }

            return new ProfileSwitchResult { Success = false, Message = "DisplayMagician.exe not found" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateProfile: Error");
            return new ProfileSwitchResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<ProfileSwitchResult> RemoveProfile(string nameOrUuid)
    {
        _logger.LogInformation("RemoveProfile: Removing profile '{Profile}'", nameOrUuid);

        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_profilesPath))
            {
                return new ProfileSwitchResult { Success = false, Message = "Profiles file not found" };
            }

            var json = await File.ReadAllTextAsync(_profilesPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Profiles", out var profilesArray))
            {
                return new ProfileSwitchResult { Success = false, Message = "No profiles found" };
            }

            // Find the profile to remove
            var profilesToKeep = new List<JsonElement>();
            string removedName = "";
            bool found = false;

            foreach (var profile in profilesArray.EnumerateArray())
            {
                var uuid = profile.GetProperty("UUID").GetString() ?? "";
                var name = profile.GetProperty("Name").GetString() ?? "";

                if (uuid.Equals(nameOrUuid, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(nameOrUuid, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    removedName = name;
                    _logger.LogInformation("RemoveProfile: Found profile to remove: {Name} ({UUID})", name, uuid);
                    continue; // skip this one
                }
                profilesToKeep.Add(profile);
            }

            if (!found)
            {
                return new ProfileSwitchResult { Success = false, Message = $"Profile '{nameOrUuid}' not found" };
            }

            // Rebuild the JSON
            var version = root.GetProperty("ProfileFileVersion").GetString() ?? "3";
            var newJson = new
            {
                ProfileFileVersion = version,
                LastUpdated = DateTimeOffset.UtcNow.ToString("o"),
                Profiles = profilesToKeep.Select(p => JsonSerializer.Deserialize<object>(p.GetRawText()))
            };

            await File.WriteAllTextAsync(_profilesPath, JsonSerializer.Serialize(newJson, _jsonOptions));

            _logger.LogInformation("RemoveProfile: Successfully removed profile '{Name}'", removedName);
            return new ProfileSwitchResult { Success = true, Message = $"Successfully removed profile '{removedName}'" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveProfile: Error removing profile");
            return new ProfileSwitchResult { Success = false, Message = ex.Message };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private Task<ProfileSwitchResult> RunConsole(string arguments)
    {
        return RunProcess(_consolePath, arguments);
    }

    private async Task<ProfileSwitchResult> RunProcess(string exe, string arguments)
    {
        if (!File.Exists(exe))
        {
            return new ProfileSwitchResult { Success = false, Message = $"Executable not found: {exe}" };
        }

        return await Task.Run(() =>
        {
            try
            {
                _logger.LogDebug("RunProcess: {Exe} {Args}", exe, arguments);
                var (exitCode, success, error) = UserSessionLauncher.RunInUserSession(exe, arguments, 60000, _envVars);

                return new ProfileSwitchResult
                {
                    Success = success,
                    ExitCode = (int)exitCode,
                    Message = success ? "Success" : $"Exit code: {exitCode}. {error}",
                    Output = ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RunProcess: Exception");
                return new ProfileSwitchResult { Success = false, Message = ex.Message };
            }
        });
    }
}

public class ProfileInfo
{
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ProfileSwitchResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = "";
    public string Output { get; set; } = "";
}
