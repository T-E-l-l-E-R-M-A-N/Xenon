namespace Xenon.Core.Services;

public interface IKeyboardInsetService
{
    event Action<double>? BottomInsetChanged;

    double BottomInset { get; }
}
