using Xenon.Core.Services;

namespace Xenon.Core.ViewModels;

public partial class AlbumPageViewModel : ViewModelBase, IPage
{
    private readonly WebSearchService _webSearchService;
    private readonly IPlayback _playback;
    private readonly IPlaylistManager _playlistManager;
    
    public string Name => "Album view";
    public PageType Type => PageType.Album;

    [ObservableProperty] private TrackGroupItemModel _album;
    [ObservableProperty] private ObservableCollection<TrackItemModel>  _tracks;

    public AlbumPageViewModel(WebSearchService webSearchService, IPlayback playback, IPlaylistManager playlistManager)
    {
        _webSearchService = webSearchService;
        _playback = playback;
        _playlistManager = playlistManager;
    }
    
    public async Task InitAsync(TrackGroupItemModel item)
    {
        Tracks = new ObservableCollection<TrackItemModel>();
        Album = item;
        var tracks = await _webSearchService.GetCollectionTracksAsync(Album.Url);
        foreach (var track in tracks)
            Tracks.Add(track);
    }
    
    [RelayCommand]
    private async Task PlayAsync(TrackItemModel item)
    {
        if (Tracks.Count > 0)
        {
            _playlistManager.SetQueue(Tracks, item);
        }

        await _playback.Play(item);
    }

    [RelayCommand]
    private async Task PlayAllTracksAsync()
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        var firstTrack = Tracks[0];
        _playlistManager.SetQueue(Tracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    [RelayCommand]
    private async Task ShuffleTracksAsync()
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        var shuffledTracks = Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        var firstTrack = shuffledTracks[0];
        _playlistManager.SetQueue(shuffledTracks, firstTrack);
        await _playback.Play(firstTrack);
    }
}
