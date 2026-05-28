using Avalonia.Threading;
using Xenon.Core.Services;

namespace Xenon.Core.ViewModels;

public partial class SearchPageViewModel : ViewModelBase, IPage
{
    private readonly WebSearchService _webSearchService;
    private readonly IPlayback _playback;
    private readonly IPlaylistManager _playlistManager;

    public string Name => "Search";
    public PageType Type => PageType.Search;
    
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _hasSearchText;
    [ObservableProperty] private ObservableCollection<TrackGroupItemModel>? _albums;
    [ObservableProperty] private ObservableCollection<TrackGroupItemModel>? _artists;
    [ObservableProperty] private ObservableCollection<TrackItemModel>? _tracks;
    [ObservableProperty] private int _playingTrackId;
    [ObservableProperty] private string _playingTrackTitle = string.Empty;
    [ObservableProperty] private string _playingTrackArtist = string.Empty;
    [ObservableProperty] private string _playingTimeString = string.Empty;
    [ObservableProperty] private double _playingDuration;
    [ObservableProperty] private double _playingTime;
    [ObservableProperty] private string _playingImage = string.Empty;
    [ObservableProperty] private bool _playing;
    [ObservableProperty] private bool _isPlaybackPreparing;
    

    public SearchPageViewModel(WebSearchService webSearchService, IPlayback playback, IPlaylistManager playlistManager)
    {
        _webSearchService = webSearchService;
        _playback = playback;
        _playlistManager = playlistManager;
        _playback.SongChanged += PlaybackOnSongChanged;
        _playback.TimeChanged += PlaybackOnTimeChanged;
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.PreparingChanged += PlaybackOnPreparingChanged;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SearchText != null)
        {
            var result = await _webSearchService.SearchAsync(SearchText);

            Albums = new ObservableCollection<TrackGroupItemModel>(result.Albums);
            Artists = new ObservableCollection<TrackGroupItemModel>(result.Artists);
            Tracks = new ObservableCollection<TrackItemModel>(result.Tracks);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }
    
    [RelayCommand]
    private void TogglePlay()
    {
        _playback.Pause();
    }
    
    [RelayCommand]
    private async Task PlayAsync(TrackItemModel item)
    {
        _playback.Stop();
        if (Tracks is { Count: > 0 })
        {
            _playlistManager.SetQueue(Tracks, item);
        }

        await _playback.Play(item);
    }

    [RelayCommand]
    private async Task PlayAllTracksAsync()
    {
        _playback.Stop();
        if (Tracks is not { Count: > 0 })
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
        _playback.Stop();
        if (Tracks is not { Count: > 0 })
        {
            return;
        }

        var shuffledTracks = Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        var firstTrack = shuffledTracks[0];
        _playlistManager.SetQueue(shuffledTracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    private void PlaybackOnSongChanged(TrackItemModel obj)
    {
        IsPlaybackPreparing = false;
        
        PlayingTrackId = obj.Id;
        PlayingTrackTitle = obj.Name;
        PlayingTrackArtist = obj.Artist;
        PlayingDuration = obj.Time.TotalSeconds;
        PlayingImage = obj.Image;
    }

    private void PlaybackOnTimeChanged(long obj)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PlayingTime = obj;
            PlayingTimeString = TimeSpan.FromSeconds(obj).ToString("mm\\:ss");
        });
    }

    private void PlaybackOnStateChanged(bool obj)
    {
        Playing = obj;
    }

    private void PlaybackOnPreparingChanged(TrackItemModel? obj)
    {
        IsPlaybackPreparing = obj is not null;
        if (obj is null)
        {
            if (_playback.PlayingTrackId == 0)
            {
                PlayingTrackId = 0;
            }

            return;
        }

        PlayingTrackId = obj.Id;
        PlayingTrackTitle = obj.Name;
        PlayingTrackArtist = obj.Artist;
        PlayingDuration = obj.Time.TotalSeconds;
        PlayingTime = 0;
        PlayingTimeString = "00:00";
        PlayingImage = obj.Image;
    }

    partial void OnSearchTextChanged(string? value)
    {
        HasSearchText = !string.IsNullOrEmpty(value);
    }
}
