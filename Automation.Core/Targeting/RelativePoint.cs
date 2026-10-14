namespace Automation.Core.Targeting;

public readonly record struct RelativePoint(double X, double Y)
{
    public RelativePoint Clamp() => new(Math.Clamp(X, 0d, 1d), Math.Clamp(Y, 0d, 1d));
}