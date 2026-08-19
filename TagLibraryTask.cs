using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButterJelly;

public class TagLibraryTask : IScheduledTask
{
    private readonly TaggerEngine _engine;
    private readonly ILogger<TagLibraryTask> _logger;

    public TagLibraryTask(TaggerEngine engine, ILogger<TagLibraryTask> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public string Name => "Peanut Butter & Jelly";

    public string Key => "PeanutButterJellyTagLibrary";

    public string Description =>
        "Apply Deezer genres/artists/explicit, LRCLIB lyrics, artist bios/photos, and playlist repair.";

    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _engine.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peanut Butter & Jelly failed");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
