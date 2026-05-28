using Xenon.Core.ViewModels;

namespace Xenon.Core.Services;

public interface IRecentTracksStore
{
    IReadOnlyList<TrackItemModel> LoadRecentTracks(int count);
    void SaveRecentTracks(IEnumerable<TrackItemModel> tracks);
}
