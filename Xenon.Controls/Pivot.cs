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

public class Pivot : ItemsControl
{
    private const double DragThreshold = 60;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(600);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<Pivot, int>(nameof(SelectedIndex), 0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ItemWidthFactorProperty =
        AvaloniaProperty.Register<Pivot, double>(nameof(ItemWidthFactor), 1d);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<Pivot, double>(nameof(ItemSpacing), 20d);

    public static readonly StyledProperty<object?> TitleProperty =
        AvaloniaProperty.Register<Pivot, object?>(nameof(Title));

    public static readonly StyledProperty<IDataTemplate?> TitleTemplateProperty =
        AvaloniaProperty.Register<Pivot, IDataTemplate?>(nameof(TitleTemplate));

    public static readonly StyledProperty<IDataTemplate?> ItemTitleTemplateProperty =
        AvaloniaProperty.Register<Pivot, IDataTemplate?>(nameof(ItemTitleTemplate));

    public static readonly StyledProperty<string?> ItemTitleProperty =
        AvaloniaProperty.Register<Pivot, string?>(nameof(ItemTitle));

    private Border? _viewport;
    private StackPanel? _headerPanel;
    private Panel? _itemsHost;
    private TranslateTransform? _headerTranslate;
    private TranslateTransform? _itemsTranslate;
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

    public Pivot()
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
        _headerPanel = e.NameScope.Find<StackPanel>("PART_HeaderPanel");

        _itemsTranslate = GetOrCreateTranslateTransform(EnsureItemsHost());
        _headerTranslate = GetOrCreateTranslateTransform(_headerPanel);
        EnsureAnimatedTransform(_itemsTranslate);
        EnsureAnimatedTransform(_headerTranslate);

        if (_headerPanel is not null)
        {
            _headerPanel.Spacing = ItemSpacing;
        }

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
        return item is not PivotItem;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new PivotItem();
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is not PivotItem pivotItem)
        {
            return;
        }

        pivotItem.IsGeneratedByPivot = item is not PivotItem;
        if (pivotItem.IsGeneratedByPivot)
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

        foreach (var pivotItem in itemsHost.Children.OfType<PivotItem>())
        {
            PreparePivotItem(pivotItem);
        }

        itemsHost.Width = GetVisibleControls().Sum(GetChildWidth);
        EnsureHeaderPanel();
        UpdateHeaderStates();

        if (_itemCount == 0)
        {
            _appliedSelectedIndex = 0;
            ResetTransforms();
            return;
        }

