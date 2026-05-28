using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Xenon.Controls;

public sealed class TurnstileFeatherTransition : IPageTransition
{
    private readonly Rotate3DTransition _pageTurn = new()
    {
        Duration = TimeSpan.FromMilliseconds(250),
        Orientation = PageSlide.SlideAxis.Horizontal,
        Depth = 900,
        SlideInEasing = new ExponentialEaseOut(),
        SlideOutEasing = new ExponentialEaseIn()
    };

    public async Task Start(
        Visual? from,
        Visual? to,
        bool forward,
        CancellationToken cancellationToken)
    {
        // 1. Общий 3D-поворот страницы
        var pageTask = _pageTurn.Start(from, to, forward, cancellationToken);

        // 2. Поверх него — feather/stagger для помеченных элементов
        var featherTasks = new List<Task>();

        if (from is Control fromControl)
            featherTasks.Add(AnimateFeather(fromControl, incoming: false, forward, cancellationToken));

        if (to is Control toControl)
            featherTasks.Add(AnimateFeather(toControl, incoming: true, forward, cancellationToken));

        await Task.WhenAll(featherTasks.Append(pageTask));
    }

    private static async Task AnimateFeather(
        Control root,
        bool incoming,
        bool forward,
        CancellationToken token)
    {
        var targets = root.GetVisualDescendants()
            .OfType<Control>()
            .Select(x => new { Control = x, Index = TurnstileFeather.GetIndex(x) })
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .ToList();

        var delay = forward
            ? incoming ? 40 : 50
            : incoming ? 50 : 40;

        var duration = incoming ? 350 : 250;

        var angle = forward
            ? incoming ? -80 : 50
            : incoming ? 50 : -80;

        await Task.WhenAll(targets.Select((x, i) =>
            AnimateElement(x.Control, incoming, angle, duration, delay * i, token)));
    }

    private static async Task AnimateElement(
        Control element,
        bool incoming,
        double angle,
        int durationMs,
        int delayMs,
        CancellationToken token)
    {
        var oldOpacity = element.Opacity;
        var oldTransform = element.RenderTransform;
        var oldOrigin = element.RenderTransformOrigin;

        var rotate = new RotateTransform();
        var scale = new ScaleTransform();
        var group = new TransformGroup
        {
            Children = { scale, rotate }
        };

        element.RenderTransform = group;
        element.RenderTransformOrigin = new RelativePoint(-0.2, 0.5, RelativeUnit.Relative);

        if (incoming)
            element.Opacity = 0;

        await Task.Delay(delayMs, token);

        var start = DateTime.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var t = (DateTime.UtcNow - start).TotalMilliseconds / durationMs;
            if (t >= 1)
                break;

            var eased = incoming ? EaseOutExpo(t) : EaseInExpo(t);
            var current = incoming
                ? Lerp(angle, 0, eased)
                : Lerp(0, angle, eased);

            // 2D feather поверх 3D page turn:
            scale.ScaleX = Math.Max(0.12, Math.Abs(Math.Cos(current * Math.PI / 180)));
            rotate.Angle = current * 0.04;

            element.Opacity = incoming ? oldOpacity : oldOpacity;

            await Task.Delay(16, token);
        }

        element.RenderTransform = oldTransform;
        element.RenderTransformOrigin = oldOrigin;
        element.Opacity = oldOpacity;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double EaseOutExpo(double t) =>
        t >= 1 ? 1 : 1 - Math.Pow(2, -6 * t);

    private static double EaseInExpo(double t) =>
        t <= 0 ? 0 : Math.Pow(2, 6 * (t - 1));
}