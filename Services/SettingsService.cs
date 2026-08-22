using System;
using System.IO;
using System.Text.Json;
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
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "AriaUI");
        Directory.CreateDirectory(configDir);
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
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
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

        EnsureDownloadDirExists();
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
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

        EnsureDownloadDirExists();
    }

    private void EnsureDownloadDirExists()
    {
        if (string.IsNullOrWhiteSpace(_settings.DefaultDownloadDir) || !Directory.Exists(_settings.DefaultDownloadDir))
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "下载");
            if (!Directory.Exists(fallback))
            {
                fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(fallback);
            }
            _settings.DefaultDownloadDir = fallback;
        }
    }

    public async Task SaveAsync(AppSettings? newSettings = null)
    {
        var targetSettings = newSettings ?? _settings;
        var errors = targetSettings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }

        var json = JsonSerializer.Serialize(targetSettings, new JsonSerializerOptions { WriteIndented = true });
        
        var tempFile = _configFilePath + ".tmp";
        await File.WriteAllTextAsync(tempFile, json);
        File.Move(tempFile, _configFilePath, true);

        _settings = targetSettings;
    }
}