        var normalizedSelection = WrapIndex(SelectedIndex);
        if (!_hasAppliedSelection)
        {
            ApplySelectedIndexImmediately(normalizedSelection);
            _hasAppliedSelection = true;
        }
        else
        {
            _appliedSelectedIndex = normalizedSelection;
            ResetTransforms();
        }
    }

    private void ApplySelectedIndexIfReady(int requestedIndex)
    {
        var itemCount = GetCurrentItemCount();
        if (itemCount == 0 || _itemsHost is null || _isAnimating)
        {
            return;
        }

        ApplySelectedIndexImmediately(WrapIndex(requestedIndex, itemCount));
    }

    private void ApplySelectedIndexImmediately(int targetIndex)
    {
        var itemCount = GetCurrentItemCount();
        if (itemCount == 0 || _itemsHost is null)
        {
            return;
        }

        targetIndex = WrapIndex(targetIndex, itemCount);
        var appliedSelectedIndex = WrapIndex(_appliedSelectedIndex, itemCount);
        var forwardDistance = (targetIndex - appliedSelectedIndex + itemCount) % itemCount;
        var backwardDistance = (appliedSelectedIndex - targetIndex + itemCount) % itemCount;

        ExecuteWithoutTransitions(() =>
        {
            if (forwardDistance <= backwardDistance)
            {
                for (var index = 0; index < forwardDistance; index++)
                {
                    var currentCount = GetCurrentItemCount();
                    if (_itemsHost is null || currentCount <= 1)
                    {
                        break;
                    }

                    MoveFirstVisibleToEnd();
                    MoveHeaderFirstToEnd();
                }
            }
            else
            {
                for (var index = 0; index < backwardDistance; index++)
                {
                    var currentCount = GetCurrentItemCount();
                    if (_itemsHost is null || currentCount <= 1)
                    {
                        break;
                    }

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
        if (_viewport is null ||
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

        if (deltaX > 0 && !_isLoopPreparedForPrevious)
        {
            PreparePreviousLoop(contentStep);
        }
        else if (deltaX <= 0 && _isLoopPreparedForPrevious)
        {
            RestoreNaturalOrder();
        }

        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = _baseItemsX + deltaX;
        }

        if (_headerTranslate is not null)
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
        var itemCount = GetCurrentItemCount();
        if (_isAnimating || itemCount == 0 || _itemsTranslate is null)
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

        ExecuteWithoutTransitions(() =>
        {
            var currentCount = GetCurrentItemCount();
            if (_itemsHost is not null && currentCount > 1)
            {
                MoveFirstVisibleToEnd();
            }

            _appliedSelectedIndex = WrapIndex(_appliedSelectedIndex + 1, Math.Max(currentCount, 1));
            MoveHeaderFirstToEnd();
            UpdateHeaderStates();
            ResetTransforms();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
    }

    private async Task MovePreviousAsync(bool previewPrepared)
    {
        if (_isAnimating || GetCurrentItemCount() == 0 || _itemsTranslate is null)
        {
            return;
        }

        _isAnimating = true;
        if (!previewPrepared)
        {
            PreparePreviousLoop(GetContentStepWidth());
        }

        _itemsTranslate.X = 0;
        if (_headerTranslate is not null)
        {
            _headerTranslate.X = 0;
        }

        await Task.Delay(SlideDuration);

        ExecuteWithoutTransitions(() =>
        {
            _appliedSelectedIndex = WrapIndex(_appliedSelectedIndex - 1, Math.Max(GetCurrentItemCount(), 1));
            _isLoopPreparedForPrevious = false;
            UpdateHeaderStates();
            ResetTransforms();
        });

        SetSelectedIndexCore(_appliedSelectedIndex);
        _isAnimating = false;
    }

    private async Task CancelPreviousPreviewAsync()
    {
        if (_isAnimating || GetCurrentItemCount() == 0 || _itemsTranslate is null)
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
        _itemsTranslate.X = 0;
        if (_headerTranslate is not null)
        {
            _headerTranslate.X = 0;
        }

        await Task.Delay(SlideDuration);

        _baseItemsX = 0;
        _baseHeaderX = 0;
        _isAnimating = false;
    }

    private void PreparePreviousLoop(double contentStep)
    {
        var itemCount = GetCurrentItemCount();
        if (_itemsHost is null || _itemsTranslate is null || itemCount == 0)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            if (itemCount > 1)
            {
                MoveLastVisibleToStart();
            }

            MoveHeaderLastToStart();
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
        var itemCount = GetCurrentItemCount();
        if (_itemsHost is null || itemCount == 0)
        {
            return;
        }

        ExecuteWithoutTransitions(() =>
        {
            if (itemCount > 1)
            {
                MoveFirstVisibleToEnd();
            }

            MoveHeaderFirstToEnd();
            UpdateHeaderStates();
            ResetTransforms();
        });

        _isLoopPreparedForPrevious = false;
    }

    private void ResetTransforms()
    {
        if (_itemsTranslate is not null)
        {
            _itemsTranslate.X = 0;
        }

        if (_headerTranslate is not null)
        {
            _headerTranslate.X = 0;
        }

        _baseItemsX = 0;
        _baseHeaderX = 0;
    }

    private void PreparePivotItem(PivotItem pivotItem)
    {
        ApplyItemWidth(pivotItem);
        if (pivotItem.IsGeneratedByPivot)
        {
            pivotItem.ContentTemplate = ItemTemplate;
            pivotItem.HeaderTemplate = ItemTitleTemplate;
            pivotItem.Header = ResolveItemTitle(pivotItem.Content) ?? pivotItem.Content;
        }
        else if (pivotItem.HeaderTemplate is null)
        {
            pivotItem.HeaderTemplate = ItemTitleTemplate;
        }
    }

    private void ApplyItemWidth(PivotItem pivotItem)
    {
        var viewportWidth = GetViewportWidth();
        if (viewportWidth <= 0)
        {
            return;
        }

        pivotItem.Width = viewportWidth * Math.Max(0, ItemWidthFactor);
    }

    private void RefreshPreparedContainers()
    {
        if (_itemsHost is null)
        {
            return;
        }

        foreach (var pivotItem in _itemsHost.Children.OfType<PivotItem>())
        {
            PreparePivotItem(pivotItem);
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
        var visibleItems = GetVisibleControls().OfType<PivotItem>().ToArray();
        if (_headerPanel.Children.Count != visibleItems.Length)
        {
            _headerPanel.Children.Clear();
            foreach (var pivotItem in visibleItems)
            {
                _headerPanel.Children.Add(CreateHeaderControl(pivotItem));
            }
            return;
        }

        var childIndex = 0;
        foreach (var pivotItem in visibleItems)
        {
            if (_headerPanel.Children[childIndex] is not ContentControl headerControl)
            {
                childIndex++;
                continue;
            }

            headerControl.Content = CreateHeaderContent(pivotItem.Header);
            headerControl.ContentTemplate = GetHeaderContentTemplate(pivotItem);
            childIndex++;
        }
    }

    private ContentControl CreateHeaderControl(PivotItem pivotItem)
    {
        var headerControl = new ContentControl
        {
            Content = CreateHeaderContent(pivotItem.Header),
            ContentTemplate = GetHeaderContentTemplate(pivotItem),
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

    private IDataTemplate? GetHeaderContentTemplate(PivotItem pivotItem)
    {
        return pivotItem.Header is Control ? null : pivotItem.HeaderTemplate ?? ItemTitleTemplate;
    }

    private async void HeaderControlOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || _headerPanel is null || _isAnimating || _itemCount == 0)
        {
            return;
        }

        var visualIndex = _headerPanel.Children.IndexOf(control);
        if (visualIndex <= 0)
        {
            return;
        }

        e.Handled = true;
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

        var targetIndex = WrapIndex(_appliedSelectedIndex + visualIndex);
        ApplySelectedIndexImmediately(targetIndex);
    }

    private void UpdateHeaderStates()
    {
        if (_headerPanel is null)
        {
            return;
        }

        for (var index = 0; index < _headerPanel.Children.Count; index++)
        {
            if (_headerPanel.Children[index] is not Control control)
            {
                continue;
            }

            control.Opacity = index == 0 ? 1d : 0.48d;
        }
    }

    private void MoveHeaderFirstToEnd()
    {
        if (_headerPanel is null || _headerPanel.Children.Count == 0)
        {
            return;
        }

        _headerPanel.Children.Move(0, _headerPanel.Children.Count - 1);
    }

    private void MoveHeaderLastToStart()
    {
        if (_headerPanel is null || _headerPanel.Children.Count == 0)
        {
            return;
        }

        _headerPanel.Children.Move(_headerPanel.Children.Count - 1, 0);
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
            stackPanel.Spacing = 0;
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
        return firstControl is null ? 0 : GetChildWidth(firstControl);
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
        return WrapIndex(index, _itemCount);
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
