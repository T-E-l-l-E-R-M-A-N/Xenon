namespace Xenon.Core.Services;

public class KeyboardInsetService : IKeyboardInsetService
{
    private double _bottomInset;

    public event Action<double>? BottomInsetChanged;

    public double BottomInset => _bottomInset;

    protected void SetBottomInset(double value)
    {
        if (Math.Abs(_bottomInset - value) < 0.5d)
        {
            return;
        }

        _bottomInset = Math.Max(0d, value);
        BottomInsetChanged?.Invoke(_bottomInset);
    }
}
