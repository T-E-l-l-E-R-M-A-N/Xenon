using Avalonia.Animation.Easings;

namespace Xenon.Controls;

internal readonly record struct MahAppsNumericKeyFrame(double Time, double Value, SplineEasing? Spline = null);

internal static class MahAppsAnimationHelpers
{
    public static double Evaluate(ReadOnlySpan<MahAppsNumericKeyFrame> keyFrames, double time)
    {
        if (keyFrames.Length == 0)
        {
            return 0d;
        }

        if (time <= keyFrames[0].Time)
        {
            return keyFrames[0].Value;
        }

        for (var index = 1; index < keyFrames.Length; index++)
        {
            var previous = keyFrames[index - 1];
            var current = keyFrames[index];
            if (time > current.Time)
            {
                continue;
            }

            var segmentDuration = current.Time - previous.Time;
            if (segmentDuration <= double.Epsilon)
            {
                return current.Value;
            }

            var progress = Math.Clamp((time - previous.Time) / segmentDuration, 0d, 1d);
            if (current.Spline is not null)
            {
                progress = current.Spline.Ease(progress);
            }

            return Lerp(previous.Value, current.Value, progress);
        }

        return keyFrames[^1].Value;
    }

    public static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    public static double Normalize(double time, double duration)
    {
        if (duration <= 0d)
        {
            return 0d;
        }

        var normalized = time % duration;
        return normalized < 0d ? normalized + duration : normalized;
    }
}
