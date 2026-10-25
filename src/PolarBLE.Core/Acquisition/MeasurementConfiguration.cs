using System.Collections.Immutable;
using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;

namespace PolarBLE.Core.Acquisition;

/// <summary>
/// Everything needed to acquire one measurement: how to start and stop it on the strap, how
/// to decode its frames, and how to advertise the resulting stream.
/// </summary>
/// <remarks>
/// Describing measurements as data lets the session start, route and stop any number of them
/// through one code path instead of repeating the same steps per measurement.
/// </remarks>
public sealed class MeasurementConfiguration
{
    /// <summary>Short name shown in telemetry, e.g. "ECG" or "ACC".</summary>
    public required string Label { get; init; }

    /// <summary>Name used in sentences, e.g. "ECG" or "accelerometer".</summary>
    public required string DisplayName { get; init; }

    /// <summary>The start-measurement request to write to the control point.</summary>
    public required ImmutableArray<byte> StartCommand { get; init; }

    /// <summary>Decodes this measurement's frames.</summary>
    public required IMeasurementDecoder Decoder { get; init; }

    /// <summary>The stream that carries the decoded samples.</summary>
    public required StreamDefinition Stream { get; init; }

    /// <summary>The measurement being configured.</summary>
    public PmdMeasurementType MeasurementType => Decoder.MeasurementType;

    /// <summary>Builds the stop-measurement request for this measurement.</summary>
    /// <returns>The control-point bytes.</returns>
    public byte[] BuildStopCommand() => PmdControlPoint.BuildStop(MeasurementType);

    /// <summary>Creates the stream that acquires this measurement into <paramref name="sink"/>.</summary>
    /// <param name="sink">Receives the samples; owned by the returned stream.</param>
    /// <returns>The stream.</returns>
    public MeasurementStream CreateStream(ISampleSink sink) =>
        new(Label, Decoder, Stream.SampleRate, sink);
}
