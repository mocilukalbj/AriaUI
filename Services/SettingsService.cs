using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task SaveAsync(AppSettings newSettings);
    Task<AppSettings> UpdateAsync(Action<AppSettings> update);
}

public class SettingsService : ISettingsService
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _settings = new();

    public AppSettings Settings => _settings.Clone();

    public SettingsService()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(userProfile, ".config");
        }
        var configDir = Path.Combine(appData, "AriaUI");

        var legacyConfig = Path.Combine(userProfile, ".config", "AriaUI", "config.json");
        var standardConfig = Path.Combine(configDir, "config.json");
        if (File.Exists(legacyConfig) && !File.Exists(standardConfig))
        {
            _configFilePath = legacyConfig;
        }
        else
        {
            Directory.CreateDirectory(configDir);
            _configFilePath = standardConfig;
        }

        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                _settings = JsonSerializer.Deserialize(json, AriaJsonContext.Default.AppSettings)
                    ?? throw new JsonException("Settings file contains null.");
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[SettingsService] Failed to parse config file: {ex.Message}");
            BackupCorruptedConfig();
            throw;
        }

        var changed = EnsureDefaultDownloadDir(_settings);
        ValidateLoadedSettings();
        if (changed)
        {
            PersistSettings(_settings);
        }
        else
        {
            SetPrivateFileMode(_configFilePath);
        }
    }

    private void BackupCorruptedConfig()
    {
        if (File.Exists(_configFilePath))
        {
            var badConfig = $"{_configFilePath}.bad-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_configFilePath, badConfig, true);
            SetPrivateFileMode(badConfig);
            Console.Error.WriteLine($"[SettingsService] Corrupted config file backed up to: {badConfig}");
        }
    }

    private static bool EnsureDefaultDownloadDir(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DefaultDownloadDir))
        {
            return false;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(userProfile, "Downloads");
        if (!Directory.Exists(fallback))
        {
            fallback = Path.Combine(userProfile, "下载");
            Directory.CreateDirectory(fallback);
        }
        settings.DefaultDownloadDir = fallback;
        return true;
    }

    private void ValidateLoadedSettings()
    {
        var errors = _settings.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Invalid settings: {string.Join("; ", errors)}");
        }
    }

    public async Task SaveAsync(AppSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);

        await _saveLock.WaitAsync();
        try
        {
            var targetSettings = newSettings.Clone();
            _ = EnsureDefaultDownloadDir(targetSettings);
            var errors = targetSettings.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join("; ", errors));
            }

            await PersistSettingsAsync(targetSettings);
            _settings = targetSettings;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _saveLock.WaitAsync();
        try
        {
            var targetSettings = _settings.Clone();
            update(targetSettings);
            _ = EnsureDefaultDownloadDir(targetSettings);
            var errors = targetSettings.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join("; ", errors));
            }

            await PersistSettingsAsync(targetSettings);
            _settings = targetSettings;
            return targetSettings.Clone();
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void PersistSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AriaJsonContext.Default.AppSettings);
        var tempFile = _configFilePath + ".tmp";

        try
        {
            WritePrivateText(tempFile, json);
            File.Move(tempFile, _configFilePath, true);
        }
        catch
        {
            TryDeleteTempFile(tempFile);
            throw;
        }
    }

    private async Task PersistSettingsAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AriaJsonContext.Default.AppSettings);
        var tempFile = _configFilePath + ".tmp";

        try
        {
            await WritePrivateTextAsync(tempFile, json);
            File.Move(tempFile, _configFilePath, true);
        }
        catch
        {
            TryDeleteTempFile(tempFile);
            throw;
        }
    }

    private static void SetPrivateFileMode(string filePath)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void WritePrivateText(string filePath, string contents)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        SetPrivateFileMode(filePath);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    private static async Task WritePrivateTextAsync(string filePath, string contents)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        SetPrivateFileMode(filePath);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents);
    }

    private static void TryDeleteTempFile(string tempFile)
    {
        try
        {
            File.Delete(tempFile);
        }
        catch
        {
            // Preserve the original persistence exception.
        }
    }
}
