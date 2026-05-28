using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Xenon.Controls;

public class PanoramaPivot : ItemsControl
{
    private const double DragThreshold = 60;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(600);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<PanoramaPivot, int>(nameof(SelectedIndex), 0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ItemWidthFactorProperty =
        AvaloniaProperty.Register<PanoramaPivot, double>(nameof(ItemWidthFactor), 0.9d);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<PanoramaPivot, double>(nameof(ItemSpacing), 20d);

    public static readonly StyledProperty<IDataTemplate?> ItemTitleTemplateProperty =
        AvaloniaProperty.Register<PanoramaPivot, IDataTemplate?>(nameof(ItemTitleTemplate));

    public static readonly StyledProperty<string?> ItemTitleProperty =
        AvaloniaProperty.Register<PanoramaPivot, string?>(nameof(ItemTitle));

    public static readonly StyledProperty<bool> IsLoopingProperty =
        AvaloniaProperty.Register<PanoramaPivot, bool>(nameof(IsLooping), true);

    public static readonly StyledProperty<bool> ShowsNextPreviewProperty =
        AvaloniaProperty.Register<PanoramaPivot, bool>(nameof(ShowsNextPreview), true);

    public static readonly StyledProperty<bool> UseSwipesProperty =
        AvaloniaProperty.Register<PanoramaPivot, bool>(nameof(UseSwipes), true);

    private Border? _viewport;
    private StackPanel? _headerPanel;
    private Panel? _itemsHost;
    private TranslateTransform? _itemsTranslate;
    private TranslateTransform? _headerTranslate;
    private int _itemCount;
    private int _appliedSelectedIndex;
    private bool _hasAppliedSelection;
    private bool _isPointerDown;
    private bool _isAnimating;
    private bool _isLoopPreparedForPrevious;
    private bool _updatingSelection;
    private Point _startPoint;
    private double _baseItemsX;
    private double _baseHeaderX;
    private double _lastViewportWidth = double.NaN;
    private int _lastMeasuredItemCount = -1;

    public PanoramaPivot()
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

    public bool IsLooping
    {
        get => GetValue(IsLoopingProperty);
        set => SetValue(IsLoopingProperty, value);
    }

    public bool ShowsNextPreview
    {
        get => GetValue(ShowsNextPreviewProperty);
        set => SetValue(ShowsNextPreviewProperty, value);
    }

    public bool UseSwipes
    {
        get => GetValue(UseSwipesProperty);
        set => SetValue(UseSwipesProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DetachViewportHandlers();
        _viewport = e.NameScope.Find<Border>("PART_Viewport");
        _headerPanel = e.NameScope.Find<StackPanel>("PART_HeaderPanel");
        _itemsTranslate = GetOrCreateTranslateTransform(EnsureItemsHost());
        _headerTranslate = GetOrCreateTranslateTransform(_headerPanel);
        EnsureAnimatedTransform(_itemsTranslate);
        EnsureAnimatedTransform(_headerTranslate);
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

        if (change.Property == ItemWidthFactorProperty ||
            change.Property == ItemSpacingProperty ||
            change.Property == ShowsNextPreviewProperty ||
            change.Property == IsLoopingProperty)
        {
            _hasAppliedSelection = false;
            RefreshMetrics();
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
        return item is not PanoramaPivotItem;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new PanoramaPivotItem();
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is not PanoramaPivotItem pivotItem)
        {
            return;
        }

        pivotItem.IsGeneratedByPanoramaPivot = item is not PanoramaPivotItem;
        if (pivotItem.IsGeneratedByPanoramaPivot)
        {
            pivotItem.Content = item;
            pivotItem.ContentTemplate = ItemTemplate;
            pivotItem.Header = ResolveItemTitle(item) ?? item;
            pivotItem.HeaderTemplate = ItemTitleTemplate;
        }
        else if (pivotItem.HeaderTemplate is null)
        {
            pivotItem.HeaderTemplate = ItemTitleTemplate;
        }

        pivotItem.HorizontalAlignment = HorizontalAlignment.Left;
        ApplyItemWidth(pivotItem);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_viewport is null || EnsureItemsHost() is null)
        {
            return;
        }

        var viewportWidth = GetViewportWidth();
        var itemCount = GetCurrentItemCount();
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

        foreach (var item in itemsHost.Children.OfType<PanoramaPivotItem>())
        {
            PreparePanoramaPivotItem(item);
        }

        if (itemsHost is StackPanel stackPanel)
        {
            stackPanel.Spacing = ItemSpacing;
        }

        itemsHost.Width = GetVisibleControls().Sum(GetChildWidth) + Math.Max(0, _itemCount - 1) * ItemSpacing;
        EnsureHeaderPanel();
        UpdateHeaderStates();

        if (_itemCount == 0)
        {
            _appliedSelectedIndex = 0;
            ResetTransforms();
            return;
        }

        var normalizedSelection = NormalizeIndex(SelectedIndex);
        if (!_hasAppliedSelection)
        {
            ApplySelectedIndexImmediately(normalizedSelection);
            _hasAppliedSelection = true;
        }
        else if (IsLooping)
        {
            _appliedSelectedIndex = normalizedSelection;
            ResetTransforms();
        }
        else
        {
            _appliedSelectedIndex = normalizedSelection;
            ResetTransforms();
        }
    }

    private void ApplySelectedIndexIfReady(int requestedIndex)
    {
        if (_itemCount == 0 || _itemsHost is null || _isAnimating)
        {
            return;
        }

        ApplySelectedIndexImmediately(NormalizeIndex(requestedIndex));
    }

    private void ApplySelectedIndexImmediately(int targetIndex)
    {
        var itemCount = GetCurrentItemCount();
        if (itemCount == 0 || _itemsHost is null)
        {
            return;
        }

        targetIndex = NormalizeIndex(targetIndex, itemCount);
        if (!IsLooping)
        {
            _appliedSelectedIndex = targetIndex;
            ResetTransforms();
            UpdateHeaderStates();
            SetSelectedIndexCore(targetIndex);
            return;
        }

        var appliedSelectedIndex = WrapIndex(_appliedSelectedIndex, itemCount);
        var forwardDistance = (targetIndex - appliedSelectedIndex + itemCount) % itemCount;
        var backwardDistance = (appliedSelectedIndex - targetIndex + itemCount) % itemCount;

        ExecuteWithoutTransitions(() =>
        {
            if (forwardDistance <= backwardDistance)
            {
                for (var index = 0; index < forwardDistance; index++)
                {
                    MoveFirstVisibleToEnd();
                    MoveHeaderFirstToEnd();
                }
            }
            else
            {
                for (var index = 0; index < backwardDistance; index++)
                {
                    MoveLastVisibleToStart();
                    MoveHeaderLastToStart();
                }
            }

            _appliedSelectedIndex = targetIndex;
            UpdateHeaderStates();
            ResetTransforms();
        });

        SetSelectedIndexCore(targetIndex);
    }

    private void ViewportOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!UseSwipes ||
            _viewport is null ||
            _isAnimating ||
            _itemCount == 0 ||
            IsGestureHandledByChildHorizontalScroller(e.Source) ||
            !e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPointerDown = true;
        _startPoint = e.GetPosition(_viewport);
        _baseItemsX = _itemsTranslate?.X ?? 0;
        _baseHeaderX = _headerTranslate?.X ?? 0;
        e.Pointer.Capture(_viewport);
        e.Handled = true;
    }

