using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AriaUI.Services;

public class FileSystemService : IFileSystemService
{
    public void OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{filePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{filePath}\"");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSystemService] Error opening file: {ex.Message}");
        }
    }

    public void OpenDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;

        if (File.Exists(directoryPath))
        {
            directoryPath = Path.GetDirectoryName(directoryPath) ?? directoryPath;
        }

        if (!Directory.Exists(directoryPath)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{directoryPath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directoryPath}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{directoryPath}\"");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSystemService] Error opening directory: {ex.Message}");
        }
    }
}
