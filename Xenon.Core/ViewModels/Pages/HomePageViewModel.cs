using Xenon.Core.Services;
using System.Threading;
using Avalonia.Threading;

namespace Xenon.Core.ViewModels;

public partial class HomePageViewModel : ViewModelBase, IPage
{
    private readonly WebSearchService _webSearchService;
    private readonly IPlayback _playback;
    private readonly IPlaylistManager _playlistManager;
    private readonly IRecentTracksStore _recentTracksStore;
    private int _demoCollectionIndex = -1;
    private int _searchSuggestionVersion;
    private CancellationTokenSource? _searchSuggestionCancellation;
    private CancellationTokenSource? _tileRotationCancellation;
    private int _tileRotationSize = 2;
    private List<TrackItemModel> _demoCollectionTracks = [];
    public string Name => "Listen now";
    public PageType Type => PageType.Home;

    [ObservableProperty] private ObservableCollection<TrackGroupItemModel> _collections = [];
    [ObservableProperty] private ObservableCollection<TrackItemModel> _tracks = [];
    [ObservableProperty] private ObservableCollection<HomeCollectionTileSlot> _collectionTiles = [];
    [ObservableProperty] private ObservableCollection<HomeTrackTileSlot> _trackTiles = [];
    [ObservableProperty] private SearchPageViewModel? _searchPageViewModel;
    [ObservableProperty] private TrackItemModel? _firstRecentItem;
    [ObservableProperty] private TrackItemModel? _secondRecentItem;
    [ObservableProperty] private TrackItemModel? _thirdRecentItem;
    [ObservableProperty] private TrackItemModel? _fourthRecentItem;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _hasSearchText;
    [ObservableProperty] private ObservableCollection<TrackItemModel> _searchSuggestionTracks = [];
    [ObservableProperty] private bool _isSearchPopupOpen;

    [ObservableProperty] private int _playingTrackId;
    [ObservableProperty] private string _playingTrackTitle = string.Empty;
    [ObservableProperty] private string _playingTrackArtist = string.Empty;
    [ObservableProperty] private string _playingTimeString = string.Empty;
    [ObservableProperty] private double _playingDuration;
    [ObservableProperty] private double _playingTime;
    [ObservableProperty] private string _playingImage = string.Empty;
    [ObservableProperty] private bool _playing;
    [ObservableProperty] private bool _isPlaybackPreparing;

    public HomePageViewModel(
        WebSearchService webSearchService,
        IPlayback playback,
        IPlaylistManager playlistManager,
        IRecentTracksStore recentTracksStore,
        SearchPageViewModel? searchPageViewModel)
    {
        _webSearchService = webSearchService;
        _playback = playback;
        _playlistManager = playlistManager;
        _recentTracksStore = recentTracksStore;
        _searchPageViewModel = searchPageViewModel;
    }
    
    public async Task InitAsync()
    {
        _playback.SongChanged += PlaybackOnSongChanged;
        _playback.TimeChanged += PlaybackOnTimeChanged;
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.PreparingChanged += PlaybackOnPreparingChanged;
        _webSearchService.Init();
        Collections = [];
        Tracks = [];
        CollectionTiles = [];
        TrackTiles = [];
        LoadRecentTracks();
        var collections = await _webSearchService.GetCollectionsAsync();
        foreach (var collection in collections)
            Collections?.Add(collection);

        if (Collections is { Count: > 0 } availableCollections)
        {
            _demoCollectionIndex = Random.Shared.Next(availableCollections.Count);
            var randCollection = availableCollections[_demoCollectionIndex];
            var tracks = await _webSearchService.GetCollectionTracksAsync(randCollection.Url);
            _demoCollectionTracks = tracks;
            foreach (var trackItemModel in _demoCollectionTracks)
                Tracks.Add(trackItemModel);
        }

        FillCollectionTiles();
        FillTrackTiles();
        StartTileRotation();
    }

    [RelayCommand]
    private async Task OpenAlbumAsync(TrackGroupItemModel item)
    {
        var albumPage = IoC.Resolve<AlbumPageViewModel>();
        await albumPage.InitAsync(item);
        IoC.Resolve<MainViewModel>().OpenPage(albumPage);
    }

