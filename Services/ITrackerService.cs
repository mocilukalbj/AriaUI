using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AriaUI.Services;

public interface ITrackerService
{
    Task<List<string>> FetchTrackersAsync(string trackersUrl, CancellationToken cancellationToken = default);
}