    private void ViewportOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewport is null || !_isPointerDown || _isAnimating || _itemCount == 0)
        {
            return;
        }

        var deltaX = e.GetPosition(_viewport).X - _startPoint.X;
        var contentStep = GetContentStepWidth();

        if (IsLooping)
        {
            if (deltaX > 0 && !_isLoopPreparedForPrevious)
            {
                PreparePreviousLoop(contentStep);
            }
            else if (deltaX <= 0 && _isLoopPreparedForPrevious)
            {
                RestoreNaturalOrder();
            }
        }
        else
        {
            var isAtFirst = _appliedSelectedIndex <= 0;
            var isAtLast = _appliedSelectedIndex >= _itemCount - 1;
            if ((isAtFirst && deltaX > 0) || (isAtLast && deltaX < 0))
            {
                deltaX *= 0.25d;
            }
        }

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = _baseItemsX + deltaX;
        }

        if (IsLooping && _headerTranslate is not null)
        {
            var headerStep = GetActiveHeaderStep();
            var headerFactor = contentStep <= 0 ? 0 : headerStep / contentStep;
            _headerTranslate.X = _baseHeaderX + (deltaX * headerFactor);
        }

        e.Handled = true;
    }

    private async void ViewportOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        var deltaX = e.GetPosition(_viewport).X - _startPoint.X;
        ReleasePointer(e.Pointer);

        if (Math.Abs(deltaX) < DragThreshold)
        {
            if (_isLoopPreparedForPrevious)
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

    private void ViewportOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPointerDown = false;
    }

    private async Task MoveNextAsync()
    {
        if (_isAnimating || _itemCount == 0 || _itemsTranslate is null)
        {
            return;
        }

        if (!IsLooping && _appliedSelectedIndex >= _itemCount - 1)
        {
            await AnimateBackToRestAsync();
            return;
        }

        _isAnimating = true;
        _itemsTranslate.X = _baseItemsX - GetContentStepWidth();
        if (IsLooping && _headerTranslate is not null)
        {
            _headerTranslate.X = _baseHeaderX - GetActiveHeaderStep();
        }

        await Task.Delay(SlideDuration);

        ExecuteWithoutTransitions(() =>
        {
            if (IsLooping && GetCurrentItemCount() > 1)
            {
                MoveFirstVisibleToEnd();
                MoveHeaderFirstToEnd();
                _appliedSelectedIndex = WrapIndex(_appliedSelectedIndex + 1, _itemCount);
            }
            else
            {
                _appliedSelectedIndex = Math.Min(_itemCount - 1, _appliedSelectedIndex + 1);
            }

            UpdateHeaderStates();
            ResetTransforms();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
    }

    private async Task MovePreviousAsync(bool previewPrepared)
    {
        if (_isAnimating || _itemCount == 0 || _itemsTranslate is null)
        {
            return;
        }

        if (!IsLooping && _appliedSelectedIndex <= 0)
        {
            await AnimateBackToRestAsync();
            return;
        }

        _isAnimating = true;
        if (IsLooping)
        {
            if (!previewPrepared)
            {
                PreparePreviousLoop(GetContentStepWidth());
            }

            _itemsTranslate.X = 0;
            if (_headerTranslate is not null)
            {
                _headerTranslate.X = 0;
            }
        }
        else
        {
            _itemsTranslate.X = _baseItemsX + GetContentStepWidth();
        }

        await Task.Delay(SlideDuration);

        ExecuteWithoutTransitions(() =>
        {
            _appliedSelectedIndex = IsLooping
                ? WrapIndex(_appliedSelectedIndex - 1, _itemCount)
                : Math.Max(0, _appliedSelectedIndex - 1);
            _isLoopPreparedForPrevious = false;
            UpdateHeaderStates();
            ResetTransforms();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
    }

    private async Task CancelPreviousPreviewAsync()
    {
        if (_isAnimating || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        _itemsTranslate.X = -GetContentStepWidth();
        if (_headerTranslate is not null)
        {
            _headerTranslate.X = -GetActiveHeaderStep();
        }

        await Task.Delay(SlideDuration);
        RestoreNaturalOrder();
        _isAnimating = false;
    }

    private async Task AnimateBackToRestAsync()
    {
        if (_isAnimating || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        ResetTransforms();
        await Task.Delay(SlideDuration);
        _isAnimating = false;
    }

    private void PreparePreviousLoop(double contentStep)
    {
        if (_itemsHost is null || _itemsTranslate is null || _itemCount == 0)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            if (_itemCount > 1)
            {
                MoveLastVisibleToStart();
                MoveHeaderLastToStart();
            }

            _itemsTranslate.X = -contentStep;
            if (_headerTranslate is not null)
            {
                _headerTranslate.X = -GetActiveHeaderStep();
            }
        });

        _baseItemsX = -contentStep;
        _baseHeaderX = -GetActiveHeaderStep();
        _isLoopPreparedForPrevious = true;
    }

    private void RestoreNaturalOrder()
    {
        if (_itemsHost is null || _itemCount == 0)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            if (_itemCount > 1)
            {
                MoveFirstVisibleToEnd();
                MoveHeaderFirstToEnd();
            }

            UpdateHeaderStates();
            ResetTransforms();
        });

        _isLoopPreparedForPrevious = false;
    }

    private void ResetTransforms()
    {
        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = IsLooping ? 0 : -GetOffsetForIndex(_appliedSelectedIndex);
        }

        if (_headerTranslate is not null)
        {
            _headerTranslate.X = 0;
        }

        _baseItemsX = _itemsTranslate?.X ?? 0;
        _baseHeaderX = 0;
    }

    private void PreparePanoramaPivotItem(PanoramaPivotItem item)
    {
        ApplyItemWidth(item);
        if (item.IsGeneratedByPanoramaPivot)
        {
            item.ContentTemplate = ItemTemplate;
            item.HeaderTemplate = ItemTitleTemplate;
            item.Header = ResolveItemTitle(item.Content) ?? item.Content;
        }
        else if (item.HeaderTemplate is null)
        {
            item.HeaderTemplate = ItemTitleTemplate;
        }
    }

    private void ApplyItemWidth(PanoramaPivotItem item)
    {
        var viewportWidth = GetViewportWidth();
        if (viewportWidth <= 0)
        {
            return;
        }

        item.Width = ShowsNextPreview ? viewportWidth * Math.Max(0, ItemWidthFactor) : viewportWidth;
    }

    private void RefreshPreparedContainers()
    {
        if (_itemsHost is null)
        {
            return;
        }

        foreach (var item in _itemsHost.Children.OfType<PanoramaPivotItem>())
        {
            PreparePanoramaPivotItem(item);
        }

        EnsureHeaderPanel();
        UpdateHeaderStates();
    }

    private void EnsureHeaderPanel()
    {
        if (_headerPanel is null || _itemsHost is null)
        {
            return;
        }

        _headerPanel.Spacing = ItemSpacing;
        var visibleItems = GetVisibleControls().OfType<PanoramaPivotItem>().ToArray();
        if (_headerPanel.Children.Count != visibleItems.Length)
        {
            _headerPanel.Children.Clear();
            foreach (var item in visibleItems)
            {
                _headerPanel.Children.Add(CreateHeaderControl(item));
            }
            return;
        }

        for (var index = 0; index < visibleItems.Length; index++)
        {
            if (_headerPanel.Children[index] is not ContentControl headerControl)
            {
                continue;
            }

            headerControl.Content = CreateHeaderContent(visibleItems[index].Header);
            headerControl.ContentTemplate = GetHeaderContentTemplate(visibleItems[index]);
        }
    }

    private ContentControl CreateHeaderControl(PanoramaPivotItem item)
    {
        var headerControl = new ContentControl
        {
            Content = CreateHeaderContent(item.Header),
            ContentTemplate = GetHeaderContentTemplate(item),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        headerControl.PointerPressed += HeaderControlOnPointerPressed;
        return headerControl;
    }

    private static object? CreateHeaderContent(object? header)
    {
        if (header is TextBlock textBlock)
        {
            return new TextBlock
            {
                Text = textBlock.Text,
                FontSize = textBlock.FontSize,
                FontFamily = textBlock.FontFamily,
                FontStyle = textBlock.FontStyle,
                FontWeight = textBlock.FontWeight,
                Foreground = textBlock.Foreground,
                Margin = textBlock.Margin,
                MaxLines = textBlock.MaxLines,
                TextAlignment = textBlock.TextAlignment,
                TextTrimming = textBlock.TextTrimming,
                TextWrapping = textBlock.TextWrapping,
                VerticalAlignment = textBlock.VerticalAlignment
            };
        }

        if (header is Control control)
        {
            return control.DataContext ?? control.ToString();
        }

        return header;
    }

    private IDataTemplate? GetHeaderContentTemplate(PanoramaPivotItem item)
    {
        return item.Header is Control ? null : item.HeaderTemplate ?? ItemTitleTemplate;
    }

    private async void HeaderControlOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || _headerPanel is null || _isAnimating || _itemCount == 0)
        {
            return;
        }

        var visualIndex = _headerPanel.Children.IndexOf(control);
        if (visualIndex < 0)
        {
            return;
        }

        e.Handled = true;
        if (visualIndex == GetVisualSelectedIndex())
        {
            return;
        }

        if (IsLooping)
        {
            if (visualIndex == 1)
            {
                await MoveNextAsync();
                return;
            }

            if (visualIndex == _itemCount - 1)
            {
                await MovePreviousAsync(previewPrepared: false);
                return;
            }

            ApplySelectedIndexImmediately(WrapIndex(_appliedSelectedIndex + visualIndex, _itemCount));
            return;
        }

        ApplySelectedIndexImmediately(visualIndex);
    }

    private void UpdateHeaderStates()
    {
        if (_headerPanel is null)
        {
            return;
        }

        var selectedVisualIndex = GetVisualSelectedIndex();
        for (var index = 0; index < _headerPanel.Children.Count; index++)
        {
            if (_headerPanel.Children[index] is Control control)
            {
                control.Opacity = index == selectedVisualIndex ? 1d : 0.48d;
            }
        }
    }

    private int GetVisualSelectedIndex()
    {
        return IsLooping ? 0 : _appliedSelectedIndex;
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
        }

        return _itemsHost;
    }

    private void ExecuteWithoutTransitions(Action action)
    {
        var itemsTransitions = _itemsTranslate?.Transitions;
        var headerTransitions = _headerTranslate?.Transitions;
        if (_itemsTranslate is not null)
        {
            _itemsTranslate.Transitions = null;
        }

        if (_headerTranslate is not null)
        {
            _headerTranslate.Transitions = null;
        }

        action();

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.Transitions = itemsTransitions;
        }

        if (_headerTranslate is not null)
        {
            _headerTranslate.Transitions = headerTransitions;
        }
    }

    private void ReleasePointer(IPointer pointer)
    {
        _isPointerDown = false;
        pointer.Capture(null);
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

    private double GetContentStepWidth()
    {
        if (_itemsHost is null || GetCurrentItemCount() == 0)
        {
            return 0;
        }

        var firstControl = GetVisibleControls().FirstOrDefault();
        return firstControl is null ? 0 : GetChildWidth(firstControl) + ItemSpacing;
    }

    private double GetActiveHeaderStep()
    {
        if (_headerPanel is null || _headerPanel.Children.Count == 0)
        {
            return 0;
        }

        if (_headerPanel.Children[0] is not Control control)
        {
            return 0;
        }

        return GetChildWidth(control) + ItemSpacing;
    }

    private double GetOffsetForIndex(int index)
    {
        return Math.Max(0, index) * GetContentStepWidth();
    }

    private double GetViewportWidth()
    {
        if (_viewport is not null && _viewport.Bounds.Width > 0)
        {
            return _viewport.Bounds.Width;
        }

        return Bounds.Width;
    }

    private int NormalizeIndex(int index)
    {
        return NormalizeIndex(index, _itemCount);
    }

    private int NormalizeIndex(int index, int itemCount)
    {
        return IsLooping ? WrapIndex(index, itemCount) : ClampIndex(index, itemCount);
    }

    private static int WrapIndex(int index, int itemCount)
    {
        if (itemCount == 0)
        {
            return 0;
        }

        var wrapped = index % itemCount;
        return wrapped < 0 ? wrapped + itemCount : wrapped;
    }

    private static int ClampIndex(int index, int itemCount)
    {
        if (itemCount == 0)
        {
            return 0;
        }

        return Math.Clamp(index, 0, itemCount - 1);
    }

    private int GetCurrentItemCount()
    {
        var count = _itemsHost is null ? 0 : GetVisibleItemCount(_itemsHost);
        _itemCount = count;
        return count;
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
        if (firstVisibleIndex >= 0 && lastVisibleIndex >= 0 && firstVisibleIndex != lastVisibleIndex)
        {
            _itemsHost.Children.Move(firstVisibleIndex, lastVisibleIndex);
        }
    }

    private void MoveLastVisibleToStart()
    {
        if (_itemsHost is null)
        {
            return;
        }

        var firstVisibleIndex = GetFirstVisibleChildIndex();
        var lastVisibleIndex = GetLastVisibleChildIndex();
        if (firstVisibleIndex >= 0 && lastVisibleIndex >= 0 && firstVisibleIndex != lastVisibleIndex)
        {
            _itemsHost.Children.Move(lastVisibleIndex, firstVisibleIndex);
        }
    }

    private void MoveHeaderFirstToEnd()
    {
        if (_headerPanel?.Children.Count > 0)
        {
            _headerPanel.Children.Move(0, _headerPanel.Children.Count - 1);
        }
    }

    private void MoveHeaderLastToStart()
    {
        if (_headerPanel?.Children.Count > 0)
        {
            _headerPanel.Children.Move(_headerPanel.Children.Count - 1, 0);
        }
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

    private static bool IsGestureHandledByChildHorizontalScroller(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor =>
        {
            if (ancestor.GetType().Name == "QueueCarousel")
            {
                return true;
            }

            return ancestor is ScrollViewer scrollViewer &&
                   scrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                   scrollViewer.Extent.Width > scrollViewer.Viewport.Width + 0.5d;
        });
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
