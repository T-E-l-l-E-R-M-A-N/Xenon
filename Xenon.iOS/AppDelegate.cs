using System;
using System.Net;
using Autofac;
using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using System.Threading.Tasks;
using System.Threading;
using Xenon.Core.Services;
using Xenon.Core.ViewModels;
using MediaManager;
using MediaManager.Library;
using MediaManager.Media;
using MediaManager.Playback;
using MediaManager.Player;
using MediaPlayer;
using Microsoft.Extensions.DependencyInjection;
using Projektanker.Icons.Avalonia.MaterialDesign;
using Xenon.Core;
using Xenon.UI;

namespace Kardamon.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        Projektanker.Icons.Avalonia.IconProvider.Current.Register<MaterialDesignIconProvider>();
        //CrossPushNotification.Current.RegisterForPushNotifications();
        var services = new ServiceCollection();
        services.AddScoped<IPlayback, XPlatformPlaybackService>();
        services.AddScoped<IKeyboardInsetService, IosKeyboardInsetService>();
        IoC.Build(services);
       // CheckNotifyPermission();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            ;
    }

}

public sealed class IosKeyboardInsetService : KeyboardInsetService
{
    public IosKeyboardInsetService()
    {
        UIKeyboard.Notifications.ObserveWillShow((_, args) => UpdateInset(args.FrameEnd));
        UIKeyboard.Notifications.ObserveWillChangeFrame((_, args) => UpdateInset(args.FrameEnd));
        UIKeyboard.Notifications.ObserveWillHide((_, _) => SetBottomInset(0));
    }

    private void UpdateInset(CGRect frameEnd)
    {
        var keyboardHeight = Math.Max(0, frameEnd.Height);
        SetBottomInset(keyboardHeight);
    }
}

public class XPlatformPlaybackService : IPlayback
{
    private const string CoverResourceName = "cover";
    private const string CoverResourceExtension = "jpg";
    private static readonly TimeSpan PositionUpdateInterval = TimeSpan.FromMilliseconds(500);
    private readonly IPlaylistManager _playlistManager;
    private int _playbackRequestVersion;
    private TrackItemModel? _nowPlayingTrack;
    private System.Threading.Timer? _positionTimer;
    private long _lastReportedPosition = -1;
    private static readonly Lazy<string?> CoverImageUri = new(CreateCoverImageUri);
    private static readonly Lazy<UIImage?> CoverImage = new(CreateCoverImage);
    
    public event Action<long>? TimeChanged;
    public event Action<bool>? StateChanged;
    public event Action<TrackItemModel?>? PreparingChanged;
    public event Action<TrackItemModel>? SongChanged;
    public event Action? PlayFinished;
    public bool IsPaused { get; set; }
    public bool IsPreparing { get; private set; }
    public int PlayingTrackId { get; set; }

    public XPlatformPlaybackService(IPlaylistManager playlistManager)
    {
        _playlistManager = playlistManager;
    }

    public async Task Play(TrackItemModel s)
    {
        if (string.IsNullOrWhiteSpace(s.Url))
        {
            throw new InvalidOperationException("The selected track does not have a playback path.");
        }

        var requestVersion = BeginPlaybackRequest(s);
        try
        {
            await RunOnMainThreadAsync(async () =>
            {
                await CrossMediaManager.Current.Stop();
                if (!IsCurrentPlaybackRequest(requestVersion))
                {
                    return;
                }

                var media = await CrossMediaManager.Current.Play(s.Url);
                if (!IsCurrentPlaybackRequest(requestVersion))
                {
                    await CrossMediaManager.Current.Stop();
                    return;
                }

                ApplyDefaultArtwork(media);
                UpdateSystemNowPlaying(s, 0, isPlaying: true);
                ScheduleSystemArtworkRefresh(s);
            });

            if (!IsCurrentPlaybackRequest(requestVersion))
            {
                return;
            }

            IsPaused = false;
            _playlistManager.SetCurrent(s);
            SongChanged?.Invoke(s);
            PlayingTrackId = s.Id;
            _nowPlayingTrack = s;
            StartPositionTimer();
            SetPreparing(null);
            StateChanged?.Invoke(true);
        }
        catch
        {
            if (IsCurrentPlaybackRequest(requestVersion))
            {
                SetPreparing(null);
            }

            throw;
        }
    }

    public async Task<bool> PlayNext()
    {
        var nextTrack = _playlistManager.MoveNext();
        if (nextTrack is null)
        {
            return false;
        }

        await Play(nextTrack);
        return true;
    }

    public async Task<bool> PlayPrevious()
    {
        var previousTrack = _playlistManager.MovePrevious();
        if (previousTrack is null)
        {
            return false;
        }

        await Play(previousTrack);
        return true;
    }

