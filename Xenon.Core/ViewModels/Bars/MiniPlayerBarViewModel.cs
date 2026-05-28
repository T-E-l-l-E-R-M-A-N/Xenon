using Avalonia.Threading;
using Xenon.Core.Services;

namespace Xenon.Core.ViewModels;

public partial class MiniPlayerBarViewModel : ObservableObject
{
    private readonly IPlayback _playback;
    private readonly IPlaylistManager _playlistManager;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _timeString;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _time;

    [ObservableProperty] private bool _playing;
    [ObservableProperty] private bool _isPlaybackPreparing;
    [ObservableProperty] private bool _hasNextTrack;

    public MiniPlayerBarViewModel(IPlayback playback, IPlaylistManager playlistManager)
    {
        _playback = playback;
        _playlistManager = playlistManager;
    }

    public void Init()
    {
        _playback.StateChanged += PlaybackOnStateChanged;
        _playback.TimeChanged += PlaybackOnTimeChanged;
        _playback.PreparingChanged += PlaybackOnPreparingChanged;
        _playback.SongChanged += PlaybackOnSongChanged;
        _playlistManager.QueueChanged += PlaylistManagerOnQueueChanged;
        UpdateQueueState();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        _playback.Pause();
    }

    [RelayCommand]
    private async Task NextTrackAsync()
    {
        await _playback.PlayNext();
    }
    
    
    private void PlaybackOnSongChanged(TrackItemModel obj)
    {
        IsPlaybackPreparing = false;
        Duration = obj.Time.TotalSeconds;
        Title = obj.Name;
        UpdateQueueState();
    }

    private void PlaybackOnPreparingChanged(TrackItemModel? obj)
    {
        IsPlaybackPreparing = obj is not null;
        if (obj is null)
        {
            return;
        }

        Time = 0;
        TimeString = "00:00";
        Duration = obj.Time.TotalSeconds;
        Title = obj.Name;
        UpdateQueueState();
    }

    private void PlaybackOnTimeChanged(long obj)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Time = obj;
            TimeString = TimeSpan.FromSeconds(obj).ToString("mm\\:ss");
        });
    }

    private void PlaybackOnStateChanged(bool obj)
    {
        Playing = obj;
    }

    private void PlaylistManagerOnQueueChanged()
    {
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        HasNextTrack = _playlistManager.HasNextTrack;
    }
}
