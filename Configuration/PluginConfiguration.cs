using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PeanutButterJelly.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Force { get; set; }

    public bool TagAlbums { get; set; } = true;

    public bool TagTracks { get; set; } = true;

    public bool FetchLyrics { get; set; } = true;

    public bool FetchArtists { get; set; } = true;

    public bool RepairPlaylists { get; set; } = true;

    /// <summary>
    /// Gets or sets worker count. 0 means use CPU count.
    /// </summary>
    public int Workers { get; set; }
}
