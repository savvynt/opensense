namespace OpenSense.Core.Control;

/// <summary>
/// Turns a fan's curve-step temperatures into a steady boost. The temperature the curve is read at never lags a rise,
/// so the fans speed up at once, but it only comes down once the temperature is more than <see cref="Hysteresis"/> °C
/// under it, and then to that far above the temperature: it always stays within <see cref="Hysteresis"/> °C above the
/// real one, never below. A temperature that wobbles by less than that, as the CPU's does under a changing load, holds
/// the boost where it is instead of nudging the fans between the firmware's speed steps every few seconds.
/// </summary>
public sealed class CurveFollower
{
    /// <summary>How far (°C) the temperature has to fall before the boost follows it down.</summary>
    public const double Hysteresis = 2.0;

    private double? _held;

    public int? Current { get; private set; }

    public void Reset()
    {
        _held = null;
        Current = null;
    }

    public int Update(double temperature, FanCurve curve)
    {
        // Up: the temperature itself. Down: only once it is more than Hysteresis under what is held, and then that far above it.
        var held = _held is { } previous ? Math.Max(temperature, Math.Min(previous, temperature + Hysteresis)) : temperature;
        _held = held;

        Current = Math.Clamp(curve.Evaluate(held), 0, 100);
        return Current.Value;
    }
}
