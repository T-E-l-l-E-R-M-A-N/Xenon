using Xenon.Core.ViewModels;

namespace Xenon.Core.Services;

public interface IPlaylistManager
{
    PlaylistModel PlaybackQueue { get; }
    IReadOnlyList<PlaylistModel> Playlists { get; }
    TrackItemModel? CurrentTrack { get; }
    bool HasNextTrack { get; }
    bool HasPreviousTrack { get; }
    event Action? QueueChanged;

    void SetQueue(IEnumerable<TrackItemModel> tracks, TrackItemModel? currentTrack = null);
    void AddToQueue(IEnumerable<TrackItemModel> tracks);
    void SetCurrent(TrackItemModel track);
    TrackItemModel? MoveNext();
    TrackItemModel? MovePrevious();
}
