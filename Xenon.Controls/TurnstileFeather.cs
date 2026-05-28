using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Xenon.Controls;

public static class TurnstileFeather
{
    public static readonly AttachedProperty<int> IndexProperty =
        AvaloniaProperty.RegisterAttached<Control, int>(
            "Index",
            typeof(TurnstileFeather),
            -1);

    public static readonly AttachedProperty<int> FeatheringIndexProperty =
        AvaloniaProperty.RegisterAttached<Control, int>(
            "FeatheringIndex",
            typeof(TurnstileFeather),
            -1);

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsEnabled",
            typeof(TurnstileFeather));

    public static int GetIndex(Control element)
    {
        var index = element.GetValue(IndexProperty);
        return index >= 0 ? index : element.GetValue(FeatheringIndexProperty);
    }

    public static void SetIndex(Control element, int value) => element.SetValue(IndexProperty, value);

    public static int GetFeatheringIndex(Control element) => element.GetValue(FeatheringIndexProperty);
    public static void SetFeatheringIndex(Control element, int value) => element.SetValue(FeatheringIndexProperty, value);

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static Task AnimateInAsync(Control root, bool forward, CancellationToken token)
    {
        var targets = GetFeatherTargets(root);
        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(targets.Select((target, order) =>
            AnimateElementInAsync(target.Control, forward, order, token)));
    }

    private static List<(Control Control, int Index)> GetFeatherTargets(Control root)
    {
        return root.GetVisualDescendants()
            .OfType<Control>()
            .Select(control => (Control: control, Index: GetIndex(control)))
            .Where(item => item.Index >= 0 && IsPermittedTarget(item.Control))
            .OrderBy(item => item.Index)
            .ToList();
    }

    private static bool IsPermittedTarget(Control control)
    {
        return control is not Panorama and not PanoramaItem;
    }

    private static async Task AnimateElementInAsync(Control element, bool forward, int order, CancellationToken token)
    {
        var oldOpacity = element.Opacity;
        var oldTransform = element.RenderTransform;
        var oldOrigin = element.RenderTransformOrigin;
        var delay = TimeSpan.FromMilliseconds(45 + order * 55);
        var duration = TimeSpan.FromMilliseconds(520);
        var easing = new CubicEaseOut();
        var startAngle = forward ? -82d : 82d;
        var rotate = new Rotate3DTransform
        {
            Depth = 900,
            AngleY = startAngle
        };

        element.RenderTransform = rotate;
        element.RenderTransformOrigin = new RelativePoint(-0.12, 0.5, RelativeUnit.Relative);
        element.Opacity = 0;

        try
        {
            await Task.Delay(delay, token);
            var start = DateTime.UtcNow;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var progress = (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds;
                if (progress >= 1d)
                {
                    break;
                }

                var eased = easing.Ease(Math.Clamp(progress, 0d, 1d));
                rotate.AngleY = Lerp(startAngle, 0d, eased);
                element.Opacity = Lerp(0d, oldOpacity, eased);
                await Task.Delay(16, token);
            }

            rotate.AngleY = 0;
            element.Opacity = oldOpacity;
        }
        finally
        {
            element.RenderTransform = oldTransform;
            element.RenderTransformOrigin = oldOrigin;
            element.Opacity = oldOpacity;
        }
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * progress;
    }
}
