using LiteDB;
using Xenon.Core.ViewModels;

namespace Xenon.Core.Services;

public sealed class RecentTracksStore : IRecentTracksStore
{
    private const string CollectionName = "recent_tracks";
    private readonly string _connectionString;

    public RecentTracksStore()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Xenon");

        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "xenon.db");
        _connectionString = $"Filename={databasePath};Mode=Exclusive";
    }

    public IReadOnlyList<TrackItemModel> LoadRecentTracks(int count)
    {
        try
        {
            using var database = new LiteDatabase(_connectionString);
            var collection = database.GetCollection<RecentTrackRecord>(CollectionName);

            return collection
                .FindAll()
                .OrderBy(record => record.Position)
                .Take(Math.Max(0, count))
                .Select(record => new TrackItemModel
                {
                    Id = record.TrackId,
                    Name = record.Name ?? string.Empty,
                    Artist = record.Artist ?? string.Empty,
                    Time = TimeSpan.FromTicks(record.TimeTicks),
                    Url = record.Url ?? string.Empty,
                    Image = record.Image ?? string.Empty
                })
                .ToList();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return [];
        }
    }

    public void SaveRecentTracks(IEnumerable<TrackItemModel> tracks)
    {
        try
        {
            using var database = new LiteDatabase(_connectionString);
            var collection = database.GetCollection<RecentTrackRecord>(CollectionName);

            collection.Delete(Query.All());
            var position = 0;
            foreach (var track in tracks.Where(track => track is not null).Take(4))
            {
                collection.Insert(new RecentTrackRecord
                {
                    Id = ++position,
                    Position = position,
                    TrackId = track.Id,
                    Name = track.Name,
                    Artist = track.Artist,
                    TimeTicks = track.Time.Ticks,
                    Url = track.Url,
                    Image = track.Image
                });
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
    }

    private sealed class RecentTrackRecord
    {
        public int Id { get; set; }
        public int Position { get; set; }
        public int TrackId { get; set; }
        public string? Name { get; set; }
        public string? Artist { get; set; }
        public long TimeTicks { get; set; }
        public string? Url { get; set; }
        public string? Image { get; set; }
    }
}
