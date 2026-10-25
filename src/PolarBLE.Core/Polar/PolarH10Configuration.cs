using System.Collections.Immutable;
using System.Globalization;
using PolarBLE.Core.Acquisition;
using PolarBLE.Core.Streaming;

namespace PolarBLE.Core.Polar;

/// <summary>
/// The measurement configuration this application requests from a Polar H10, and the stream
/// definitions that describe the resulting data.
/// </summary>
/// <remarks>
/// The byte sequences produced here are pinned by tests to the requests known to work against
/// real hardware, so the strap is always asked for exactly the same thing.
/// </remarks>
public static class PolarH10Configuration
{
    /// <summary>ECG sample rate in hertz.</summary>
    public const ushort EcgSampleRateHz = 130;

    /// <summary>ECG resolution in bits.</summary>
    public const ushort EcgResolutionBits = 14;

    /// <summary>Accelerometer sample rate in hertz.</summary>
    public const ushort AccSampleRateHz = 200;

    /// <summary>Accelerometer resolution in bits.</summary>
    public const ushort AccResolutionBits = 16;

    /// <summary>Accelerometer range in g.</summary>
    public const ushort AccRangeG = 8;

    /// <summary>
    /// Every measurement this application acquires from a strap, in the order they are started.
    /// </summary>
    /// <param name="deviceName">The strap's advertised name.</param>
    /// <param name="deviceAddress">The strap's Bluetooth address, formatted as hex.</param>
    /// <returns>The ECG and accelerometer configurations.</returns>
    public static IReadOnlyList<MeasurementConfiguration> CreateMeasurements(
        string deviceName,
        string deviceAddress) =>
    [
        new MeasurementConfiguration
        {
            Label = "ECG",
            DisplayName = "ECG",
            StartCommand = [.. BuildEcgStart()],
            Decoder = MeasurementDecoders.Ecg,
            Stream = EcgStream(deviceName, deviceAddress),
        },
        new MeasurementConfiguration
        {
            Label = "ACC",
            DisplayName = "accelerometer",
            StartCommand = [.. BuildAccStart()],
            Decoder = MeasurementDecoders.Acc,
            Stream = AccStream(deviceName, deviceAddress),
        },
    ];

    /// <summary>Builds the ECG start-measurement request.</summary>
    /// <returns>The control-point bytes.</returns>
    public static byte[] BuildEcgStart() => PmdControlPoint.BuildStart(
        PmdMeasurementType.Ecg,
        new PmdSetting(PmdSettingType.SampleRate, EcgSampleRateHz),
        new PmdSetting(PmdSettingType.Resolution, EcgResolutionBits));

    /// <summary>Builds the accelerometer start-measurement request.</summary>
    /// <returns>The control-point bytes.</returns>
    public static byte[] BuildAccStart() => PmdControlPoint.BuildStart(
        PmdMeasurementType.Acc,
        new PmdSetting(PmdSettingType.SampleRate, AccSampleRateHz),
        new PmdSetting(PmdSettingType.Resolution, AccResolutionBits),
        new PmdSetting(PmdSettingType.Range, AccRangeG));

    /// <summary>
    /// Describes the ECG stream.
    /// </summary>
    /// <param name="deviceName">The strap's advertised name.</param>
    /// <param name="deviceAddress">The strap's Bluetooth address, formatted as hex.</param>
    /// <returns>The stream definition.</returns>
    public static StreamDefinition EcgStream(string deviceName, string deviceAddress) => new()
    {
        Name = $"{deviceName}_ECG",
        Type = "ECG",
        SourceId = $"PolarBLE:{deviceAddress}:ECG",
        SampleRate = EcgSampleRateHz,
        Channels = [new ChannelDefinition("ECG", "microvolts", "ECG")],
        DeviceAddress = deviceAddress,
        Acquisition = new Dictionary<string, string>
        {
            ["resolution_bits"] = Format(EcgResolutionBits),
            ["sample_rate_hz"] = Format(EcgSampleRateHz),
        },
    };

    /// <summary>
    /// Describes the accelerometer stream.
    /// </summary>
    /// <param name="deviceName">The strap's advertised name.</param>
    /// <param name="deviceAddress">The strap's Bluetooth address, formatted as hex.</param>
    /// <returns>The stream definition.</returns>
    public static StreamDefinition AccStream(string deviceName, string deviceAddress) => new()
    {
        Name = $"{deviceName}_ACC",
        Type = "Accelerometer",
        SourceId = $"PolarBLE:{deviceAddress}:ACC",
        SampleRate = AccSampleRateHz,
        Channels =
        [
            new ChannelDefinition("X", "milli-g", "AccelerometerX"),
            new ChannelDefinition("Y", "milli-g", "AccelerometerY"),
            new ChannelDefinition("Z", "milli-g", "AccelerometerZ"),
        ],
        DeviceAddress = deviceAddress,
        Acquisition = new Dictionary<string, string>
        {
            ["resolution_bits"] = Format(AccResolutionBits),
            ["sample_rate_hz"] = Format(AccSampleRateHz),
            ["range_g"] = Format(AccRangeG),
        },
    };

    // Metadata is read by machines, so it must not vary with the user's locale.
    private static string Format(ushort value) => value.ToString(CultureInfo.InvariantCulture);
}
