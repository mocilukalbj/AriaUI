using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Load();
    Task LoadAsync();
    Task SaveAsync(AppSettings? newSettings = null);
}

public class SettingsService : ISettingsService
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        var configDir = Path.Combine(appData, "AriaUI");

        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsService] Failed to create config dir: {ex.Message}");
        }

        _configFilePath = Path.Combine(configDir, "config.json");

        var defaultDownload = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(defaultDownload))
        {
            defaultDownload = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "下载");
        }
        _settings.DefaultDownloadDir = defaultDownload;

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var loaded = JsonSerializer.Deserialize(json, AriaJsonContext.Default.AppSettings);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsService] Fallback to default settings: {ex.Message}");
        }

        EnsureSecretAndDir();
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath);
                var loaded = JsonSerializer.Deserialize(json, AriaJsonContext.Default.AppSettings);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SettingsService] Fallback to default settings: {ex.Message}");
        }

        EnsureSecretAndDir();
    }

    private void EnsureSecretAndDir()
    {
        if (string.IsNullOrWhiteSpace(_settings.RpcSecret))
        {
            _settings.RpcSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(_settings.DefaultDownloadDir) || !Directory.Exists(_settings.DefaultDownloadDir))
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(fallback))
            {
                fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "下载");
                try { Directory.CreateDirectory(fallback); } catch { }
            }
            _settings.DefaultDownloadDir = fallback;
        }
    }

    public async Task SaveAsync(AppSettings? newSettings = null)
    {
        await _saveLock.WaitAsync();
        try
        {
            var targetSettings = newSettings ?? _settings;
            var errors = targetSettings.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join("; ", errors));
            }

            var json = JsonSerializer.Serialize(targetSettings, AriaJsonContext.Default.AppSettings);
            
            var tempFile = _configFilePath + ".tmp";
            await File.WriteAllTextAsync(tempFile, json);

            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { }

            File.Move(tempFile, _configFilePath, true);

            _settings = targetSettings;
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
