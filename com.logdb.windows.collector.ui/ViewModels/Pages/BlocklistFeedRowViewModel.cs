using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

/// <summary>
/// One editable row in the firewall blocklist editor. Maps to a single entry
/// in <c>FirewallConfigDto.PublicBlocklists</c>: the dictionary key becomes
/// <see cref="FeedId"/>, the value becomes the rest. Operators add new rows
/// for custom feeds and toggle <see cref="Enabled"/> to disable feeds without
/// losing the URL.
/// </summary>
public sealed class BlocklistFeedRowViewModel : ObservableObject
{
    private string _feedId = string.Empty;
    private string _displayName = string.Empty;
    private string _url = string.Empty;
    private bool _enabled = true;
    private int _minScore;

    public string FeedId
    {
        get => _feedId;
        set => SetProperty(ref _feedId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public int MinScore
    {
        get => _minScore;
        set => SetProperty(ref _minScore, value);
    }
}
