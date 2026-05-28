using Xenon.Core.ViewModels;

namespace Xenon.Core.Services;

public sealed class PlaylistModel
{
    public PlaylistModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<TrackItemModel> Tracks { get; } = [];
}

public sealed class PlaylistManager : IPlaylistManager
{
    private readonly List<PlaylistModel> _playlists;
    private int _currentIndex = -1;

    public PlaylistManager()
    {
        PlaybackQueue = new PlaylistModel("Playback queue");
        _playlists = [PlaybackQueue];
    }

    public PlaylistModel PlaybackQueue { get; }
    public IReadOnlyList<PlaylistModel> Playlists => _playlists;
    public TrackItemModel? CurrentTrack => IsCurrentIndexValid ? PlaybackQueue.Tracks[_currentIndex] : null;
    public bool HasNextTrack => _currentIndex >= 0 && _currentIndex < PlaybackQueue.Tracks.Count - 1;
    public bool HasPreviousTrack => _currentIndex > 0 && _currentIndex < PlaybackQueue.Tracks.Count;
    public event Action? QueueChanged;

    public void SetQueue(IEnumerable<TrackItemModel> tracks, TrackItemModel? currentTrack = null)
    {
        PlaybackQueue.Tracks.Clear();
        foreach (var track in tracks.Where(track => track is not null))
        {
            PlaybackQueue.Tracks.Add(track);
        }

        if (PlaybackQueue.Tracks.Count == 0)
        {
            _currentIndex = -1;
        }
        else if (currentTrack is not null)
        {
            _currentIndex = FindTrackIndex(currentTrack);
            if (_currentIndex < 0)
            {
                PlaybackQueue.Tracks.Insert(0, currentTrack);
                _currentIndex = 0;
            }
        }
        else
        {
            _currentIndex = 0;
        }

        QueueChanged?.Invoke();
    }

    public void AddToQueue(IEnumerable<TrackItemModel> tracks)
    {
        foreach (var track in tracks.Where(track => track is not null && FindTrackIndex(track) < 0))
        {
            PlaybackQueue.Tracks.Add(track);
        }

        if (_currentIndex < 0 && PlaybackQueue.Tracks.Count > 0)
        {
            _currentIndex = 0;
        }

        QueueChanged?.Invoke();
    }

    public void SetCurrent(TrackItemModel track)
    {
        var index = FindTrackIndex(track);
        if (index < 0)
        {
            PlaybackQueue.Tracks.Add(track);
            index = PlaybackQueue.Tracks.Count - 1;
        }

        _currentIndex = index;
        QueueChanged?.Invoke();
    }

    public TrackItemModel? MoveNext()
    {
        if (!HasNextTrack)
        {
            return null;
        }

        _currentIndex++;
        QueueChanged?.Invoke();
        return CurrentTrack;
    }

    public TrackItemModel? MovePrevious()
    {
        if (!HasPreviousTrack)
        {
            return null;
        }

        _currentIndex--;
        QueueChanged?.Invoke();
        return CurrentTrack;
    }

    private bool IsCurrentIndexValid => _currentIndex >= 0 && _currentIndex < PlaybackQueue.Tracks.Count;

    private int FindTrackIndex(TrackItemModel track)
    {
        for (var index = 0; index < PlaybackQueue.Tracks.Count; index++)
        {
            if (PlaybackQueue.Tracks[index].Id == track.Id)
            {
                return index;
            }
        }

        return -1;
    }
}
