using Xenon.Core.Services;

namespace Xenon.Core.ViewModels;

public partial class CollectionBrowsePageViewModel : ViewModelBase, IPage
{
    private readonly WebSearchService _webSearchService;
    private readonly IPlayback _playback;
    private readonly IPlaylistManager _playlistManager;
    private int _selectionRefreshVersion;

    public string Name => "Collections";
    public PageType Type => PageType.CollectionBrowse;
    
    [ObservableProperty] private ObservableCollection<TrackGroupItemModel> _collections;
    [ObservableProperty] private string? _selectedCollectionImage;
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private int _playingTrackId;

    public CollectionBrowsePageViewModel(WebSearchService webSearchService, IPlayback playback, IPlaylistManager playlistManager)
    {
        _webSearchService = webSearchService;
        _playback = playback;
        _playlistManager = playlistManager;
        _playback.SongChanged += PlaybackOnSongChanged;
    }

    partial void OnCollectionsChanged(ObservableCollection<TrackGroupItemModel> value)
    {
        _ = RefreshSelectedCollectionAsync(SelectedIndex);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        _ = RefreshSelectedCollectionAsync(value);
        SelectedCollectionImage = Collections[value].Image;
    }

    private async Task RefreshSelectedCollectionAsync(int selectedIndex)
    {
        var version = ++_selectionRefreshVersion;
        if (Collections.Count == 0)
        {
            return;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, Collections.Count - 1);
        for (var index = 0; index < Collections.Count; index++)
        {
            if (index != selectedIndex)
            {
                Collections[index].Tracks = null!;
            }
        }

        var selectedCollection = Collections[selectedIndex];
        if (selectedCollection.Tracks is { Count: > 0 })
        {
            return;
        }

        var tracks = await _webSearchService.GetCollectionTracksAsync(selectedCollection.Url);
        if (version == _selectionRefreshVersion)
        {
            selectedCollection.Tracks = new ObservableCollection<TrackItemModel>(tracks);
        }

        
    }

    [RelayCommand]
    private async Task PlayAsync(TrackItemModel item)
    {
        _playback.Stop();
        var tracks = await GetSelectedCollectionTracksAsync();
        if (tracks.Count > 0)
        {
            _playlistManager.SetQueue(tracks, item);
        }

        await _playback.Play(item);
    }

    [RelayCommand]
    private async Task PlaySelectedCollectionAsync()
    {
        _playback.Stop();
        var tracks = await GetSelectedCollectionTracksAsync();
        if (tracks.Count == 0)
        {
            return;
        }

        var firstTrack = tracks[0];
        _playlistManager.SetQueue(tracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    [RelayCommand]
    private async Task ShuffleSelectedCollectionAsync()
    {
        _playback.Stop();
        var tracks = await GetSelectedCollectionTracksAsync();
        if (tracks.Count == 0)
        {
            return;
        }

        var shuffledTracks = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        var firstTrack = shuffledTracks[0];
        _playlistManager.SetQueue(shuffledTracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    private async Task<IReadOnlyList<TrackItemModel>> GetSelectedCollectionTracksAsync()
    {
        if (Collections.Count == 0)
        {
            return [];
        }

        var selectedIndex = Math.Clamp(SelectedIndex, 0, Collections.Count - 1);
        var selectedCollection = Collections[selectedIndex];
        if (selectedCollection.Tracks is not { Count: > 0 })
        {
            var tracks = await _webSearchService.GetCollectionTracksAsync(selectedCollection.Url);
            selectedCollection.Tracks = new ObservableCollection<TrackItemModel>(tracks);
        }

        return selectedCollection.Tracks ?? [];
    }

    private void PlaybackOnSongChanged(TrackItemModel obj)
    {
        PlayingTrackId = obj.Id;
    }
}
