using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace Xenon.Controls;

public class ProgressRing : TemplatedControl
{
    private const double RingAnimationDurationSeconds = 3.47d;
    private const double RingStoryboardCycleSeconds = 4.305d;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000d / 60d);
    private static readonly double[] BeginOffsets = [0d, 0.167d, 0.334d, 0.501d, 0.668d, 0.835d];
    private static readonly SplineEasing Spline1 = CreateSpline(0.13d, 0.21d, 0.1d, 0.7d);
    private static readonly SplineEasing Spline2 = CreateSpline(0.02d, 0.33d, 0.38d, 0.77d);
    private static readonly SplineEasing Spline3 = CreateSpline(0.57d, 0.17d, 0.95d, 0.75d);
    private static readonly SplineEasing Spline4 = CreateSpline(0d, 0.19d, 0.07d, 0.72d);
    private static readonly SplineEasing Spline5 = CreateSpline(0d, 0d, 0.95d, 0.37d);
    private static readonly MahAppsNumericKeyFrame[][] RotationKeyFrames =
    [
        [new(0d, -110d, Spline1), new(0.433d, 10d, Spline2), new(1.2d, 93d), new(1.617d, 205d, Spline3), new(2.017d, 357d, Spline4), new(2.783d, 439d), new(3.217d, 585d, Spline5)],
        [new(0d, -116d, Spline1), new(0.433d, 4d, Spline2), new(1.2d, 87d), new(1.617d, 199d, Spline3), new(2.017d, 351d, Spline4), new(2.783d, 433d), new(3.217d, 579d, Spline5)],
        [new(0d, -122d, Spline1), new(0.433d, -2d, Spline2), new(1.2d, 81d), new(1.617d, 193d, Spline3), new(2.017d, 345d, Spline4), new(2.783d, 427d), new(3.217d, 573d, Spline5)],
        [new(0d, -128d, Spline1), new(0.433d, -8d, Spline2), new(1.2d, 75d), new(1.617d, 187d, Spline3), new(2.017d, 339d, Spline4), new(2.783d, 421d), new(3.217d, 567d, Spline5)],
        [new(0d, -134d, Spline1), new(0.433d, -14d, Spline2), new(1.2d, 69d), new(1.617d, 181d, Spline3), new(2.017d, 331d, Spline4), new(2.783d, 415d), new(3.217d, 561d, Spline5)],
        [new(0d, -140d, Spline1), new(0.433d, -20d, Spline2), new(1.2d, 63d), new(1.617d, 175d, Spline3), new(2.017d, 325d, Spline4), new(2.783d, 409d), new(3.217d, 555d, Spline5)]
    ];

    public static readonly DirectProperty<ProgressRing, double> BindableWidthProperty =
        AvaloniaProperty.RegisterDirect<ProgressRing, double>(
            nameof(BindableWidth),
            ring => ring.BindableWidth);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> IsLargeProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsLarge), true);

    public static readonly DirectProperty<ProgressRing, double> MaxSideLengthProperty =
        AvaloniaProperty.RegisterDirect<ProgressRing, double>(
            nameof(MaxSideLength),
            ring => ring.MaxSideLength);

    public static readonly DirectProperty<ProgressRing, double> EllipseDiameterProperty =
        AvaloniaProperty.RegisterDirect<ProgressRing, double>(
            nameof(EllipseDiameter),
            ring => ring.EllipseDiameter);

    public static readonly DirectProperty<ProgressRing, Thickness> EllipseOffsetProperty =
        AvaloniaProperty.RegisterDirect<ProgressRing, Thickness>(
            nameof(EllipseOffset),
            ring => ring.EllipseOffset);

    public static readonly StyledProperty<double> EllipseDiameterScaleProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(EllipseDiameterScale), 1d);

    private readonly DispatcherTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly Avalonia.Controls.Shapes.Ellipse?[] _ellipses = new Avalonia.Controls.Shapes.Ellipse?[6];
    private readonly RotateTransform?[] _rotateTransforms = new RotateTransform?[6];
    private Grid? _ring;
    private Canvas? _sixthCircle;
    private double _bindableWidth;
    private double _maxSideLength;
    private double _ellipseDiameter;
    private Thickness _ellipseOffset;
    private bool _isAnimationRunning;

    public ProgressRing()
    {
        _animationTimer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, OnAnimationTick);

        AttachedToVisualTree += (_, _) => UpdateAnimationState(resetClock: true);
        DetachedFromVisualTree += (_, _) => StopAnimation(resetVisuals: true);
    }

    public double BindableWidth
    {
        get => _bindableWidth;
        private set => SetAndRaise(BindableWidthProperty, ref _bindableWidth, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsLarge
    {
        get => GetValue(IsLargeProperty);
        set => SetValue(IsLargeProperty, value);
    }

    public double MaxSideLength
    {
        get => _maxSideLength;
        private set => SetAndRaise(MaxSideLengthProperty, ref _maxSideLength, value);
    }

    public double EllipseDiameter
    {
        get => _ellipseDiameter;
        private set => SetAndRaise(EllipseDiameterProperty, ref _ellipseDiameter, value);
    }

    public Thickness EllipseOffset
    {
        get => _ellipseOffset;
        private set => SetAndRaise(EllipseOffsetProperty, ref _ellipseOffset, value);
    }

    public double EllipseDiameterScale
    {
        get => GetValue(EllipseDiameterScaleProperty);
        set => SetValue(EllipseDiameterScaleProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _ring = e.NameScope.Find<Grid>("Ring");
        _sixthCircle = e.NameScope.Find<Canvas>("SixthCircle");

        for (var index = 0; index < 6; index++)
        {
            var ellipseIndex = index + 1;
            _ellipses[index] = e.NameScope.Find<Avalonia.Controls.Shapes.Ellipse>($"E{ellipseIndex}");
            _rotateTransforms[index] = EnsureRotateTransform(e.NameScope.Find<Canvas>($"C{ellipseIndex}"));
        }

        UpdateMetrics();
        ApplyLargeState();
        UpdateAnimationState(resetClock: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty || change.Property == EllipseDiameterScaleProperty)
        {
            UpdateMetrics();
            return;
        }

        if (change.Property == IsLargeProperty)
        {
            ApplyLargeState();
            return;
        }

        if (change.Property == IsActiveProperty)
        {
            UpdateAnimationState(resetClock: true);
            return;
        }

        if (change.Property == IsVisibleProperty)
        {
            var isVisible = change.GetNewValue<bool>();
            if (IsActive != isVisible)
            {
                SetCurrentValue(IsActiveProperty, isVisible);
            }

            UpdateAnimationState(resetClock: true);
        }
    }

    private static SplineEasing CreateSpline(double x1, double y1, double x2, double y2)
    {
        return new SplineEasing
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2
        };
    }

    private static RotateTransform? EnsureRotateTransform(Visual? visual)
    {
        if (visual is null)
        {
            return null;
        }

        if (visual.RenderTransform is RotateTransform rotateTransform)
        {
            return rotateTransform;
        }

        rotateTransform = new RotateTransform();
        visual.RenderTransform = rotateTransform;
        return rotateTransform;
    }

    private void UpdateMetrics()
    {
        var width = Bounds.Width;
        if ((width <= 0d || !double.IsFinite(width)) && double.IsFinite(Width))
        {
            width = Width;
        }

        var height = Bounds.Height;
        if ((height <= 0d || !double.IsFinite(height)) && double.IsFinite(Height))
        {
            height = Height;
        }

        width = double.IsFinite(width) ? Math.Max(0d, width) : 0d;
        height = double.IsFinite(height) ? Math.Max(0d, height) : 0d;

        var sideLength = Math.Max(width, height);

        BindableWidth = sideLength;
        MaxSideLength = sideLength;
        EllipseDiameter = sideLength <= 0d ? 0d : (sideLength / 8d) * EllipseDiameterScale;
        EllipseOffset = new Thickness(0d, sideLength / 2d, 0d, 0d);
    }

    private void ApplyLargeState()
    {
        if (_sixthCircle is not null)
        {
            _sixthCircle.IsVisible = IsLarge;
        }
    }

    private void UpdateAnimationState(bool resetClock)
    {
        if (_ring is null)
        {
            return;
        }

        if (IsActive && IsVisible)
        {
            _ring.IsVisible = true;
            StartAnimation(resetClock);
        }
        else
        {
            StopAnimation(resetVisuals: true);
        }
    }

    private void StartAnimation(bool resetClock)
    {
        if (_isAnimationRunning && !resetClock)
        {
            return;
        }

        if (resetClock)
        {
            _animationClock.Restart();
            ApplyFrame(0d);
        }

        if (!_animationTimer.IsEnabled)
        {
            _animationTimer.Start();
        }

        _isAnimationRunning = true;
    }

    private void StopAnimation(bool resetVisuals)
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        _isAnimationRunning = false;

        if (_ring is not null)
        {
            _ring.IsVisible = false;
        }

        if (resetVisuals)
        {
            ResetVisuals();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!IsActive || !IsVisible)
        {
            StopAnimation(resetVisuals: true);
            return;
        }

        ApplyFrame(MahAppsAnimationHelpers.Normalize(
            _animationClock.Elapsed.TotalSeconds,
            RingStoryboardCycleSeconds));
    }

    private void ApplyFrame(double globalTime)
    {
        for (var index = 0; index < 6; index++)
        {
            var beginOffset = BeginOffsets[index];
            if (globalTime < beginOffset)
            {
                SetEllipseState(index, 0d, 0d);
                continue;
            }

            var localTime = globalTime - beginOffset;
            if (localTime > RingAnimationDurationSeconds)
            {
                SetEllipseState(index, RotationKeyFrames[index][^1].Value, 0d);
                continue;
            }

            var angle = MahAppsAnimationHelpers.Evaluate(RotationKeyFrames[index], localTime);
            var opacity = localTime < 3.22d ? 1d : 0d;
            if (localTime < 0d)
            {
                opacity = 0d;
            }

            SetEllipseState(index, angle, opacity);
        }

        if (!IsLarge)
        {
            SetEllipseState(5, 0d, 0d);
        }
    }

    private void SetEllipseState(int index, double angle, double opacity)
    {
        if (_rotateTransforms[index] is not null)
        {
            _rotateTransforms[index]!.Angle = angle;
        }

        if (_ellipses[index] is not null)
        {
            _ellipses[index]!.Opacity = opacity;
        }
    }

    private void ResetVisuals()
    {
        for (var index = 0; index < 6; index++)
        {
            SetEllipseState(index, 0d, 0d);
        }
    }
}
