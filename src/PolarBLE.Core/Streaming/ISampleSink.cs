namespace PolarBLE.Core.Streaming;

/// <summary>
/// Destination for decoded samples.
/// </summary>
/// <remarks>
/// The abstraction keeps the acquisition path free of liblsl, so that it can be exercised in
/// tests without opening a network outlet.
/// </remarks>
public interface ISampleSink : IDisposable
{
    /// <summary>Number of channels each sample carries.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// Pushes one frame of samples.
    /// </summary>
    /// <remarks>
    /// No timestamp is supplied. An LSL-backed sink lets liblsl stamp the chunk with the time
    /// it is pushed and derive the earlier samples from the stream's nominal sample rate.
    /// </remarks>
    /// <param name="interleaved">
    /// Sample values, channel-interleaved: length must be at least
    /// <paramref name="sampleCount"/> times <see cref="ChannelCount"/>.
    /// </param>
    /// <param name="sampleCount">Number of samples in this frame.</param>
    void PushChunk(ReadOnlySpan<float> interleaved, int sampleCount);
}
