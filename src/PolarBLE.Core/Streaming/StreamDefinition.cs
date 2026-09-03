namespace PolarBLE.Core.Streaming;

/// <summary>
/// One channel of an outgoing stream.
/// </summary>
/// <param name="Label">Channel name, e.g. "ECG" or "X".</param>
/// <param name="Unit">Physical unit, e.g. "microvolts" or "milli-g".</param>
/// <param name="Type">Channel type, e.g. "ECG" or "AccelerometerX".</param>
public sealed record ChannelDefinition(string Label, string Unit, string Type);

/// <summary>
/// Everything needed to advertise a stream on the network, including the metadata a
/// downstream analyst needs in order to interpret the numbers years later.
/// </summary>
public sealed record StreamDefinition
{
    /// <summary>Stream name as it appears to consumers.</summary>
    public required string Name { get; init; }

    /// <summary>Content type, following the XDF vocabulary where one exists.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Stable identifier for this data source. It must survive an application restart so that
    /// a recorder can re-bind to the same physical strap after a dropout, and it must differ
    /// between two straps recorded simultaneously.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>Nominal sample rate in hertz.</summary>
    public required double SampleRate { get; init; }

    /// <summary>The channels, in the order they are interleaved in a pushed chunk.</summary>
    public required IReadOnlyList<ChannelDefinition> Channels { get; init; }

    /// <summary>Device manufacturer, recorded in the acquisition metadata block.</summary>
    public string Manufacturer { get; init; } = "Polar";

    /// <summary>Device model, recorded in the acquisition metadata block.</summary>
    public string Model { get; init; } = "H10";

    /// <summary>Bluetooth address of the strap, recorded in the acquisition metadata block.</summary>
    public string DeviceAddress { get; init; } = string.Empty;

    /// <summary>
    /// Additional acquisition metadata, such as the exact PMD settings the sensor was asked
    /// for. Recorded so that a recording can be interpreted without consulting the source.
    /// </summary>
    public IReadOnlyDictionary<string, string> Acquisition { get; init; } =
        new Dictionary<string, string>();
}
