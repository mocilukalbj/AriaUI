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
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(filePath);
                using var p = Process.Start(psi);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var p = Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add(filePath);
                using var p = Process.Start(psi);
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
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(directoryPath);
                using var p = Process.Start(psi);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add(directoryPath);
                using var p = Process.Start(psi);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add(directoryPath);
                using var p = Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSystemService] Error opening directory: {ex.Message}");
        }
    }
}
