using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Lighting;
using OpenSense.Core.Monitoring;
using OpenSense.Core.Settings;

namespace OpenSense.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenSense.Tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Machine_settings_round_trip_through_json()
    {
        var store = new SettingsStore<MachineSettings>(SettingsPath);
        var original = new MachineSettings
        {
            Profile = new ControlProfile
            {
                Mode = FanControlMode.Custom,
                Manual = new Dictionary<FanId, ManualFanSetting> { [FanId.Cpu] = new(40, UseCurve: true), [FanId.Gpu] = new(20) },
                Curve = FanCurve.From((35, 10), (70, 60), (90, 100)),
                AutoBoost = false,
                OperatingMode = OperatingMode.Performance,
                Safety = new SafetySettings { RestoreAutoOnExit = false },
            },
            Keyboard = new KeyboardSettings { WindowsKey = false },
            Lighting = new LightingConfig().With("Keyboard",
                new LightingSettings { Effect = LightingEffect.Wave, Speed = 7, Direction = LightingDirection.Up, EffectColor = "#00FF88" }),
            Overrides = new CapabilityOverrides { OperatingModes = true },
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(FanControlMode.Custom, loaded.Profile.Mode);
        Assert.Equal(new ManualFanSetting(40, UseCurve: true), loaded.Profile.ManualFor(FanId.Cpu));
        Assert.Equal(original.Profile.Curve, loaded.Profile.Curve);
        Assert.Null(loaded.Profile.Curves);
        Assert.False(loaded.Profile.AutoBoost);
        Assert.Equal(OperatingMode.Performance, loaded.Profile.OperatingMode);
        Assert.Equal(original.Profile.Safety, loaded.Profile.Safety);
        Assert.Equal(LightingEffect.Wave, loaded.Lighting.For("Keyboard")!.Effect);
        Assert.Equal(LightingDirection.Up, loaded.Lighting.For("Keyboard")!.Direction);
        Assert.Equal("#00FF88", loaded.Lighting.For("Keyboard")!.EffectColor);
        Assert.Null(loaded.Keyboard.LegacyLighting);
        Assert.Equal(MachineSettings.CurrentVersion, loaded.Version);
        Assert.False(loaded.Keyboard.WindowsKey);
        Assert.Null(loaded.Keyboard.LcdOverdrive);
        Assert.True(loaded.Overrides.OperatingModes);
    }

    private MachineSettings LoadUpgraded(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        return new SettingsStore<MachineSettings>(SettingsPath).Load().Upgrade();
    }

    [Fact]
    public void Per_fan_curves_become_the_one_of_the_first_fan_that_followed_a_curve()
    {
        var upgraded = LoadUpgraded("""
            { "Version": 3, "Profile": { "Mode": "Custom",
              "Manual": { "Cpu": { "Percent": 40, "UseCurve": false }, "Gpu": { "Percent": 20, "UseCurve": true } },
              "Curves": {
                "Cpu": { "Curve": { "Points": [ { "Temperature": 50, "Percent": 10 }, { "Temperature": 90, "Percent": 90 } ] } },
                "Gpu": { "Curve": { "Points": [ { "Temperature": 40, "Percent": 0 }, { "Temperature": 80, "Percent": 80 } ] } } } } }
            """);

        Assert.Equal(FanCurve.From((40, 0), (80, 80)), upgraded.Profile.Curve);
        Assert.Null(upgraded.Profile.Curves);
        Assert.Equal(MachineSettings.CurrentVersion, upgraded.Version);
    }

    [Fact]
    public void Standard_per_fan_curves_become_the_new_standard_and_a_set_one_is_kept()
    {
        var standard = LoadUpgraded("""
            { "Version": 3, "Profile": { "Curves": {
                "Cpu": { "Curve": { "Points": [ { "Temperature": 88, "Percent": 0 }, { "Temperature": 94, "Percent": 100 } ] } },
                "Gpu": { "Curve": { "Points": [ { "Temperature": 81, "Percent": 0 }, { "Temperature": 87, "Percent": 100 } ] } } } } }
            """);
        Assert.Equal(AntiThrottle.Shared(ThermalLimits.Default), standard.Profile.Curve);

        var edited = LoadUpgraded("""
            { "Version": 3, "Profile": { "Curves": {
                "Cpu": { "Curve": { "Points": [ { "Temperature": 88, "Percent": 0 }, { "Temperature": 94, "Percent": 100 } ] } },
                "Gpu": { "Curve": { "Points": [ { "Temperature": 45, "Percent": 10 }, { "Temperature": 85, "Percent": 100 } ] } } } } }
            """);
        Assert.Equal(FanCurve.From((45, 10), (85, 100)), edited.Profile.Curve);
    }

    [Fact]
    public void The_shared_standard_curve_is_never_under_either_chips_own()
    {
        var limits = ThermalLimits.Default;
        var shared = AntiThrottle.Shared(limits);
        for (var temperature = 40; temperature <= 100; temperature++)
        {
            Assert.True(shared.Evaluate(temperature) >= AntiThrottle.Cpu(limits.Cpu).Evaluate(temperature), $"CPU {temperature} °C");
            Assert.True(shared.Evaluate(temperature) >= AntiThrottle.Gpu(limits.Gpu).Evaluate(temperature), $"GPU {temperature} °C");
        }
    }

    [Fact]
    public void User_settings_round_trip_through_json()
    {
        var store = new SettingsStore<UserSettings>(SettingsPath);
        var original = new UserSettings { Ui = new UiSettings { UseFahrenheit = true, Theme = 2, CloseToTray = false } };

        store.Save(original);

        Assert.Equal(original.Ui, store.Load().Ui);
    }

    [Fact]
    public void Missing_members_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "Version": 1, "Profile": { "Mode": "Max" } }""");

        var loaded = new SettingsStore<MachineSettings>(SettingsPath).Load();

        Assert.Equal(FanControlMode.Max, loaded.Profile.Mode);
        Assert.True(loaded.Profile.AutoBoost);
        Assert.Equal(AntiThrottle.Shared(ThermalLimits.Default), loaded.Profile.Curve);
    }

    [Fact]
    public void Settings_from_a_two_fan_version_still_load_and_a_second_gpu_fan_round_trips()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            { "Version": 1, "Profile": { "Mode": "Custom",
              "Manual": { "Cpu": { "Percent": 40, "UseCurve": false }, "Gpu": { "Percent": 20, "UseCurve": true } } } }
            """);
        var store = new SettingsStore<MachineSettings>(SettingsPath);

        var loaded = store.Load();
        Assert.Equal(new ManualFanSetting(40), loaded.Profile.ManualFor(FanId.Cpu));
        Assert.Equal(new ManualFanSetting(20, UseCurve: true), loaded.Profile.ManualFor(FanId.Gpu));
        Assert.Equal(new ManualFanSetting(), loaded.Profile.ManualFor(FanId.Gpu2));

        store.Save(loaded with
        {
            Profile = loaded.Profile with
            {
                Manual = new Dictionary<FanId, ManualFanSetting>(loaded.Profile.Manual) { [FanId.Gpu2] = new(70) },
            },
        });
        Assert.Equal(new ManualFanSetting(70), store.Load().Profile.ManualFor(FanId.Gpu2));
        Assert.Contains("\"Gpu2\"", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Version_1_keyboard_lighting_moves_to_the_keyboard_light()
    {
        Directory.CreateDirectory(_directory);
        // As OpenSense 0.2 wrote it.
        File.WriteAllText(SettingsPath, """
            { "Version": 1,
              "Keyboard": { "Lighting": { "Effect": "Shifting", "Brightness": 75, "Speed": 3, "Direction": "Left", "EffectColor": "#FF8000",
                "Zones": [ { "On": true, "Color": "#102030" }, { "On": false, "Color": "#FFFFFF" } ] }, "WindowsKey": false } }
            """);
        var store = new SettingsStore<MachineSettings>(SettingsPath);

        var upgraded = store.Load().Upgrade();

        Assert.Equal(MachineSettings.CurrentVersion, upgraded.Version);
        Assert.Null(upgraded.Keyboard.LegacyLighting);
        Assert.False(upgraded.Keyboard.WindowsKey);
        var keyboard = upgraded.Lighting.For(EcKeyboardBackend.Id);
        Assert.NotNull(keyboard);
        Assert.Equal(LightingEffect.Shifting, keyboard.Effect);
        Assert.Equal(75, keyboard.Brightness);
        Assert.Equal(LightingDirection.Left, keyboard.Direction);
        Assert.Equal(new ZoneSetting(false, "#FFFFFF"), keyboard.Zone(1));

        store.Save(upgraded);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.False(saved.RootElement.GetProperty("Keyboard").TryGetProperty("Lighting", out _));
        Assert.Equal("Shifting", saved.RootElement.GetProperty("Lighting").GetProperty("Devices").GetProperty("Keyboard").GetProperty("Effect").GetString());
        Assert.Equal(LightingEffect.Shifting, store.Load().Upgrade().Lighting.For(EcKeyboardBackend.Id)!.Effect);
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ not json");

        var loaded = new SettingsStore<MachineSettings>(SettingsPath).Load();

        Assert.Equal(FanControlMode.Auto, loaded.Profile.Mode);
        Assert.True(File.Exists(SettingsPath + ".bad"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// Settings files written by released versions (<c>Fixtures/Settings/&lt;version&gt;</c>, written by that version with every
/// member changed from its default) load in this one without losing a value. A release that changes the settings adds
/// its own folder.
/// </summary>
public sealed class ReleasedSettingsTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(SourceTree.Tests, "Fixtures", "Settings");

    /// <summary>Where today's settings keep what a release kept elsewhere: (release, path then, path now).</summary>
    private static readonly (string Release, string Then, string Now)[] Moved =
    [
        ("0.2.0", "Keyboard.Lighting", "Lighting.Devices.Keyboard"), // settings version 2 keeps lighting per light
    ];

    /// <summary>What today's settings no longer keep, on purpose.</summary>
    private static readonly string[] Dropped =
    [
        "Overrides.CoolBoost", "Overrides.GpuModeSwitch", // overrides only settings.json could set, removed
        "Profile.Curves", // a curve per fan: settings version 4 keeps the one for all fans (Profile.Curve)
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenSense.Tests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string> Files => new(Directory.GetDirectories(Fixtures)
        .SelectMany(release => Directory.GetFiles(release).Select(file => (Path.GetFileName(release), Path.GetFileName(file)))));

    [Theory, MemberData(nameof(Files))]
    public void A_file_a_release_wrote_loads_with_every_value(string release, string file)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, file);
        File.Copy(Path.Combine(Fixtures, release, file), path);

        object loaded = file switch
        {
            "settings.json" => new SettingsStore<MachineSettings>(path).Load().Upgrade(),
            "user.json" => new SettingsStore<UserSettings>(path).Load(),
            "state.json" => new SettingsStore<RuntimeState>(path).Load(),
            _ => throw new InvalidDataException($"{file} is no settings file"),
        };

        Assert.False(File.Exists(path + ".bad"), "The file was taken for a corrupt one.");
        var now = Values(JsonSerializer.SerializeToNode(loaded, loaded.GetType(), SettingsJson.Options));
        var lost = new List<string>();
        foreach (var (at, value) in Values(JsonNode.Parse(File.ReadAllText(path))).Where(v => v.Key != "Version" && !Dropped.Any(d => v.Key == d || v.Key.StartsWith(d + ".", StringComparison.Ordinal)))) // Upgrade raises Version
        {
            var place = Moved.Where(m => m.Release == release && at.StartsWith(m.Then + ".", StringComparison.Ordinal))
                .Select(m => m.Now + at[m.Then.Length..]).FirstOrDefault() ?? at;
            if (now.GetValueOrDefault(place) != value)
                lost.Add($"{at} = {value}, read as {now.GetValueOrDefault(place) ?? "nothing"}");
        }
        Assert.Empty(lost);
    }

    /// <summary>Every value in <paramref name="node"/> by its path (list items by their index).</summary>
    private static Dictionary<string, string> Values(JsonNode? node, string path = "", Dictionary<string, string>? values = null)
    {
        values ??= new(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject members:
                foreach (var (name, child) in members)
                    Values(child, path.Length == 0 ? name : $"{path}.{name}", values);
                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                    Values(items[i], $"{path}.{i.ToString(CultureInfo.InvariantCulture)}", values);
                break;
            default:
                values[path] = node?.ToJsonString() ?? "null";
                break;
        }
        return values;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
