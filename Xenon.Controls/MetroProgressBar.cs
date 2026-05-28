using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Xenon.Controls;

public class MetroProgressBar : ProgressBar
{
    private const double AnimationDurationSeconds = 3.917d;
    private const double BorderDelayStepSeconds = 0.167d;
    private const double EllipseDelayStepSeconds = 0.167d;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000d / 60d);
    private static readonly SplineEasing MetroSpline = CreateSpline(0.4d, 0d, 0.6d, 1d);

    public static readonly StyledProperty<double> EllipseDiameterProperty =
        AvaloniaProperty.Register<MetroProgressBar, double>(nameof(EllipseDiameter));

    public static readonly StyledProperty<double> EllipseOffsetProperty =
        AvaloniaProperty.Register<MetroProgressBar, double>(nameof(EllipseOffset));

    public static readonly DirectProperty<MetroProgressBar, string> FormattedProgressTextProperty =
        AvaloniaProperty.RegisterDirect<MetroProgressBar, string>(
            nameof(FormattedProgressText),
            control => control.FormattedProgressText);

    private readonly DispatcherTimer _indeterminateTimer;
    private readonly Stopwatch _indeterminateClock = new();
    private readonly Border?[] _ellipseBorders = new Border[5];
    private readonly Ellipse?[] _ellipses = new Ellipse[5];
    private readonly TranslateTransform?[] _ellipseBorderTransforms = new TranslateTransform?[5];
    private readonly TranslateTransform?[] _ellipseTransforms = new TranslateTransform?[5];
    private LayoutTransformControl? _layoutTransformHost;
    private Grid? _ellipseClip;
    private Grid? _ellipseGrid;
    private Grid? _determinateRoot;
    private Border? _indicator;
    private TranslateTransform? _ellipseGridTransform;
    private string _formattedProgressText = string.Empty;
    private bool _isIndeterminateAnimationRunning;

    static MetroProgressBar()
    {
        ClipToBoundsProperty.OverrideDefaultValue<MetroProgressBar>(true);
    }

    public MetroProgressBar()
    {
        _indeterminateTimer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, OnIndeterminateTick);

        AttachedToVisualTree += (_, _) => UpdateVisualState(forceRestart: true);
        DetachedFromVisualTree += (_, _) => StopIndeterminateAnimation(resetVisuals: true);
    }

    public double EllipseDiameter
    {
        get => GetValue(EllipseDiameterProperty);
        set => SetValue(EllipseDiameterProperty, value);
    }

    public double EllipseOffset
    {
        get => GetValue(EllipseOffsetProperty);
        set => SetValue(EllipseOffsetProperty, value);
    }

    public string FormattedProgressText
    {
        get => _formattedProgressText;
        private set => SetAndRaise(FormattedProgressTextProperty, ref _formattedProgressText, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _layoutTransformHost = e.NameScope.Find<LayoutTransformControl>("PART_LayoutTransformHost");
        _ellipseClip = e.NameScope.Find<Grid>("EllipseClip");
        _ellipseGrid = e.NameScope.Find<Grid>("EllipseGrid");
        _determinateRoot = e.NameScope.Find<Grid>("DeterminateRoot");
        _indicator = e.NameScope.Find<Border>("PART_Indicator");
        _ellipseGridTransform = EnsureTranslateTransform(_ellipseGrid);

        for (var index = 0; index < 5; index++)
        {
            var ellipseIndex = index + 1;
            _ellipseBorders[index] = e.NameScope.Find<Border>($"B{ellipseIndex}");
            _ellipses[index] = e.NameScope.Find<Ellipse>($"E{ellipseIndex}");
            _ellipseBorderTransforms[index] = EnsureTranslateTransform(_ellipseBorders[index]);
            _ellipseTransforms[index] = EnsureTranslateTransform(_ellipses[index]);
        }

        UpdateLayoutOrientation();
        UpdateEllipseMetrics();
        UpdateDeterminateIndicator();
        UpdateProgressText();
        UpdateVisualState(forceRestart: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty || change.Property == OrientationProperty)
        {
            UpdateLayoutOrientation();
            UpdateEllipseMetrics();
            UpdateDeterminateIndicator();
            UpdateVisualState(forceRestart: change.Property == OrientationProperty);
            return;
        }

        if (change.Property == ValueProperty ||
            change.Property == MinimumProperty ||
            change.Property == MaximumProperty)
        {
            UpdateDeterminateIndicator();
            UpdateProgressText();
            return;
        }

        if (change.Property == ShowProgressTextProperty || change.Property == ProgressTextFormatProperty)
        {
            UpdateProgressText();
            return;
        }

        if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty)
        {
            UpdateDeterminateIndicator();
            UpdateVisualState(forceRestart: true);
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

    private static TranslateTransform? EnsureTranslateTransform(Visual? visual)
    {
        if (visual is null)
        {
            return null;
        }

        if (visual.RenderTransform is TranslateTransform translateTransform)
        {
            return translateTransform;
        }

        translateTransform = new TranslateTransform();
        visual.RenderTransform = translateTransform;
        return translateTransform;
    }

    private void UpdateLayoutOrientation()
    {
        if (_layoutTransformHost is null)
        {
            return;
        }

        _layoutTransformHost.LayoutTransform = Orientation == Orientation.Vertical
            ? new RotateTransform(-90d)
            : null;
    }

    private void UpdateEllipseMetrics()
    {
        var size = GetAnimationTrackLength();
        if (size <= 0d)
        {
            return;
        }

        SetCurrentValue(EllipseDiameterProperty, size <= 180d ? 4d : (size <= 280d ? 5d : 6d));
        SetCurrentValue(EllipseOffsetProperty, size <= 180d ? 4d : (size <= 280d ? 7d : 9d));
    }

    private void UpdateProgressText()
    {
        if (!ShowProgressText)
        {
            FormattedProgressText = string.Empty;
            return;
        }

        var range = Maximum - Minimum;
        var progress = range <= 0d ? 0d : Math.Clamp((Value - Minimum) / range, 0d, 1d);
        var percent = progress * 100d;

        if (!string.IsNullOrWhiteSpace(ProgressTextFormat))
        {
            try
            {
                FormattedProgressText = string.Format(
                    CultureInfo.CurrentCulture,
                    ProgressTextFormat,
                    Value,
                    Minimum,
                    Maximum,
                    percent);
                return;
            }
            catch (FormatException)
            {
            }
        }

        FormattedProgressText = $"{percent:0}%";
    }

    private void UpdateDeterminateIndicator()
    {
        if (_indicator is null || _determinateRoot is null || IsIndeterminate)
        {
            return;
        }

        var rootBounds = _determinateRoot.Bounds;
        if (rootBounds.Width <= 0d || rootBounds.Height <= 0d)
        {
            Dispatcher.UIThread.Post(UpdateDeterminateIndicator, DispatcherPriority.Loaded);
            return;
        }

        var range = Maximum - Minimum;
        var ratio = range <= 0d ? 0d : Math.Clamp((Value - Minimum) / range, 0d, 1d);

        if (Orientation == Orientation.Horizontal)
        {
            _indicator.HorizontalAlignment = HorizontalAlignment.Left;
            _indicator.VerticalAlignment = VerticalAlignment.Stretch;
            _indicator.Width = rootBounds.Width * ratio;
            _indicator.Height = double.NaN;
        }
        else
        {
            _indicator.HorizontalAlignment = HorizontalAlignment.Stretch;
            _indicator.VerticalAlignment = VerticalAlignment.Bottom;
            _indicator.Width = double.NaN;
            _indicator.Height = rootBounds.Height * ratio;
        }
    }

    private void UpdateVisualState(bool forceRestart = false)
    {
        if (_ellipseGrid is null || _determinateRoot is null)
        {
            return;
        }

        var showIndeterminate = IsIndeterminate;
        if (_ellipseClip is not null)
        {
            _ellipseClip.IsVisible = showIndeterminate;
            _ellipseClip.Opacity = showIndeterminate ? 1d : 0d;
        }

        _ellipseGrid.IsVisible = showIndeterminate;
        _ellipseGrid.Opacity = showIndeterminate ? 1d : 0d;
        _determinateRoot.IsVisible = !showIndeterminate;
        _determinateRoot.Opacity = showIndeterminate ? 0d : 1d;

        if (showIndeterminate && IsVisible && GetAnimationTrackLength() > 0d)
        {
            StartIndeterminateAnimation(forceRestart);
        }
        else
        {
            StopIndeterminateAnimation(resetVisuals: !showIndeterminate);
        }
    }

    private void StartIndeterminateAnimation(bool forceRestart)
    {
        if (_isIndeterminateAnimationRunning && !forceRestart)
        {
            return;
        }

        _indeterminateClock.Restart();
        ApplyIndeterminateFrame(0d);

        if (!_indeterminateTimer.IsEnabled)
        {
            _indeterminateTimer.Start();
        }

        _isIndeterminateAnimationRunning = true;
    }

    private void StopIndeterminateAnimation(bool resetVisuals)
    {
        _indeterminateTimer.Stop();
        _indeterminateClock.Reset();
        _isIndeterminateAnimationRunning = false;

        if (resetVisuals)
        {
            ResetIndeterminateVisuals();
        }
    }

    private void OnIndeterminateTick(object? sender, EventArgs e)
    {
        if (!IsIndeterminate || !IsVisible)
        {
            StopIndeterminateAnimation(resetVisuals: true);
            return;
        }

        ApplyIndeterminateFrame(MahAppsAnimationHelpers.Normalize(
            _indeterminateClock.Elapsed.TotalSeconds,
            AnimationDurationSeconds));
    }

    private void ApplyIndeterminateFrame(double time)
    {
        var size = GetAnimationTrackLength();
        if (size <= 0d)
        {
            return;
        }

        var containerStart = size <= 180d ? -34d : (size <= 280d ? -50.5d : -63d);
        var containerEndBase = 0.4352d * size;
        var containerEnd = size <= 180d
            ? containerEndBase - 25.731d
            : (size <= 280d ? containerEndBase + 27.84d : containerEndBase + 58.862d);
        var ellipseWell = size / 3d;
        var ellipseEnd = size * 2d / 3d;

        if (_ellipseGridTransform is not null)
        {
            _ellipseGridTransform.X = MahAppsAnimationHelpers.Lerp(
                containerStart,
                containerEnd,
                time / AnimationDurationSeconds);
        }

        for (var index = 0; index < 5; index++)
        {
            var borderDelay = 0.5d + (index * BorderDelayStepSeconds);
            var borderMid = 2d + (index * BorderDelayStepSeconds);
            var borderEnd = 3d + (index * BorderDelayStepSeconds);
            var ellipseDelay = index * EllipseDelayStepSeconds;

            if (_ellipseBorderTransforms[index] is not null)
            {
                _ellipseBorderTransforms[index]!.X = EvaluateMetroBorderPosition(time, borderDelay, borderMid, borderEnd);
            }

            if (_ellipseTransforms[index] is not null)
            {
                _ellipseTransforms[index]!.X = EvaluateMetroEllipsePosition(time, ellipseDelay, ellipseWell, ellipseEnd);
            }

            if (_ellipses[index] is not null)
            {
                _ellipses[index]!.Opacity = EvaluateMetroEllipseOpacity(time, ellipseDelay);
            }
        }
    }

    private void ResetIndeterminateVisuals()
    {
        if (_ellipseGridTransform is not null)
        {
            _ellipseGridTransform.X = 0d;
        }

        for (var index = 0; index < 5; index++)
        {
            if (_ellipseBorderTransforms[index] is not null)
            {
                _ellipseBorderTransforms[index]!.X = 0d;
            }

            if (_ellipseTransforms[index] is not null)
            {
                _ellipseTransforms[index]!.X = 0d;
            }

            if (_ellipses[index] is not null)
            {
                _ellipses[index]!.Opacity = 0d;
            }
        }
    }

    private double GetAnimationTrackLength()
    {
        var length = Orientation == Orientation.Horizontal ? Bounds.Width : Bounds.Height;
        if (length <= 0d)
        {
            length = Orientation == Orientation.Horizontal ? Width : Height;
        }

        return double.IsFinite(length) ? Math.Max(0d, length) : 0d;
    }

    private static double EvaluateMetroBorderPosition(double time, double startZeroTime, double holdEndTime, double endTime)
    {
        if (time <= 0d)
        {
            return -50d;
        }

        if (time <= startZeroTime)
        {
            return MahAppsAnimationHelpers.Lerp(-50d, 0d, time / startZeroTime);
        }

        if (time <= holdEndTime)
        {
            return 0d;
        }

        if (time <= endTime)
        {
            return MahAppsAnimationHelpers.Lerp(0d, 100d, (time - holdEndTime) / (endTime - holdEndTime));
        }

        return 100d;
    }

    private static double EvaluateMetroEllipsePosition(double time, double delay, double well, double end)
    {
        if (time <= delay)
        {
            return 0d;
        }

        var firstMotionEnd = delay + 1d;
        if (time <= firstMotionEnd)
        {
            return MahAppsAnimationHelpers.Lerp(0d, well, MetroSpline.Ease((time - delay) / 1d));
        }

        var holdEnd = delay + 2d;
        if (time <= holdEnd)
        {
            return well;
        }

        var secondMotionEnd = delay + 3d;
        if (time <= secondMotionEnd)
        {
            return MahAppsAnimationHelpers.Lerp(well, end, MetroSpline.Ease((time - holdEnd) / 1d));
        }

        return end;
    }

    private static double EvaluateMetroEllipseOpacity(double time, double delay)
    {
        return time < delay || time >= delay + 3d ? 0d : 1d;
    }
}