    private void CurrentOnMediaItemFinished(object? sender, MediaItemEventArgs e)
    {
        UIApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            if (await PlayNext())
            {
                return;
            }

            PlayFinished?.Invoke();
            PlayingTrackId = 0;
            _nowPlayingTrack = null;
            StopPositionTimer();
            IsPaused = true;
            StateChanged?.Invoke(false);
        });
    }

    private void CurrentOnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        ReportPosition(e.Position);
    }

    public void Pause()
    {
        UIApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            if (IsPaused == false)
            {
                await CrossMediaManager.Current.Pause();
                IsPaused = true;
                StopPositionTimer();
                UpdateSystemNowPlaying(_nowPlayingTrack, _lastReportedPosition, isPlaying: false);
            }
            else
            {
                await CrossMediaManager.Current.Play();
                IsPaused = false;
                StartPositionTimer();
                UpdateSystemNowPlaying(_nowPlayingTrack, _lastReportedPosition, isPlaying: true);
            }

            StateChanged?.Invoke(!IsPaused);
        });
    }

    public void SeekTo(long newTime)
    {
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            var position = TimeSpan.FromSeconds(newTime);
            CrossMediaManager.Current.SeekTo(position);
            ReportPosition(position);
            UpdateSystemNowPlaying(_nowPlayingTrack, newTime, isPlaying: !IsPaused);
        });
    }
    
    public void Stop()
    {
        CancelPlaybackRequest();
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _ = CrossMediaManager.Current.Stop();
            SetPreparing(null);
            PlayingTrackId = 0;
            _nowPlayingTrack = null;
            StopPositionTimer();
            IsPaused = true;
            StateChanged?.Invoke(false);
        });
    }

    public void Init()
    {
        CrossMediaManager.Current.Init();
        CrossMediaManager.Current.MediaItemFinished += CurrentOnMediaItemFinished;
        CrossMediaManager.Current.PositionChanged += CurrentOnPositionChanged;
        CrossMediaManager.Current.StateChanged += CurrentOnStateChanged;
        CrossMediaManager.Current.Notification.ShowNavigationControls = false;
    }

    private void CurrentOnStateChanged(object? sender, StateChangedEventArgs e)
    {
        var isPlaying =
            e.State == MediaPlayerState.Playing ||
            e.State == MediaPlayerState.Buffering ||
            e.State == MediaPlayerState.Loading;

        if (isPlaying && !IsPaused)
        {
            StartPositionTimer();
        }
        else if (!isPlaying)
        {
            StopPositionTimer();
        }

        StateChanged?.Invoke(isPlaying);
    }

    private void SetPreparing(TrackItemModel? track)
    {
        IsPreparing = track is not null;
        PreparingChanged?.Invoke(track);
    }

    private int BeginPlaybackRequest(TrackItemModel track)
    {
        CancelPlaybackRequest();
        var version = Interlocked.Increment(ref _playbackRequestVersion);
        SetPreparing(track);
        StateChanged?.Invoke(false);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _ = CrossMediaManager.Current.Stop();
        });
        _nowPlayingTrack = track;
        _lastReportedPosition = 0;
        return version;
    }

    private void CancelPlaybackRequest()
    {
        Interlocked.Increment(ref _playbackRequestVersion);
    }

    private bool IsCurrentPlaybackRequest(int version)
    {
        return version == Volatile.Read(ref _playbackRequestVersion);
    }

    private static Task RunOnMainThreadAsync(Func<Task> action)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        UIApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await action();
                completionSource.TrySetResult();
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });

        return completionSource.Task;
    }

    private static void ApplyDefaultArtwork(IMediaItem media)
    {
        var coverImageUri = CoverImageUri.Value;
        if (string.IsNullOrWhiteSpace(coverImageUri))
        {
            return;
        }

        media.ImageUri = coverImageUri;
        media.AlbumImageUri = coverImageUri;

        if (CoverImage.Value is { } coverImage)
        {
            media.Image = coverImage;
            media.AlbumImage = coverImage;
        }
    }

    private static string? CreateCoverImageUri()
    {
        var path = NSBundle.MainBundle.PathForResource(CoverResourceName, CoverResourceExtension);
        return string.IsNullOrWhiteSpace(path) ? null : new Uri(path).AbsoluteUri;
    }

    private static UIImage? CreateCoverImage()
    {
        var path = NSBundle.MainBundle.PathForResource(CoverResourceName, CoverResourceExtension);
        return string.IsNullOrWhiteSpace(path) ? null : UIImage.FromFile(path);
    }

    private void StartPositionTimer()
    {
        _positionTimer ??= new System.Threading.Timer(
            _ => UIApplication.SharedApplication.BeginInvokeOnMainThread(PollPosition),
            null,
            TimeSpan.Zero,
            PositionUpdateInterval);
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void PollPosition()
    {
        if (_nowPlayingTrack is null || IsPaused)
        {
            return;
        }

        ReportPosition(CrossMediaManager.Current.Position);
    }

    private void ReportPosition(TimeSpan position)
    {
        var seconds = Convert.ToInt64(position.TotalSeconds);
        if (seconds == _lastReportedPosition)
        {
            return;
        }

        _lastReportedPosition = seconds;
        TimeChanged?.Invoke(seconds);

        if (_nowPlayingTrack is not null)
        {
            UpdateSystemNowPlaying(_nowPlayingTrack, seconds, isPlaying: !IsPaused);
        }
    }

    private static void ScheduleSystemArtworkRefresh(TrackItemModel track)
    {
        _ = RefreshSystemArtworkAfterDelayAsync(track, TimeSpan.FromMilliseconds(300));
        _ = RefreshSystemArtworkAfterDelayAsync(track, TimeSpan.FromSeconds(1));
        _ = RefreshSystemArtworkAfterDelayAsync(track, TimeSpan.FromSeconds(2));
    }

    private static async Task RefreshSystemArtworkAfterDelayAsync(TrackItemModel track, TimeSpan delay)
    {
        await Task.Delay(delay);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            UpdateSystemNowPlaying(track, null, isPlaying: true));
    }

    private static void UpdateSystemNowPlaying(TrackItemModel? track, long? positionSeconds, bool isPlaying)
    {
        if (track is null)
        {
            return;
        }

        var info = new MPNowPlayingInfo();
        info.Title = track.Name;
        info.Artist = track.Artist;
        info.PlaybackDuration = track.Time.TotalSeconds;
        info.PlaybackRate = isPlaying ? 1d : 0d;

        if (positionSeconds is { } position)
        {
            info.ElapsedPlaybackTime = position;
        }

        if (CoverImage.Value is { } coverImage)
        {
            info.Artwork = new MPMediaItemArtwork(coverImage.Size, _ => coverImage);
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }
}
