using System;
using System.Diagnostics;
using System.IO;

namespace AriaUI.Services;

public class FileSystemService : IFileSystemService
{
    public void OpenFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Cannot open a file that does not exist.", filePath);
        }

        ProcessStartInfo startInfo;
        if (OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(filePath);
        }
        else if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(filePath) { UseShellExecute = true };
        }
        else
        {
            throw new PlatformNotSupportedException("Opening files is supported only on Linux and Windows.");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to launch the system file opener for: {filePath}");
    }

    public void OpenDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (File.Exists(directoryPath))
        {
            directoryPath = Path.GetDirectoryName(directoryPath) ?? directoryPath;
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Cannot open a directory that does not exist: {directoryPath}");
        }

        ProcessStartInfo startInfo;
        if (OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(directoryPath);
        }
        else if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directoryPath);
        }
        else
        {
            throw new PlatformNotSupportedException("Opening directories is supported only on Linux and Windows.");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to launch the system directory opener for: {directoryPath}");
    }
}
