using Xenon.Core.ViewModels;

namespace Xenon.Core.Services;

public interface IPlayback
{
    int PlayingTrackId { get; set; }
    event Action<long> TimeChanged;
    event Action<bool> StateChanged;
    event Action<TrackItemModel?> PreparingChanged;
    event Action<TrackItemModel>? SongChanged;
    event Action? PlayFinished;
    bool IsPaused { get; set; }
    bool IsPreparing { get; }
    Task Play(TrackItemModel s);
    Task<bool> PlayNext();
    Task<bool> PlayPrevious();
    void Pause();
    void SeekTo(long newTime);
    void Init();
    void Stop();
}
