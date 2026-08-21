using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PeanutButterJelly.Configuration;

public static class Providers
{
    public const string LrcLib = "LRCLIB";
    public const string Deezer = "Deezer";
    public const string AudioDb = "AudioDB";
    public const string Wikipedia = "Wikipedia";
    public const string Wikidata = "Wikidata";

    public static bool Enabled(IReadOnlyList<string>? selected, string id)
        => selected is not null && selected.Any(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Force { get; set; }

    public bool TagAlbums { get; set; } = true;

    public bool TagTracks { get; set; } = true;

    /// <summary>Legacy. Used only when <see cref="LyricProviders"/> is null.</summary>
    public bool FetchLyrics { get; set; } = true;

    /// <summary>Legacy. Used only when photo/bio provider lists are null.</summary>
    public bool FetchArtists { get; set; } = true;

    public bool RepairPlaylists { get; set; } = true;

    public List<string>? LyricProviders { get; set; }

    public List<string>? PhotoProviders { get; set; }

    public List<string>? BioProviders { get; set; }

    /// <summary>
    /// Gets or sets worker count. 0 means use CPU count.
    /// </summary>
    public int Workers { get; set; }

    public IReadOnlyList<string> EffectiveLyricProviders
        => LyricProviders ?? (FetchLyrics ? [Providers.LrcLib] : []);

    public IReadOnlyList<string> EffectivePhotoProviders
        => PhotoProviders ?? (FetchArtists ? [Providers.Deezer] : []);

    public IReadOnlyList<string> EffectiveBioProviders
        => BioProviders ?? (FetchArtists ? [Providers.AudioDb, Providers.Wikipedia, Providers.Wikidata] : []);

    public bool WriteLyrics => EffectiveLyricProviders.Count > 0;

    public bool WritePhotos => EffectivePhotoProviders.Count > 0;

    public bool WriteBios => EffectiveBioProviders.Count > 0;

    public bool WriteExplicitTags { get; set; } = true;

    public string ExplicitTags { get; set; } = "Explicit";

    public bool RenameExplicitTitles { get; set; } = true;

    public string ExplicitMark { get; set; } = "🅴";

    /// <summary>append or prepend.</summary>
    public string ExplicitMarkPlacement { get; set; } = "append";

    public IReadOnlyList<string> EffectiveExplicitTags
        => (ExplicitTags ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool PrependExplicitMark
        => string.Equals(ExplicitMarkPlacement, "prepend", StringComparison.OrdinalIgnoreCase);
}
