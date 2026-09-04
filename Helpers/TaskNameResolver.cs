using System;
using System.IO;
using AriaUI.Models;

namespace AriaUI.Helpers;

public static class TaskNameResolver
{
    public static string Resolve(AriaTaskInfo? task)
    {
        if (task is null) return "Unknown Download";

        var btName = TryGetBtName(task);
        if (!string.IsNullOrWhiteSpace(btName)) return btName;

        var fileName = TryGetFileName(task);
        if (!string.IsNullOrWhiteSpace(fileName)) return fileName;

        var uriName = TryGetUriName(task);
        if (!string.IsNullOrWhiteSpace(uriName)) return uriName;

        return !string.IsNullOrEmpty(task.Gid) ? $"Task-{task.Gid}" : "Unknown Download";
    }

    private static string? TryGetBtName(AriaTaskInfo task)
    {
        var name = task.Bittorrent?.Info?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? TryGetFileName(AriaTaskInfo task)
    {
        if (task.Files == null || task.Files.Count == 0) return null;
        var firstFile = task.Files[0];
        if (string.IsNullOrWhiteSpace(firstFile.Path)) return null;

        var fn = Path.GetFileName(firstFile.Path);
        return string.IsNullOrWhiteSpace(fn) ? null : fn;
    }

    private static string? TryGetUriName(AriaTaskInfo task)
    {
        if (task.Files == null || task.Files.Count == 0) return null;
        var firstFile = task.Files[0];
        if (firstFile.Uris == null || firstFile.Uris.Count == 0) return null;

        var uriStr = firstFile.Uris[0].Uri;
        if (string.IsNullOrWhiteSpace(uriStr)) return null;

        if (Uri.TryCreate(uriStr, UriKind.Absolute, out var uri))
        {
            var fn = Path.GetFileName(uri.LocalPath.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(fn)) return fn;

            // Strip query parameters and fragment to prevent leaking sensitive tokens/auth
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }

        var queryIdx = uriStr.IndexOf('?');
        return queryIdx > 0 ? uriStr.Substring(0, queryIdx) : uriStr;
    }
}