    [RelayCommand]
    private async Task StartSearchAsync(string text)
    {
        IsSearchPopupOpen = false;
        var searchPage = IoC.Resolve<SearchPageViewModel>();
        searchPage.SearchText = text;
        await searchPage.SearchCommand.ExecuteAsync(null);
        IoC.Resolve<MainViewModel>().OpenPage(searchPage);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        SearchSuggestionTracks.Clear();
        IsSearchPopupOpen = false;
    }

    [RelayCommand]
    private async Task PlayAsync(TrackItemModel item)
    {
        _playback.Stop();
        PlayingTrackId = 0;
        if (_demoCollectionTracks.Count > 0)
        {
            _playlistManager.SetQueue(_demoCollectionTracks, item);
        }

        await _playback.Play(item);
    }

    [RelayCommand]
    private async Task PlaySearchSuggestionAsync(TrackItemModel item)
    {
        _playback.Stop();
        PlayingTrackId = 0;
        if (SearchSuggestionTracks.Count > 0)
        {
            _playlistManager.SetQueue(SearchSuggestionTracks, item);
        }

        IsSearchPopupOpen = false;
        await _playback.Play(item);
    }

    [RelayCommand]
    private async Task PlayAllTracksAsync()
    {
        _playback.Stop();
        if (_demoCollectionTracks.Count == 0)
        {
            return;
        }

        var firstTrack = _demoCollectionTracks[0];
        _playlistManager.SetQueue(_demoCollectionTracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    [RelayCommand]
    private async Task ShuffleTracksAsync()
    {
        _playback.Stop();
        if (_demoCollectionTracks.Count == 0)
        {
            return;
        }

        var shuffledTracks = _demoCollectionTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        var firstTrack = shuffledTracks[0];
        _playlistManager.SetQueue(shuffledTracks, firstTrack);
        await _playback.Play(firstTrack);
    }

    [RelayCommand]
    private async Task OpenCollection(TrackGroupItemModel trackGroup)
    {
        var collectionBrowsePage = IoC.Resolve<CollectionBrowsePageViewModel>();
        var collections = await _webSearchService.GetCollectionsAsync();
        if(collectionBrowsePage.Collections == null) collectionBrowsePage.Collections = new ObservableCollection<TrackGroupItemModel>(collections);
        var index = collectionBrowsePage.Collections.IndexOf(
            collectionBrowsePage.Collections.FirstOrDefault(x => x.Id == trackGroup.Id));
        IoC.Resolve<MainViewModel>().OpenPage(collectionBrowsePage);
        collectionBrowsePage.SelectedIndex = index;
    }

    [RelayCommand]
    private async Task OpenNthCollection()
    {
        var collectionBrowsePage = IoC.Resolve<CollectionBrowsePageViewModel>();
        var collections = await _webSearchService.GetCollectionsAsync();
        if(collectionBrowsePage.Collections == null) collectionBrowsePage.Collections = new ObservableCollection<TrackGroupItemModel>(collections);
        IoC.Resolve<MainViewModel>().OpenPage(collectionBrowsePage);
        collectionBrowsePage.SelectedIndex = _demoCollectionIndex;
    }
    
    [RelayCommand]
    private void TogglePlay()
    {
        _playback.Pause();
    }

    private void PlaybackOnSongChanged(TrackItemModel obj)
    {
        IsPlaybackPreparing = false;
        var recentTracks = new[]
            {
                obj,
                FirstRecentItem,
                SecondRecentItem,
                ThirdRecentItem,
                FourthRecentItem
            }
            .Where(track => track is not null)
            .DistinctBy(track => track!.Id)
            .OfType<TrackItemModel>()
            .Take(4)
            .ToArray();

        SetRecentTracks(recentTracks);
        _recentTracksStore.SaveRecentTracks(recentTracks);
        
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
        _ = UpdateSearchSuggestionsAsync(value);
    }

    private async Task UpdateSearchSuggestionsAsync(string? text)
    {
        _searchSuggestionCancellation?.Cancel();
        _searchSuggestionCancellation?.Dispose();
        _searchSuggestionCancellation = new CancellationTokenSource();
        var token = _searchSuggestionCancellation.Token;
        var version = ++_searchSuggestionVersion;

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length <= 2)
        {
            SearchSuggestionTracks.Clear();
            IsSearchPopupOpen = false;
            return;
        }

        try
        {
            await Task.Delay(300, token);
            var result = await _webSearchService.SearchAsync(text.Trim());
            if (token.IsCancellationRequested || version != _searchSuggestionVersion)
            {
                return;
            }

            SearchSuggestionTracks = new ObservableCollection<TrackItemModel>(result.Tracks.Take(8));
            IsSearchPopupOpen = SearchSuggestionTracks.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            if (version == _searchSuggestionVersion)
            {
                SearchSuggestionTracks.Clear();
                IsSearchPopupOpen = false;
            }
        }
    }

    private void LoadRecentTracks()
    {
        try
        {
            SetRecentTracks(_recentTracksStore.LoadRecentTracks(4));
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            SetRecentTracks([]);
        }
    }

    private void SetRecentTracks(IReadOnlyList<TrackItemModel?> recentTracks)
    {
        FirstRecentItem = recentTracks.Count > 0 ? recentTracks[0] : null;
        SecondRecentItem = recentTracks.Count > 1 ? recentTracks[1] : null;
        ThirdRecentItem = recentTracks.Count > 2 ? recentTracks[2] : null;
        FourthRecentItem = recentTracks.Count > 3 ? recentTracks[3] : null;
    }

    private void StartTileRotation()
    {
        _tileRotationCancellation?.Cancel();
        _tileRotationCancellation?.Dispose();
        _tileRotationCancellation = new CancellationTokenSource();
        var token = _tileRotationCancellation.Token;

        _ = RotateTilesAsync(token);
    }

    private async Task RotateTilesAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(7));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var size = _tileRotationSize;
                _tileRotationSize = _tileRotationSize == 2 ? 3 : 2;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RotateCollectionTiles(size);
                    RotateTrackTiles(size);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FillCollectionTiles()
    {
        CollectionTiles.Clear();
        foreach (var collection in Collections.OrderBy(_ => Random.Shared.Next()).Take(6))
        {
            CollectionTiles.Add(new HomeCollectionTileSlot(collection));
        }
    }

