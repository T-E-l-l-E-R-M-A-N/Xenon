using System.ComponentModel;
using AngleSharp;
using CommunityToolkit.Mvvm.DependencyInjection;
using Newtonsoft.Json;
using Xenon.Core.Services;

namespace Xenon.Core.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private Stack<IPage> _history = new Stack<IPage>();
    private readonly IPlayback _playback;
    [ObservableProperty] private IPage _currentPage;
    [ObservableProperty] private MiniPlayerBarViewModel _miniPlayer;
    [ObservableProperty] private bool _isMiniPlayerBarVisible;
    
    public MainViewModel(MiniPlayerBarViewModel miniPlayer, IPlayback playback)
    {
        _miniPlayer = miniPlayer;
        _playback = playback;
        _playback.SongChanged += _ => UpdateMiniPlayerBarVisibility();
        _playback.StateChanged += _ => UpdateMiniPlayerBarVisibility();
        _playback.PreparingChanged += _ => UpdateMiniPlayerBarVisibility();
    }

    public void Init()
    {
        MiniPlayer.Init();
        PropertyChanged += OnPropertyChanged;
        var homePage = IoC.Resolve<HomePageViewModel>();
        _history.Push(homePage);
        CurrentPage = homePage;
        homePage.InitAsync();
        UpdateMiniPlayerBarVisibility();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurrentPage))
        {
            ReturnBackCommand.NotifyCanExecuteChanged();
            OpenSearchCommand.NotifyCanExecuteChanged();
        }
    }

    public void OpenPage(IPage page)
    {
        _history.Push(CurrentPage);
        CurrentPage = page;

    }

    private bool CanReturnToPrevious() => CurrentPage is not HomePageViewModel;

    [RelayCommand(CanExecute = "CanReturnToPrevious")]
    public void ReturnBack()
    {
        if (CurrentPage is not HomePageViewModel)
        {
            var page = _history.Pop();
            CurrentPage = page;
        }
    }

    private bool CanOpenSearch() => CurrentPage is not SearchPageViewModel;

    [RelayCommand(CanExecute = "CanOpenSearch")]
    private void OpenSearch()
    {
        OpenPage(IoC.Resolve<SearchPageViewModel>());
    }

    partial void OnCurrentPageChanged(IPage value)
    {
        UpdateMiniPlayerBarVisibility();
    }

    private void UpdateMiniPlayerBarVisibility()
    {
        IsMiniPlayerBarVisible =
            CurrentPage is not null &&
            CurrentPage.Type != PageType.Home &&
            CurrentPage.Type != PageType.Playback &&
            (_playback.PlayingTrackId != 0 || _playback.IsPreparing);
    }
}

public sealed class WebSearchService
{
    private readonly string _url = "https://rus.hitmotop.com";
    private readonly IConfiguration _configuration = new Configuration().WithDefaultLoader();
    private IBrowsingContext _browsingContext = null!;

    public void Init()
    {
        _browsingContext = new BrowsingContext(_configuration);
    }
    public async Task<List<TrackGroupItemModel>> GetCollectionsAsync()
    {
        var document = await _browsingContext.OpenAsync(_url);
        var sideBarAlbums = document.QuerySelector("ul.sidebar-album");
        if (sideBarAlbums != null)
        {
            var albums = sideBarAlbums
                .QuerySelectorAll("li");
        
            var list = new List<TrackGroupItemModel>();

            foreach (var element in albums)
            {
                var href = element.QuerySelector("a").GetAttribute("href");
                var id = int.Parse(href.Split('/').LastOrDefault());
                var name = element.QuerySelector(".sidebar-album-title").TextContent;
                var image = element.QuerySelector(".sidebar-album-image").
                    GetAttribute("style")
                    .Replace("background-image: url('", "")
                    .Replace("')", "");
            
                list.Add(new TrackGroupItemModel()
                {
                    Id = id,
                    Name = name,
                    Url = _url + href,
                    Image = image
                });
            }
        
            return list;
        }

        return [];
    }
    public async Task<List<TrackItemModel>> GetCollectionTracksAsync(string collectionUrl)
    {
        var document = await _browsingContext.OpenAsync(collectionUrl);
        var items = document.QuerySelector("ul.tracks__list")
            .QuerySelectorAll("li");
        var list = new List<TrackItemModel>();

        foreach (var element in items)
        {
            var meta = element.GetAttribute("data-musmeta");
            var metaDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(meta);
            var id = int.Parse(metaDict["id"].Replace("track-id-", ""));
            var name = metaDict["title"];
            var artist = metaDict["artist"];
            var url = metaDict["url"];
            var image = metaDict["img"];
            if (image.Contains("no-cover"))
                image = "";
            var time = TimeSpan.Parse($"00:{element.QuerySelector(".track__fulltime").TextContent}");

            list.Add(new TrackItemModel()
            {
                Id = id,
                Name = name,
                Artist = artist,
                Url = url,
                Image = image,
                Time = time
            });
        }
        
        
        return list;
    }

