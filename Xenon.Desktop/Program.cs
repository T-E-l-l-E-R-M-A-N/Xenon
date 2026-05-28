using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Timers;
using System.Threading.Tasks;
using System.Threading;
using Timer = System.Timers.Timer;
using Autofac;
using Avalonia;
using Xenon.Core.Services;
using Xenon.Core.ViewModels;
using ManagedBass;
using MediaManager;
using Microsoft.Extensions.DependencyInjection;
using Projektanker.Icons.Avalonia.MaterialDesign;
using Xenon.Core;
using Xenon.UI;

namespace Xenon.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        Projektanker.Icons.Avalonia.IconProvider.Current.Register<MaterialDesignIconProvider>();

        var services = new ServiceCollection();
        services.AddScoped<IPlayback, DesktopPlaybackService>();
        services.AddScoped<IKeyboardInsetService, KeyboardInsetService>();
        IoC.Build(services);
        //CrossMediaManager.Current.Init();
        
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

public sealed class DesktopPlaybackService : IPlayback
{
    private readonly IPlaylistManager _playlistManager;
    private Timer? _timer;
    private MediaPlayer? _player;
    private CancellationTokenSource? _playbackCancellation;
    private int _playbackRequestVersion;
    
    public event Action<long>? TimeChanged;
    public event Action<bool>? StateChanged;
    public event Action<TrackItemModel?>? PreparingChanged;
    public event Action<TrackItemModel>? SongChanged;
    public event Action? PlayFinished;
    public bool IsPaused { get; set; }
    public bool IsPreparing { get; private set; }
    public int PlayingTrackId { get; set; }

    public DesktopPlaybackService(IPlaylistManager playlistManager)
    {
        _playlistManager = playlistManager;
    }

    public async Task Play(TrackItemModel s)
    {
        if (_player is null)
        {
            Init();
        }

        var player = _player ?? throw new InvalidOperationException("Playback player is not initialized.");
        var timer = _timer ?? throw new InvalidOperationException("Playback timer is not initialized.");
        var (requestVersion, token) = BeginPlaybackRequest(s);
        var path = Path.Combine(Environment.CurrentDirectory, $"{s.Id}-{requestVersion}.mp3");
        var shouldDeleteFile = false;

        try
        {
            timer.Stop();
            if (player.State is PlaybackState.Playing or PlaybackState.Paused)
            {
                player.Stop();
            }

            if (s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.Url.StartsWith("/get", StringComparison.OrdinalIgnoreCase))
            {
                var downloadUrl = s.Url.StartsWith("/get", StringComparison.OrdinalIgnoreCase)
                    ? "https://rus.hitmotop.com" + s.Url
                    : s.Url;

                using var httpClient = new HttpClient();
                var bytes = await httpClient.GetByteArrayAsync(downloadUrl, token);
                await File.WriteAllBytesAsync(path, bytes, token);
                shouldDeleteFile = true;
            }
            else
            {
                path = s.Url;
            }

            token.ThrowIfCancellationRequested();
            if (!IsCurrentPlaybackRequest(requestVersion, token))
            {
                return;
            }

            await player.LoadAsync(path);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentPlaybackRequest(requestVersion, token))
            {
                return;
            }

            _playlistManager.SetCurrent(s);
            SongChanged?.Invoke(s);
            PlayingTrackId = s.Id;
            player.Play();
            IsPaused = false;
            SetPreparing(null);
            StateChanged?.Invoke(true);
            timer.Start();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentPlaybackRequest(requestVersion, token))
            {
                SetPreparing(null);
            }
        }
        catch (Exception e)
        {
            if (IsCurrentPlaybackRequest(requestVersion, token))
            {
                SetPreparing(null);
            }

            Console.WriteLine(e);
            throw;
        }
        finally
        {
            if (shouldDeleteFile && File.Exists(path))
            {
                File.Delete(path);
            }
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

    public void Pause()
    {
        if (_player is null || _timer is null)
        {
            return;
        }

        if (_player.State == PlaybackState.Playing)
        {
            _player.Pause();
            StateChanged?.Invoke(false);
            IsPaused = true;
            _timer.Stop();
        }
        else if (_player.State == PlaybackState.Paused)
        {
            _player.Play();
            StateChanged?.Invoke(true);
            IsPaused = false;
            _timer.Start();
        }
    }

    public void SeekTo(long newTime)
    {
        if (_player is null || _timer is null)
        {
            return;
        }

        if (IsPaused)
        {
            _player.Position = TimeSpan.FromSeconds(newTime);
            _player.Play();
            StateChanged?.Invoke(true);
            IsPaused = false;
            _timer.Start();
        }
        else
        {
            Pause();
            _player.Position = TimeSpan.FromSeconds(newTime);
            Pause();
        }
    }

    public void Init()
    {
        _player = new MediaPlayer();
        _player.MediaEnded += PlayerOnMediaEnded;
        _timer = new Timer(1000);
        _timer.Elapsed += TimerOnElapsed;
    }

    public void Stop()
    {
        CancelPlaybackRequest();
        if (_player is null)
        {
            SetPreparing(null);
            PlayingTrackId = 0;
            return;
        }

        _player.Stop();
        SetPreparing(null);
        PlayingTrackId = 0;
        StateChanged?.Invoke(false);
        IsPaused = true;
        _timer?.Stop();
    }

    private async void PlayerOnMediaEnded(object? sender, EventArgs e)
    {
        if (await PlayNext())
        {
            return;
        }

        PlayFinished?.Invoke();
        PlayingTrackId = 0;
        StateChanged?.Invoke(false);
        IsPaused = true;
        _timer?.Stop();
    }

    private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        TimeChanged?.Invoke(Convert.ToInt64(_player.Position.TotalSeconds));
    }

    private void SetPreparing(TrackItemModel? track)
    {
        IsPreparing = track is not null;
        PreparingChanged?.Invoke(track);
    }

    private (int Version, CancellationToken Token) BeginPlaybackRequest(TrackItemModel track)
    {
        CancelPlaybackRequest();
        _playbackCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _playbackRequestVersion);
        SetPreparing(track);
        StateChanged?.Invoke(false);
        return (version, _playbackCancellation.Token);
    }

    private void CancelPlaybackRequest()
    {
        _playbackCancellation?.Cancel();
        _playbackCancellation?.Dispose();
        _playbackCancellation = null;
    }

    private bool IsCurrentPlaybackRequest(int version, CancellationToken token)
    {
        return !token.IsCancellationRequested && version == Volatile.Read(ref _playbackRequestVersion);
    }
}
