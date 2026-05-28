using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Xenon.Controls;

public class AsyncCachedImage : Avalonia.Controls.Image
{
    private const int DefaultMaxCacheSide = 4096;
    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> OriginalLoads = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> ResizedLoads = new();

    public static readonly StyledProperty<Uri?> UriSourceProperty =
        AvaloniaProperty.Register<AsyncCachedImage, Uri?>(nameof(UriSource));

    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<AsyncCachedImage, string?>(nameof(SourceUrl));

    public static readonly StyledProperty<Uri?> BaseUriProperty =
        AvaloniaProperty.Register<AsyncCachedImage, Uri?>(nameof(BaseUri));

    public static readonly StyledProperty<string?> CacheDirectoryProperty =
        AvaloniaProperty.Register<AsyncCachedImage, string?>(nameof(CacheDirectory));

    public static readonly StyledProperty<int> MaxCacheSideProperty =
        AvaloniaProperty.Register<AsyncCachedImage, int>(nameof(MaxCacheSide), DefaultMaxCacheSide);

    public static readonly DirectProperty<AsyncCachedImage, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<AsyncCachedImage, bool>(
            nameof(IsLoading),
            image => image.IsLoading);

    public static readonly DirectProperty<AsyncCachedImage, Exception?> ErrorProperty =
        AvaloniaProperty.RegisterDirect<AsyncCachedImage, Exception?>(
            nameof(Error),
            image => image.Error);

    private CancellationTokenSource? _loadCancellation;
    private Bitmap? _ownedBitmap;
    private string? _currentCacheKey;
    private bool _isAttached;
    private bool _isLoading;
    private Exception? _error;

