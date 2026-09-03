using PolarBLE.Core.Streaming;

namespace PolarBLE.Core.Polar;

/// <summary>
/// The measurement configuration this application requests from a Polar H10, and the stream
/// definitions that describe the resulting data.
/// </summary>
/// <remarks>
/// The byte sequences produced here are deliberately identical to the hand-written arrays in
/// the original implementation, so the sensor is asked for exactly what it was asked for
/// before. What changed is that the values are now named rather than described by comments
/// that did not match the bytes.
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

    /// <summary>
    /// Accelerometer range in g. Note that the original inline comments offered 2 or 4 as the
    /// options in play while the bytes actually requested 8; 8 is what the sensor was really
    /// being configured for, and that behaviour is preserved here.
    /// </summary>
    public const ushort AccRangeG = 8;

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
            ["resolution_bits"] = EcgResolutionBits.ToString(),
            ["sample_rate_hz"] = EcgSampleRateHz.ToString(),
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
            ["resolution_bits"] = AccResolutionBits.ToString(),
            ["sample_rate_hz"] = AccSampleRateHz.ToString(),
            ["range_g"] = AccRangeG.ToString(),
        },
    };
}
