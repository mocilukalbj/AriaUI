using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace AriaUI.Services;

public class TrackerService : ITrackerService
{
    private readonly HttpClient _httpClient;

    public TrackerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<List<string>> FetchTrackersAsync(string trackersUrl)
    {
        if (string.IsNullOrWhiteSpace(trackersUrl)) return new List<string>();

        var content = await _httpClient.GetStringAsync(trackersUrl);
        var trackers = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .Where(t => !string.IsNullOrEmpty(t) && !t.StartsWith("#"))
                              .Distinct()
                              .ToList();

        return trackers;
    }
}
