using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace OpenSense.Core.Monitoring;

/// <summary>A chip's power draw, in watts.</summary>
public interface IPowerSensor : IDisposable
{
    /// <summary>
    /// Watts averaged since the previous call, or null when there is no reading yet (the first call) or the chip didn't
    /// answer. Called from the control thread, about once per sample.
    /// </summary>
    double? ReadWatts();
}

/// <summary>What the discrete GPU draws, and the most it is allowed to, in watts.</summary>
public readonly record struct GpuPowerReading(double? Watts, double? Limit);

/// <summary>The discrete GPU's power draw. Read only while the GPU is awake, as the temperature is.</summary>
public interface IGpuPowerMeter
{
    GpuPowerReading Read();
}

/// <summary>
/// Intel CPU package power from the RAPL energy counter, read through PawnIO like the temperature: the same register
/// HWiNFO and ThrottleStop show as "CPU Package Power". The counter only counts up (in 1/2^n joule steps, wrapping at
/// 32 bits), so power is the energy used since the previous reading divided by the time between them.
/// </summary>
internal sealed class IntelRaplSensor : IPowerSensor
{
    private const ulong RaplPowerUnit = 0x606;       // MSR_RAPL_POWER_UNIT; bits 12:8 = energy unit, 1 / 2^n joules
    private const ulong PackageEnergyStatus = 0x611; // MSR_PKG_ENERGY_STATUS; 32-bit running total of the energy used

    /// <summary>Shorter windows give noisy power; the previous reading is repeated instead.</summary>
    private const double MinimumSeconds = 0.2;

    /// <summary>No laptop package draws this; such a reading is a bus glitch.</summary>
    private const double ImplausibleWatts = 500;

    private readonly PawnIOModule _module;
    private readonly double _joulesPerCount;
    private uint _lastCounter;
    private long _lastTimestamp;
    private bool _haveBaseline;
    private double? _lastWatts;

    private IntelRaplSensor(PawnIOModule module, double joulesPerCount)
    {
        _module = module;
        _joulesPerCount = joulesPerCount;
    }

    /// <summary>The sensor, or null on a CPU that isn't Intel's, without PawnIO, or where the driver refuses the counter.</summary>
    public static IPowerSensor? TryOpen()
    {
        if (!X86Base.IsSupported || CpuTemperatureSensor.Vendor() != "GenuineIntel")
            return null;
        if (PawnIOModule.TryOpen("IntelMSR", null) is not { } module)
            return null;

        var energyUnit = module.Call("ioctl_read_msr", RaplPowerUnit) is { } unit ? (int)((unit >> 8) & 0x1F) : 0;
        if (energyUnit is < 1 or > 30 || module.Call("ioctl_read_msr", PackageEnergyStatus) is null)
        {
            module.Dispose();
            return null;
        }
        return new IntelRaplSensor(module, 1.0 / (1L << energyUnit));
    }

    public double? ReadWatts()
    {
        if (_module.Call("ioctl_read_msr", PackageEnergyStatus) is not { } raw)
            return null;

        var counter = (uint)raw;
        var now = Stopwatch.GetTimestamp();
        if (!_haveBaseline)
        {
            _lastCounter = counter;
            _lastTimestamp = now;
            _haveBaseline = true;
            return null;
        }

        var seconds = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        if (seconds < MinimumSeconds)
            return _lastWatts;

        var joules = unchecked(counter - _lastCounter) * _joulesPerCount; // unchecked: the counter wraps around
        _lastCounter = counter;
        _lastTimestamp = now;
        var watts = joules / seconds;
        _lastWatts = watts is >= 0 and < ImplausibleWatts ? watts : null;
        return _lastWatts;
    }

    public void Dispose() => _module.Dispose();
}

/// <summary>
/// The NVIDIA GPU's board power from the driver (NVML, as nvidia-smi's "Power Draw"), and the limit it enforces. Like
/// the temperature, call <see cref="Read"/> only while the GPU is already awake: asking can power a sleeping Optimus
/// GPU back up.
/// </summary>
internal sealed unsafe class NvmlGpuPower : IGpuPowerMeter
{
    private readonly NvmlLibrary _nvml;
    private readonly delegate* unmanaged<nint, uint*, int> _usage;         // nvmlDeviceGetPowerUsage, milliwatts
    private readonly delegate* unmanaged<nint, uint*, int> _enforcedLimit; // nvmlDeviceGetEnforcedPowerLimit, milliwatts

    private NvmlGpuPower(NvmlLibrary nvml, nint usage)
    {
        _nvml = nvml;
        _usage = (delegate* unmanaged<nint, uint*, int>)usage;
        _enforcedLimit = (delegate* unmanaged<nint, uint*, int>)nvml.Export("nvmlDeviceGetEnforcedPowerLimit");
    }

    /// <summary>The meter, on a driver that has it; the library stays the caller's to dispose.</summary>
    public static NvmlGpuPower? TryOpen(NvmlLibrary nvml) =>
        nvml.Export("nvmlDeviceGetPowerUsage") is not 0 and var usage ? new NvmlGpuPower(nvml, usage) : null;

    public GpuPowerReading Read()
    {
        if (!_nvml.TryGetDevice(out var device))
            return default;

        uint milliwatts;
        double? watts = _usage(device, &milliwatts) == NvmlLibrary.Success ? milliwatts / 1000.0 : null;

        double? limit = null;
        if (_enforcedLimit != null)
        {
            uint limitMilliwatts;
            if (_enforcedLimit(device, &limitMilliwatts) == NvmlLibrary.Success && limitMilliwatts > 0)
                limit = limitMilliwatts / 1000.0;
        }
        return new GpuPowerReading(watts, limit);
    }
}
