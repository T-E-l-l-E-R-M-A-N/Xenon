using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Xenon.Controls;

public class Panorama : ItemsControl
{
    private const double DragThreshold = 60;
    private const double ParallaxFactor = 0.35;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(800);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<Panorama, int>(nameof(SelectedIndex), 0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ItemWidthFactorProperty =
        AvaloniaProperty.Register<Panorama, double>(nameof(ItemWidthFactor), 1d);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<Panorama, double>(nameof(ItemSpacing), 18d);

    public static readonly StyledProperty<object?> TitleProperty =
        AvaloniaProperty.Register<Panorama, object?>(nameof(Title));

    public static readonly StyledProperty<IDataTemplate?> TitleTemplateProperty =
        AvaloniaProperty.Register<Panorama, IDataTemplate?>(nameof(TitleTemplate));

    public static readonly StyledProperty<IDataTemplate?> ItemTitleTemplateProperty =
        AvaloniaProperty.Register<Panorama, IDataTemplate?>(nameof(ItemTitleTemplate));

    public static readonly StyledProperty<string?> ItemTitleProperty =
        AvaloniaProperty.Register<Panorama, string?>(nameof(ItemTitle));

    private Border? _viewport;
    private Panel? _itemsHost;
    private ContentPresenter? _titlePresenter;
    private ContentPresenter? _titleClonePresenter;
    private Rectangle? _backgroundPresenter;
    private Rectangle? _backgroundClonePresenter;
    private TranslateTransform? _itemsTranslate;
    private TranslateTransform? _titleTranslate;
    private TranslateTransform? _titleCloneTranslate;
    private TranslateTransform? _backgroundTranslate;
    private TranslateTransform? _backgroundCloneTranslate;
    private int _itemCount;
    private int _appliedSelectedIndex;
    private bool _hasAppliedSelection;
    private bool _isPointerDown;
    private bool _isAnimating;
    private bool _isLoopPreparedForPrevious;
    private bool _updatingSelection;
    private int _pendingTailNavigationStep;
    private CancellationTokenSource? _featherAnimationCancellation;
    private Point _startPoint;
    private double _baseItemsX;
    private double _baseParallaxX;
    private double _parallaxSettledX;
    private double _lastViewportWidth = double.NaN;
    private int _lastMeasuredItemCount = -1;

    public Panorama()
    {
        LayoutUpdated += OnLayoutUpdated;
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double ItemWidthFactor
    {
        get => GetValue(ItemWidthFactorProperty);
        set => SetValue(ItemWidthFactorProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public object? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IDataTemplate? TitleTemplate
    {
        get => GetValue(TitleTemplateProperty);
        set => SetValue(TitleTemplateProperty, value);
    }

    public IDataTemplate? ItemTitleTemplate
    {
        get => GetValue(ItemTitleTemplateProperty);
        set => SetValue(ItemTitleTemplateProperty, value);
    }

    public string? ItemTitle
    {
        get => GetValue(ItemTitleProperty);
        set => SetValue(ItemTitleProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DetachViewportHandlers();

        _viewport = e.NameScope.Find<Border>("PART_Viewport");
        _titlePresenter = e.NameScope.Find<ContentPresenter>("PART_TitlePresenter");
        _titleClonePresenter = e.NameScope.Find<ContentPresenter>("PART_TitleClonePresenter");
        _backgroundPresenter = e.NameScope.Find<Rectangle>("PART_BackgroundPresenter");
        _backgroundClonePresenter = e.NameScope.Find<Rectangle>("PART_BackgroundClonePresenter");

        _titleTranslate = GetOrCreateTranslateTransform(_titlePresenter);
        _titleCloneTranslate = GetOrCreateTranslateTransform(_titleClonePresenter);
        _backgroundTranslate = GetOrCreateTranslateTransform(_backgroundPresenter);
        _backgroundCloneTranslate = GetOrCreateTranslateTransform(_backgroundClonePresenter);

        EnsureAnimatedTransform(_titleTranslate);
        EnsureAnimatedTransform(_titleCloneTranslate);
        EnsureAnimatedTransform(_backgroundTranslate);
        EnsureAnimatedTransform(_backgroundCloneTranslate);

        SyncTitleClonePresentation();
        UpdateTitleMetrics();
        AttachViewportHandlers();
        Dispatcher.UIThread.Post(RefreshMetrics, DispatcherPriority.Loaded);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedIndexProperty && !_updatingSelection)
        {
            ApplySelectedIndexIfReady(change.GetNewValue<int>());
            return;
        }

        if (change.Property == ItemWidthFactorProperty || change.Property == ItemSpacingProperty)
        {
            RefreshMetrics();
            return;
        }

        if (change.Property == TitleProperty || change.Property == TitleTemplateProperty)
        {
            SyncTitleClonePresentation();
            UpdateTitleMetrics();
            return;
        }

        if (change.Property == ItemTemplateProperty ||
            change.Property == ItemTitleTemplateProperty ||
            change.Property == ItemTitleProperty)
        {
            RefreshPreparedContainers();
            RefreshMetrics();
        }
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return item is not PanoramaItem;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new PanoramaItem();
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is not PanoramaItem panoramaItem)
        {
            return;
        }

        panoramaItem.IsGeneratedByPanorama = item is not PanoramaItem;

        if (panoramaItem.IsGeneratedByPanorama)
        {
            panoramaItem.Content = item;
            panoramaItem.ContentTemplate = ItemTemplate;
            panoramaItem.Header = ResolveItemTitle(item) ?? item;
            panoramaItem.HeaderTemplate = ItemTitleTemplate;
        }
        else if (panoramaItem.HeaderTemplate is null)
        {
            panoramaItem.HeaderTemplate = ItemTitleTemplate;
        }

        panoramaItem.HorizontalAlignment = HorizontalAlignment.Left;
        ApplyItemWidth(panoramaItem);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_viewport is null)
        {
            return;
        }

        var itemsHost = EnsureItemsHost();
        if (itemsHost is null)
        {
            return;
        }

        var viewportWidth = GetViewportWidth();
        var itemCount = GetVisibleItemCount(itemsHost);
        if (Math.Abs(viewportWidth - _lastViewportWidth) > 0.5 || itemCount != _lastMeasuredItemCount)
        {
            RefreshMetrics();
        }
    }

    private void RefreshMetrics()
    {
        var itemsHost = EnsureItemsHost();
        if (itemsHost is null)
        {
            return;
        }

        _itemCount = GetVisibleItemCount(itemsHost);
        if (_itemCount != _lastMeasuredItemCount)
        {
            _hasAppliedSelection = false;
        }

        _lastMeasuredItemCount = _itemCount;
        _lastViewportWidth = GetViewportWidth();
        UpdateTitleMetrics();

        if (itemsHost is StackPanel stackPanel)
        {
            stackPanel.Spacing = ItemSpacing;
        }

        foreach (var panoramaItem in itemsHost.Children.OfType<PanoramaItem>())
        {
            PreparePanoramaItem(panoramaItem);
        }

        var totalWidth = GetVisibleControls().Sum(GetChildWidth);
        var totalSpacing = Math.Max(0, _itemCount - 1) * ItemSpacing;
        itemsHost.Width = totalWidth + totalSpacing;

        UpdateBackgroundMetrics();

        if (_itemCount == 0)
        {
            _appliedSelectedIndex = 0;
            _parallaxSettledX = 0;
            ResetTransforms();
            return;
        }

        var normalizedSelection = WrapIndex(SelectedIndex);
        if (!_hasAppliedSelection)
        {
            ApplySelectedIndexImmediately(normalizedSelection);
            _hasAppliedSelection = true;
            StartSectionFeather(forward: true);
        }
        else
        {
            _appliedSelectedIndex = normalizedSelection;
            _parallaxSettledX = GetSettledParallaxX(normalizedSelection);
            ResetTransforms();
        }
    }

    private void ApplySelectedIndexIfReady(int requestedIndex)
    {
        if (_itemCount == 0 || _itemsHost is null || _isAnimating)
        {
            return;
        }

        ApplySelectedIndexImmediately(WrapIndex(requestedIndex));
    }

    private void ApplySelectedIndexImmediately(int targetIndex)
    {
        if (_itemCount == 0 || _itemsHost is null)
        {
            return;
        }

        var forwardDistance = (targetIndex - _appliedSelectedIndex + _itemCount) % _itemCount;
        var backwardDistance = (_appliedSelectedIndex - targetIndex + _itemCount) % _itemCount;

        ExecuteWithoutTransitions(() =>
        {
            if (forwardDistance <= backwardDistance)
            {
                for (var index = 0; index < forwardDistance; index++)
                {
                    MoveFirstVisibleToEnd();
                }
            }
            else
            {
                for (var index = 0; index < backwardDistance; index++)
                {
                    MoveLastVisibleToStart();
                }
            }

            _appliedSelectedIndex = targetIndex;
            _parallaxSettledX = GetSettledParallaxX(targetIndex);
            ResetTransforms();
        });

        SetSelectedIndexCore(targetIndex);
        UpdateSectionInteractivity();
        StartSectionFeather(forwardDistance <= backwardDistance);
    }

    private async void ViewportOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        var deltaX = e.GetPosition(_viewport).X - _startPoint.X;
        var tailNavigationStep = _pendingTailNavigationStep;
        _pendingTailNavigationStep = 0;
        ReleasePointer(e.Pointer);

        if (Math.Abs(deltaX) < DragThreshold)
        {
            if (!_isLoopPreparedForPrevious && tailNavigationStep > 0)
            {
                await MoveForwardByStepsAsync(tailNavigationStep);
            }
            else if (_isLoopPreparedForPrevious)
            {
                await CancelPreviousPreviewAsync();
            }
            else
            {
                await AnimateBackToRestAsync();
            }
        }
        else if (deltaX < 0)
        {
            if (_isLoopPreparedForPrevious)
            {
                RestoreNaturalOrder();
            }

            await MoveNextAsync();
        }
        else
        {
            await MovePreviousAsync(_isLoopPreparedForPrevious);
        }

        e.Handled = true;
    }

    private void ViewportOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewport is null ||
            _isAnimating ||
            _itemCount == 0 ||
            !e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPointerDown = true;
        _startPoint = e.GetPosition(_viewport);
        _pendingTailNavigationStep = GetTailNavigationStep(_startPoint);
        _baseItemsX = _itemsTranslate?.X ?? 0;
        _baseParallaxX = _parallaxSettledX;
        e.Pointer.Capture(_viewport);
        UpdateSectionInteractivity();
        e.Handled = true;
    }