    private void FillTrackTiles()
    {
        TrackTiles.Clear();
        foreach (var track in Tracks.OrderBy(_ => Random.Shared.Next()).Take(8))
        {
            TrackTiles.Add(new HomeTrackTileSlot(track));
        }
    }

    private void RotateCollectionTiles(int count)
    {
        if (Collections.Count <= CollectionTiles.Count)
        {
            return;
        }

        foreach (var index in GetRandomIndexes(CollectionTiles.Count, count))
        {
            var usedIds = CollectionTiles
                .Where((_, slotIndex) => slotIndex != index)
                .Select(slot => slot.Item?.Id)
                .Where(id => id is not null)
                .ToHashSet();

            var candidates = Collections
                .Where(collection => !usedIds.Contains(collection.Id) && collection.Id != CollectionTiles[index].Item?.Id)
                .ToList();

            if (candidates.Count > 0)
            {
                CollectionTiles[index].Item = candidates[Random.Shared.Next(candidates.Count)];
            }
        }
    }

    private void RotateTrackTiles(int count)
    {
        if (Tracks.Count <= TrackTiles.Count)
        {
            return;
        }

        foreach (var index in GetRandomIndexes(TrackTiles.Count, count))
        {
            var usedIds = TrackTiles
                .Where((_, slotIndex) => slotIndex != index)
                .Select(slot => slot.Item?.Id)
                .Where(id => id is not null)
                .ToHashSet();

            var candidates = Tracks
                .Where(track => !usedIds.Contains(track.Id) && track.Id != TrackTiles[index].Item?.Id)
                .ToList();

            if (candidates.Count > 0)
            {
                TrackTiles[index].Item = candidates[Random.Shared.Next(candidates.Count)];
            }
        }
    }

    private static IEnumerable<int> GetRandomIndexes(int max, int count)
    {
        return Enumerable.Range(0, max)
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Min(max, count));
    }
}

public sealed class HomeCollectionTileSlot : ObservableObject
{
    private TrackGroupItemModel? _item;

    public HomeCollectionTileSlot(TrackGroupItemModel? item)
    {
        _item = item;
    }

    public TrackGroupItemModel? Item
    {
        get => _item;
        set => SetProperty(ref _item, value);
    }
}

public sealed class HomeTrackTileSlot : ObservableObject
{
    private TrackItemModel? _item;

    public HomeTrackTileSlot(TrackItemModel? item)
    {
        _item = item;
    }

    public TrackItemModel? Item
    {
        get => _item;
        set => SetProperty(ref _item, value);
    }
}
