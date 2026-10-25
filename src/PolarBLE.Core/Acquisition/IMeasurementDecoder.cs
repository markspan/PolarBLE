using PolarBLE.Core.Polar;

namespace PolarBLE.Core.Acquisition;

/// <summary>
/// Turns the payload of one PMD data frame into channel-interleaved samples.
/// </summary>
/// <remarks>
/// Implementations are stateless: every frame carries everything needed to decode it, so a
/// single instance can serve any number of streams.
/// </remarks>
public interface IMeasurementDecoder
{
    /// <summary>The measurement whose frames this decoder understands.</summary>
    PmdMeasurementType MeasurementType { get; }

    /// <summary>Number of interleaved channels in each decoded sample.</summary>
    int ChannelCount { get; }

    /// <summary>Whether frames of the given type can be decoded.</summary>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <returns>True if <see cref="SampleCount"/> and <see cref="Decode"/> accept it.</returns>
    bool IsSupported(byte frameType);

    /// <summary>Counts the samples in a payload without decoding them.</summary>
    /// <param name="frameType">A frame type for which <see cref="IsSupported"/> is true.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <returns>The number of samples the payload contains.</returns>
    int SampleCount(byte frameType, ReadOnlySpan<byte> payload);

    /// <summary>Decodes a payload into channel-interleaved values.</summary>
    /// <param name="frameType">A frame type for which <see cref="IsSupported"/> is true.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <param name="destination">
    /// Receives the values; at least <see cref="SampleCount"/> times <see cref="ChannelCount"/>
    /// long.
    /// </param>
    /// <returns>The number of samples (not values) written.</returns>
    int Decode(byte frameType, ReadOnlySpan<byte> payload, Span<float> destination);
}