    static AsyncCachedImage()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Xenon/1.0");
        HttpClient.DefaultRequestHeaders.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");
    }

    public Uri? UriSource
    {
        get => GetValue(UriSourceProperty);
        set => SetValue(UriSourceProperty, value);
    }

    public string? SourceUrl
    {
        get => GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public Uri? BaseUri
    {
        get => GetValue(BaseUriProperty);
        set => SetValue(BaseUriProperty, value);
    }

    public string? CacheDirectory
    {
        get => GetValue(CacheDirectoryProperty);
        set => SetValue(CacheDirectoryProperty, value);
    }

    public int MaxCacheSide
    {
        get => GetValue(MaxCacheSideProperty);
        set => SetValue(MaxCacheSideProperty, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    public Exception? Error
    {
        get => _error;
        private set => SetAndRaise(ErrorProperty, ref _error, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        QueueLoad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        CancelPendingLoad();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UriSourceProperty ||
            change.Property == SourceUrlProperty ||
            change.Property == BaseUriProperty ||
            change.Property == CacheDirectoryProperty ||
            change.Property == MaxCacheSideProperty ||
            change.Property == BoundsProperty)
        {
            QueueLoad();
        }
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        QueueLoad();
    }

    private void QueueLoad()
    {
        if (!_isAttached)
        {
            return;
        }

        var uri = GetUriSource();
        if (uri is null)
        {
            ClearImage();
            return;
        }

        var pixelSize = GetTargetPixelSize();
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return;
        }

        var cacheRoot = GetCacheRoot();
        var cacheKey = $"{uri.AbsoluteUri}|{pixelSize.Width}x{pixelSize.Height}|{Stretch}";
        if (cacheKey == _currentCacheKey)
        {
            return;
        }

        _currentCacheKey = cacheKey;
        CancelPendingLoad();
        _loadCancellation = new CancellationTokenSource();
        _ = LoadAsync(uri, cacheRoot, pixelSize, Stretch, _loadCancellation.Token);
    }

    private async Task LoadAsync(Uri uri, string cacheRoot, PixelSize pixelSize, Stretch stretch, CancellationToken cancellationToken)
    {
        try
        {
            IsLoading = true;
            Error = null;

            var resizedPath = await GetResizedImagePathAsync(uri, cacheRoot, pixelSize, stretch);
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(resizedPath);
            var bitmap = new Bitmap(stream);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                var oldBitmap = _ownedBitmap;
                _ownedBitmap = bitmap;
                SetCurrentValue(SourceProperty, bitmap);
                oldBitmap?.Dispose();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Error = exception;
            await Dispatcher.UIThread.InvokeAsync(ClearImage);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private static async Task<string> GetResizedImagePathAsync(
        Uri uri,
        string cacheRoot,
        PixelSize pixelSize,
        Stretch stretch)
    {
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(Path.Combine(cacheRoot, "originals"));
        Directory.CreateDirectory(Path.Combine(cacheRoot, "resized"));

        var resizedPath = Path.Combine(
            cacheRoot,
            "resized",
            $"{HashText($"{uri.AbsoluteUri}|{pixelSize.Width}x{pixelSize.Height}|{stretch}")}.png");

        if (File.Exists(resizedPath))
        {
            return resizedPath;
        }

        var load = ResizedLoads.GetOrAdd(
            resizedPath,
            _ => new Lazy<Task<string>>(
                () => CreateResizedImageAsync(uri, cacheRoot, resizedPath, pixelSize, stretch),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await load.Value;
        }
        finally
        {
            ResizedLoads.TryRemove(resizedPath, out _);
        }
    }

    private static async Task<string> CreateResizedImageAsync(
        Uri uri,
        string cacheRoot,
        string resizedPath,
        PixelSize pixelSize,
        Stretch stretch)
    {
        if (File.Exists(resizedPath))
        {
            return resizedPath;
        }

        var originalPath = await GetOriginalImagePathAsync(uri, cacheRoot);
        await using var input = File.OpenRead(originalPath);
        using var image = await SixLabors.ImageSharp.Image.LoadAsync(input);

        var targetSize = GetResizeSize(image.Width, image.Height, pixelSize, stretch);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = targetSize,
            Mode = GetResizeMode(stretch),
            Sampler = KnownResamplers.Lanczos3
        }));

        var tempPath = $"{resizedPath}.{Guid.NewGuid():N}.tmp";
        await image.SaveAsPngAsync(tempPath, new PngEncoder());

        if (!File.Exists(resizedPath))
        {
            File.Move(tempPath, resizedPath);
        }
        else
        {
            File.Delete(tempPath);
        }

        return resizedPath;
    }

    private static async Task<string> GetOriginalImagePathAsync(Uri uri, string cacheRoot)
    {
        var originalPath = Path.Combine(cacheRoot, "originals", $"{HashText(uri.AbsoluteUri)}.img");
        if (File.Exists(originalPath))
        {
            return originalPath;
        }

        var load = OriginalLoads.GetOrAdd(
            originalPath,
            _ => new Lazy<Task<string>>(
                () => DownloadOriginalAsync(uri, originalPath),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await load.Value;
        }
        finally
        {
            OriginalLoads.TryRemove(originalPath, out _);
        }
    }

    private static async Task<string> DownloadOriginalAsync(Uri uri, string originalPath)
    {
        if (File.Exists(originalPath))
        {
            return originalPath;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tempPath = $"{originalPath}.{Guid.NewGuid():N}.tmp";
        await using (var output = File.Create(tempPath))
        {
            await response.Content.CopyToAsync(output);
        }

        if (!File.Exists(originalPath))
        {
            File.Move(tempPath, originalPath);
        }
        else
        {
            File.Delete(tempPath);
        }

        return originalPath;
    }

    private static SixLabors.ImageSharp.Size GetResizeSize(int sourceWidth, int sourceHeight, PixelSize pixelSize, Stretch stretch)
    {
        var width = pixelSize.Width;
        var height = pixelSize.Height;

        if (stretch == Stretch.None)
        {
            width = Math.Min(width, sourceWidth);
            height = Math.Min(height, sourceHeight);
        }

        return new SixLabors.ImageSharp.Size(width, height);
    }

    private static ResizeMode GetResizeMode(Stretch stretch)
    {
        return stretch switch
        {
            Stretch.Fill => ResizeMode.Stretch,
            Stretch.UniformToFill => ResizeMode.Crop,
            _ => ResizeMode.Max
        };
    }

    private Uri? GetUriSource()
    {
        if (UriSource is { } uri)
        {
            return uri;
        }

        var sourceUrl = SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        sourceUrl = sourceUrl.Trim();
        if (sourceUrl.StartsWith("//", StringComparison.Ordinal))
        {
            sourceUrl = $"https:{sourceUrl}";
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out uri))
        {
            return uri;
        }

        return BaseUri is { } baseUri && Uri.TryCreate(baseUri, sourceUrl, out uri) ? uri : null;
    }

    private PixelSize GetTargetPixelSize()
    {
        var bounds = Bounds.Size;
        var width = GetFinitePositive(bounds.Width, Width);
        var height = GetFinitePositive(bounds.Height, Height);

        if (width <= 0d || height <= 0d)
        {
            return default;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var maxSide = Math.Max(1, MaxCacheSide);
        var pixelWidth = Math.Clamp((int)Math.Ceiling(width * scaling), 1, maxSide);
        var pixelHeight = Math.Clamp((int)Math.Ceiling(height * scaling), 1, maxSide);

        return new PixelSize(pixelWidth, pixelHeight);
    }

    private string GetCacheRoot()
    {
        var cacheDirectory = CacheDirectory;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return cacheDirectory;
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "Xenon", "ImageCache");
    }

    private static double GetFinitePositive(double preferred, double fallback)
    {
        if (double.IsFinite(preferred) && preferred > 0d)
        {
            return preferred;
        }

        return double.IsFinite(fallback) && fallback > 0d ? fallback : 0d;
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void CancelPendingLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        IsLoading = false;
    }

    private void ClearImage()
    {
        _currentCacheKey = null;
        var oldBitmap = _ownedBitmap;
        _ownedBitmap = null;
        SetCurrentValue(SourceProperty, null);
        oldBitmap?.Dispose();
    }
}