    public async Task<SearchResultModel> SearchAsync(string text)
    {
        var document = await _browsingContext.OpenAsync(_url + "/search?q=" + text.Replace(" ", "%20"));
        var resultModel = new SearchResultModel()
        {
            Tracks = [],
            Albums = [],
            Artists = []
        };

        var tracksList = document.QuerySelector("ul.tracks__list");
        if (tracksList != null)
        {
            var trackListItems = tracksList
                .QuerySelectorAll("li");

            foreach (var element in trackListItems)
            {
                var meta = element.GetAttribute("data-musmeta");
                var metaDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(meta);
                var id = int.Parse(metaDict["id"].Replace("track-id-", ""));
                var name = metaDict["title"];
                var artist = metaDict["artist"];
                var url = metaDict["url"];
                var image = metaDict["img"];
                if (image.Contains("no-cover"))
                    image = "";
                var time = TimeSpan.Parse($"00:{(element.QuerySelector(".track__fulltime").TextContent is string s && s != "00:60" ? s : "01:00")}");

                resultModel.Tracks.Add(new TrackItemModel()
                {
                    Id = id,
                    Name = name,
                    Artist = artist,
                    Url = url,
                    Image = image,
                    Time = time
                });
            }
        }

        var querySelectorSingerList = document.QuerySelector("ul.singers-list.album-list");
        if (querySelectorSingerList != null)
        {
            var artistListItems = querySelectorSingerList
                .QuerySelectorAll("li");

            foreach (var element in artistListItems)
            {
                var href = element.QuerySelector("a").GetAttribute("href");
                var id = int.Parse(href.Split('/').LastOrDefault());
                var name = element.QuerySelector(".album-title").TextContent;
                var image = "https:" + element.QuerySelector(".album-image").
                    GetAttribute("style")
                    .Replace("background-image: url('", "")
                    .Replace("')", "");
            
                resultModel.Artists.Add(new TrackGroupItemModel()
                {
                    Id = id,
                    Name = name,
                    Url = _url + href,
                    Image = image
                });
            }
        }

        var querySelectorAlbumList = document.QuerySelectorAll("ul.album-list");
        if (querySelectorAlbumList != null)
        {
            var singersList = querySelectorAlbumList
                .FirstOrDefault(x=>!x.ClassList.Contains("singers-list"));
            if (singersList != null)
            {
                var albumsListItems = singersList
                    .QuerySelectorAll("li");

                foreach (var element in albumsListItems)
                {
                    var href = element.QuerySelector("a").GetAttribute("href");
                    var id = int.Parse(href.Split('/').LastOrDefault());
                    var name = element.QuerySelector(".album-title").TextContent.Trim().Replace("\n                         ", " ");
                    var image = element.QuerySelector(".album-image").
                        GetAttribute("style")
                        .Replace("background-image: url('", "")
                        .Replace("')", "");
            
                    resultModel.Albums.Add(new TrackGroupItemModel()
                    {
                        Id = id,
                        Name = name,
                        Url = _url + href,
                        Image = image
                    });
                }
            }
        }

        return resultModel;
    }
}

public class SearchResultModel
{
    public List<TrackGroupItemModel> Artists { get; init; }
    public List<TrackGroupItemModel> Albums  { get; init; }
    public List<TrackItemModel> Tracks { get; init; }
}
public class TrackItemModel
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Artist { get; set; }
    public TimeSpan Time { get; set; }
    public string Url { get; set; }
    public string Image  { get; set; }
}

public partial class TrackGroupItemModel : ObservableObject
{
    [ObservableProperty] private int _id;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _image;
    [ObservableProperty] private ObservableCollection<TrackItemModel>? _tracks;
}