    private void ViewportOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewport is null || !_isPointerDown || _isAnimating || _itemCount == 0)
        {
            return;
        }

        var deltaX = e.GetPosition(_viewport).X - _startPoint.X;
        var stepWidth = GetStepWidth();

        if (deltaX > 0 && !_isLoopPreparedForPrevious)
        {
            PreparePreviousLoop(stepWidth);
        }
        else if (deltaX <= 0 && _isLoopPreparedForPrevious)
        {
            RestoreNaturalOrder();
        }

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = _baseItemsX + deltaX;
        }

        var parallaxPreview = _baseParallaxX + (deltaX * ParallaxFactor);
        SetMainParallaxX(parallaxPreview);
        HideWrapClones();
        e.Handled = true;
    }

    private void ViewportOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPointerDown = false;
        _pendingTailNavigationStep = 0;
        UpdateSectionInteractivity();
    }

    private async void ViewportOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        return;
        if (_isAnimating || _itemCount == 0)
        {
            return;
        }

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta < 0)
        {
            await MoveNextAsync();
        }
        else if (delta > 0)
        {
            await MovePreviousAsync(previewPrepared: false);
        }

        e.Handled = true;
    }

    private async Task MoveNextAsync()
    {
        if (_isAnimating || _itemCount == 0 || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        var stepWidth = GetStepWidth();
        var parallaxStep = GetParallaxStep();
        var isWrap = _appliedSelectedIndex == _itemCount - 1;
        if (isWrap)
        {
            StartWrapForwardAnimation(0);
        }
        else
        {
            SetMainParallaxX(_parallaxSettledX - parallaxStep);
        }

        _itemsTranslate.X = -stepWidth;

        await Task.Delay(SlideDuration);

        ExecuteWithoutTransitions(() =>
        {
            MoveFirstVisibleToEnd();
            _appliedSelectedIndex = WrapIndex(_appliedSelectedIndex + 1);
            _parallaxSettledX = GetSettledParallaxX(_appliedSelectedIndex);
            ResetTransforms();
            HideWrapClones();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
        UpdateSectionInteractivity();
        StartSectionFeather(forward: true);
    }

    private async Task MovePreviousAsync(bool previewPrepared)
    {
        if (_isAnimating || _itemCount == 0 || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        var parallaxStep = GetParallaxStep();
        var isWrap = _appliedSelectedIndex == 0;

        if (!previewPrepared)
        {
            PreparePreviousLoop(GetStepWidth());
        }

        _itemsTranslate.X = 0;
        if (isWrap)
        {
            StartWrapBackwardAnimation(GetSettledParallaxX(_itemCount - 1));
        }
        else
        {
            SetMainParallaxX(_parallaxSettledX + parallaxStep);
        }

        await Task.Delay(SlideDuration);

        ExecuteWithoutTransitions(() =>
        {
            _appliedSelectedIndex = WrapIndex(_appliedSelectedIndex - 1);
            _parallaxSettledX = GetSettledParallaxX(_appliedSelectedIndex);
            _isLoopPreparedForPrevious = false;
            ResetTransforms();
            HideWrapClones();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
        UpdateSectionInteractivity();
        StartSectionFeather(forward: false);
    }

    private void StartSectionFeather(bool forward)
    {
        if (!TurnstileFeather.GetIsEnabled(this))
        {
            return;
        }

        _featherAnimationCancellation?.Cancel();
        _featherAnimationCancellation?.Dispose();
        _featherAnimationCancellation = new CancellationTokenSource();
        var token = _featherAnimationCancellation.Token;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
                var currentItem = GetVisibleControls().OfType<PanoramaItem>().FirstOrDefault();
                if (currentItem is not null)
                {
                    await TurnstileFeather.AnimateInAsync(currentItem, forward, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, DispatcherPriority.Background);
    }

    private async Task CancelPreviousPreviewAsync()
    {
        if (_isAnimating || _itemCount == 0 || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        _itemsTranslate.X = -GetStepWidth();
        SetMainParallaxX(_parallaxSettledX);

        await Task.Delay(SlideDuration);

        RestoreNaturalOrder();
        HideWrapClones();
        _isAnimating = false;
        UpdateSectionInteractivity();
    }

    private async Task AnimateBackToRestAsync()
    {
        if (_isAnimating || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        _itemsTranslate.X = 0;
        SetMainParallaxX(_parallaxSettledX);

        await Task.Delay(SlideDuration);

        _baseItemsX = 0;
        _baseParallaxX = _parallaxSettledX;
        HideWrapClones();
        _isAnimating = false;
        UpdateSectionInteractivity();
    }

    private void StartWrapForwardAnimation(double targetParallaxX)
    {
        PrepareTitleClone(targetParallaxX + GetTitleWrapTravel());
        PrepareBackgroundClone(targetParallaxX + GetBackgroundWrapTravel());

        if (_titleCloneTranslate is not null)
        {
            _titleCloneTranslate.X = targetParallaxX;
        }

        if (_backgroundCloneTranslate is not null)
        {
            _backgroundCloneTranslate.X = targetParallaxX;
        }

        if (_titleTranslate is not null)
        {
            _titleTranslate.X = _parallaxSettledX - GetTitleWrapTravel();
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.X = _parallaxSettledX - GetBackgroundWrapTravel();
        }
    }

    private void StartWrapBackwardAnimation(double targetParallaxX)
    {
        PrepareTitleClone(targetParallaxX - GetTitleWrapTravel());
        PrepareBackgroundClone(targetParallaxX - GetBackgroundWrapTravel());

        if (_titleCloneTranslate is not null)
        {
            _titleCloneTranslate.X = targetParallaxX;
        }

        if (_backgroundCloneTranslate is not null)
        {
            _backgroundCloneTranslate.X = targetParallaxX;
        }

        if (_titleTranslate is not null)
        {
            _titleTranslate.X = _parallaxSettledX + GetTitleWrapTravel();
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.X = _parallaxSettledX + GetBackgroundWrapTravel();
        }
    }

    private void PreparePreviousLoop(double stepWidth)
    {
        if (_itemsHost is null || _itemsTranslate is null)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            MoveLastVisibleToStart();
            _itemsTranslate.X = -stepWidth;
            SetMainParallaxX(_parallaxSettledX);
        });

        _baseItemsX = -stepWidth;
        _baseParallaxX = _parallaxSettledX;
        _isLoopPreparedForPrevious = true;
        UpdateSectionInteractivity();
    }

    private void RestoreNaturalOrder()
    {
        if (_itemsHost is null)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            MoveFirstVisibleToEnd();
            ResetTransforms();
        });

        _isLoopPreparedForPrevious = false;
        UpdateSectionInteractivity();
    }

    private void ResetTransforms()
    {
        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = 0;
        }

        if (_titleTranslate is not null)
        {
            _titleTranslate.X = _parallaxSettledX;
        }

        if (_titleCloneTranslate is not null)
        {
            _titleCloneTranslate.X = _parallaxSettledX;
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.X = _parallaxSettledX;
        }

        if (_backgroundCloneTranslate is not null)
        {
            _backgroundCloneTranslate.X = _parallaxSettledX;
        }

        _baseItemsX = 0;
        _baseParallaxX = _parallaxSettledX;
        UpdateSectionInteractivity();
    }

    private void SetMainParallaxX(double x)
    {
        if (_titleTranslate is not null)
        {
            _titleTranslate.X = x;
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.X = x;
        }
    }

    private void UpdateBackgroundMetrics()
    {
        var backgroundWidth = GetViewportWidth() + GetTotalParallaxSpan();
        if (_backgroundPresenter is not null)
        {
            _backgroundPresenter.Width = backgroundWidth;
        }

        if (_backgroundClonePresenter is not null)
        {
            _backgroundClonePresenter.Width = backgroundWidth;
        }
    }

    private void PreparePanoramaItem(PanoramaItem panoramaItem)
    {
        ApplyItemWidth(panoramaItem);

        if (panoramaItem.IsGeneratedByPanorama)
        {
            panoramaItem.ContentTemplate = ItemTemplate;
            panoramaItem.HeaderTemplate = ItemTitleTemplate;
            panoramaItem.Header = ResolveItemTitle(panoramaItem.Content) ?? panoramaItem.Content;
        }
        else if (panoramaItem.HeaderTemplate is null)
        {
            panoramaItem.HeaderTemplate = ItemTitleTemplate;
        }
    }

    private void ApplyItemWidth(PanoramaItem panoramaItem)
    {
        var viewportWidth = GetViewportWidth();
        if (viewportWidth <= 0)
        {
            return;
        }

        panoramaItem.Width = viewportWidth * Math.Max(0, ItemWidthFactor);
    }

    private void RefreshPreparedContainers()
    {
        if (_itemsHost is null)
        {
            return;
        }

        foreach (var panoramaItem in _itemsHost.Children.OfType<PanoramaItem>())
        {
            PreparePanoramaItem(panoramaItem);
        }

        UpdateSectionInteractivity();
    }

    private void AttachViewportHandlers()
    {
        if (_viewport is null)
        {
            return;
        }

        _viewport.PointerPressed += ViewportOnPointerPressed;
        _viewport.PointerMoved += ViewportOnPointerMoved;
        _viewport.PointerReleased += ViewportOnPointerReleased;
        _viewport.PointerCaptureLost += ViewportOnPointerCaptureLost;
        _viewport.PointerWheelChanged += ViewportOnPointerWheelChanged;
    }

    private void DetachViewportHandlers()
    {
        if (_viewport is null)
        {
            return;
        }

        _viewport.PointerPressed -= ViewportOnPointerPressed;
        _viewport.PointerMoved -= ViewportOnPointerMoved;
        _viewport.PointerReleased -= ViewportOnPointerReleased;
        _viewport.PointerCaptureLost -= ViewportOnPointerCaptureLost;
        _viewport.PointerWheelChanged -= ViewportOnPointerWheelChanged;
    }

    private Panel? EnsureItemsHost()
    {
        if (ItemsPanelRoot is null)
        {
            return null;
        }

        if (!ReferenceEquals(_itemsHost, ItemsPanelRoot))
        {
            _itemsHost = ItemsPanelRoot;
            _itemsTranslate = GetOrCreateTranslateTransform(_itemsHost);
            EnsureAnimatedTransform(_itemsTranslate);
            _hasAppliedSelection = false;
        }

        if (_itemsHost is StackPanel stackPanel)
        {
            stackPanel.Orientation = Orientation.Horizontal;
            stackPanel.HorizontalAlignment = HorizontalAlignment.Left;
            stackPanel.Spacing = ItemSpacing;
        }

        return _itemsHost;
    }

    private void SyncTitleClonePresentation()
    {
        if (_titleClonePresenter is null)
        {
            return;
        }

        if (Title is Visual)
        {
            _titleClonePresenter.Content = null;
            _titleClonePresenter.Opacity = 0;
            return;
        }

        _titleClonePresenter.Content = Title;
        _titleClonePresenter.ContentTemplate = TitleTemplate;
    }

    private void UpdateTitleMetrics()
    {
        UpdateTitlePresenterWidth(_titlePresenter);
        UpdateTitlePresenterWidth(_titleClonePresenter);
    }

    private static void UpdateTitlePresenterWidth(ContentPresenter? presenter)
    {
        if (presenter is null)
        {
            return;
        }

        presenter.Width = double.NaN;
        presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var desiredWidth = presenter.DesiredSize.Width;
        if (double.IsNaN(desiredWidth) || double.IsInfinity(desiredWidth) || desiredWidth <= 0)
        {
            return;
        }

        presenter.Width = desiredWidth;
    }

    private void PrepareTitleClone(double x)
    {
        if (_titleClonePresenter is null || _titleCloneTranslate is null)
        {
            return;
        }

        SyncTitleClonePresentation();
        if (_titleClonePresenter.Content is null)
        {
            return;
        }

        PrepareCloneVisual(_titleClonePresenter, _titleCloneTranslate, x);
    }

    private void PrepareBackgroundClone(double x)
    {
        if (_backgroundClonePresenter is null || _backgroundCloneTranslate is null)
        {
            return;
        }

        PrepareCloneVisual(_backgroundClonePresenter, _backgroundCloneTranslate, x);
    }

    private static void PrepareCloneVisual(Visual visual, TranslateTransform transform, double x)
    {
        var transitions = transform.Transitions;
        transform.Transitions = null;
        transform.X = x;
        if (visual is Control control)
        {
            control.Opacity = 1;
        }
        else if (visual is Shape shape)
        {
            shape.Opacity = 1;
        }

        transform.Transitions = transitions;
    }

    private void HideWrapClones()
    {
        if (_titleClonePresenter is not null)
        {
            _titleClonePresenter.Opacity = 0;
        }

        if (_backgroundClonePresenter is not null)
        {
            _backgroundClonePresenter.Opacity = 0;
        }
    }

    private async Task MoveForwardByStepsAsync(int stepCount)
    {
        if (stepCount <= 0)
        {
            return;
        }

        for (var index = 0; index < stepCount; index++)
        {
            await MoveNextAsync();
        }
    }

    private void UpdateSectionInteractivity()
    {
        if (_itemsHost is null)
        {
            return;
        }

        var interactiveIndex = !_isPointerDown && !_isAnimating && !_isLoopPreparedForPrevious ? 0 : -1;
        var childIndex = 0;
        foreach (var panoramaItem in _itemsHost.Children.OfType<PanoramaItem>())
        {
            if (!IsVisibleSection(panoramaItem))
            {
                panoramaItem.IsHitTestVisible = false;
                continue;
            }

            panoramaItem.IsHitTestVisible = childIndex == interactiveIndex;
            childIndex++;
        }
    }

    private int GetTailNavigationStep(Point position)
    {
        if (_itemsHost is null || _itemCount <= 1)
        {
            return 0;
        }

        var panelX = _itemsTranslate?.X ?? 0;
        var viewportX = position.X - panelX;
        var viewportWidth = GetViewportWidth();
        if (viewportX < 0 || viewportX > viewportWidth)
        {
            return 0;
        }

        var currentOffset = 0d;
        var childIndex = 0;
        foreach (var control in GetVisibleControls())
        {
            var childWidth = GetChildWidth(control);
            var start = currentOffset;
            var end = start + childWidth;

            if (childIndex > 0)
            {
                var visibleStart = Math.Max(0, start);
                var visibleEnd = Math.Min(viewportWidth, end);
                if (visibleStart < visibleEnd && viewportX >= visibleStart && viewportX <= visibleEnd)
                {
                    return childIndex;
                }
            }

            currentOffset = end + ItemSpacing;
            childIndex++;
        }

        return 0;
    }

    private void ReleasePointer(IPointer pointer)
    {
        _isPointerDown = false;
        pointer.Capture(null);
        UpdateSectionInteractivity();
    }

    private void ExecuteWithoutTransitions(Action action)
    {
        var itemsTransitions = _itemsTranslate?.Transitions;
        var titleTransitions = _titleTranslate?.Transitions;
        var titleCloneTransitions = _titleCloneTranslate?.Transitions;
        var backgroundTransitions = _backgroundTranslate?.Transitions;
        var backgroundCloneTransitions = _backgroundCloneTranslate?.Transitions;

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.Transitions = null;
        }

        if (_titleTranslate is not null)
        {
            _titleTranslate.Transitions = null;
        }

        if (_titleCloneTranslate is not null)
        {
            _titleCloneTranslate.Transitions = null;
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.Transitions = null;
        }

        if (_backgroundCloneTranslate is not null)
        {
            _backgroundCloneTranslate.Transitions = null;
        }

        action();

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.Transitions = itemsTransitions;
        }

        if (_titleTranslate is not null)
        {
            _titleTranslate.Transitions = titleTransitions;
        }

        if (_titleCloneTranslate is not null)
        {
            _titleCloneTranslate.Transitions = titleCloneTransitions;
        }

        if (_backgroundTranslate is not null)
        {
            _backgroundTranslate.Transitions = backgroundTransitions;
        }

        if (_backgroundCloneTranslate is not null)
        {
            _backgroundCloneTranslate.Transitions = backgroundCloneTransitions;
        }
    }

    private void SetSelectedIndexCore(int index)
    {
        _updatingSelection = true;
        SetCurrentValue(SelectedIndexProperty, index);
        _updatingSelection = false;
    }

    private object? ResolveItemTitle(object? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(ItemTitle))
        {
            return null;
        }

        var value = item;
        foreach (var part in ItemTitle!.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value is null)
            {
                return null;
            }

            var propertyInfo = value.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            value = propertyInfo?.GetValue(value);
        }

        return value;
    }

    private double GetSettledParallaxX(int index)
    {
        return -index * GetParallaxStep();
    }

    private double GetStepWidth()
    {
        if (_itemsHost is null || _itemCount == 0)
        {
            return 0;
        }

        var firstControl = GetVisibleControls().FirstOrDefault();
        return firstControl is null ? 0 : GetChildWidth(firstControl) + ItemSpacing;
    }

    private double GetParallaxStep()
    {
        return GetStepWidth() * ParallaxFactor;
    }

    private double GetTotalParallaxSpan()
    {
        return Math.Max(0, _itemCount - 1) * GetParallaxStep();
    }

    private double GetTitleWrapTravel()
    {
        var titleWidth = _titlePresenter?.Bounds.Width ?? 0;
        return Math.Max(titleWidth, GetViewportWidth());
    }

    private double GetBackgroundWrapTravel()
    {
        var backgroundWidth = _backgroundPresenter?.Bounds.Width ?? 0;
        return Math.Max(backgroundWidth, GetViewportWidth());
    }

    private double GetViewportWidth()
    {
        if (_viewport is not null && _viewport.Bounds.Width > 0)
        {
            return _viewport.Bounds.Width;
        }

        return Bounds.Width;
    }

    private int WrapIndex(int index)
    {
        if (_itemCount == 0)
        {
            return 0;
        }

        var wrapped = index % _itemCount;
        return wrapped < 0 ? wrapped + _itemCount : wrapped;
    }

    private IEnumerable<Control> GetVisibleControls()
    {
        return _itemsHost?.Children.OfType<Control>().Where(IsVisibleSection) ?? Enumerable.Empty<Control>();
    }

    private static int GetVisibleItemCount(Panel itemsHost)
    {
        return itemsHost.Children.OfType<Control>().Count(IsVisibleSection);
    }

    private static bool IsVisibleSection(Control control)
    {
        return control.IsVisible;
    }

    private void MoveFirstVisibleToEnd()
    {
        if (_itemsHost is null)
        {
            return;
        }

        var firstVisibleIndex = GetFirstVisibleChildIndex();
        var lastVisibleIndex = GetLastVisibleChildIndex();
        if (firstVisibleIndex < 0 || lastVisibleIndex < 0 || firstVisibleIndex == lastVisibleIndex)
        {
            return;
        }

        _itemsHost.Children.Move(firstVisibleIndex, lastVisibleIndex);
    }

    private void MoveLastVisibleToStart()
    {
        if (_itemsHost is null)
        {
            return;
        }

        var firstVisibleIndex = GetFirstVisibleChildIndex();
        var lastVisibleIndex = GetLastVisibleChildIndex();
        if (firstVisibleIndex < 0 || lastVisibleIndex < 0 || firstVisibleIndex == lastVisibleIndex)
        {
            return;
        }

        _itemsHost.Children.Move(lastVisibleIndex, firstVisibleIndex);
    }

    private int GetFirstVisibleChildIndex()
    {
        if (_itemsHost is null)
        {
            return -1;
        }

        for (var index = 0; index < _itemsHost.Children.Count; index++)
        {
            if (_itemsHost.Children[index] is Control control && IsVisibleSection(control))
            {
                return index;
            }
        }

        return -1;
    }

    private int GetLastVisibleChildIndex()
    {
        if (_itemsHost is null)
        {
            return -1;
        }

        for (var index = _itemsHost.Children.Count - 1; index >= 0; index--)
        {
            if (_itemsHost.Children[index] is Control control && IsVisibleSection(control))
            {
                return index;
            }
        }

        return -1;
    }

    private static double GetChildWidth(Control control)
    {
        return control.Bounds.Width > 0 ? control.Bounds.Width : control.Width;
    }

    private static TranslateTransform? GetOrCreateTranslateTransform(Visual? visual)
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

    private static void EnsureAnimatedTransform(TranslateTransform? translateTransform)
    {
        if (translateTransform is null || translateTransform.Transitions is { Count: > 0 })
        {
            return;
        }

        translateTransform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = SlideDuration,
                Easing = new CubicEaseOut()
            }
        };
    }
}
