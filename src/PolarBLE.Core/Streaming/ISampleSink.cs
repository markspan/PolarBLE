namespace PolarBLE.Core.Streaming;

/// <summary>
/// Destination for decoded samples.
/// </summary>
/// <remarks>
/// The abstraction exists so that the acquisition path can be exercised in tests without
/// opening a network outlet, and so that the decoding logic never depends on liblsl.
/// </remarks>
public interface ISampleSink : IDisposable
{
    /// <summary>Number of channels each sample carries.</summary>
    int ChannelCount { get; }

    /// <summary>Total samples pushed since the sink was created.</summary>
    long SamplesPushed { get; }

    /// <summary>
    /// Pushes one frame of samples.
    /// </summary>
    /// <remarks>
    /// No timestamp is supplied: liblsl stamps the chunk with the current time when it is
    /// pushed and derives every earlier sample's timestamp from the stream's own declared
    /// sample rate, the same way it always has for a single-sample push. This is liblsl's own
    /// built-in behaviour, not something this application computes.
    /// </remarks>
    /// <param name="interleaved">
    /// Sample values, channel-interleaved: length must be
    /// <paramref name="sampleCount"/> times <see cref="ChannelCount"/>.
    /// </param>
    /// <param name="sampleCount">Number of samples in this frame.</param>
    void PushChunk(ReadOnlySpan<float> interleaved, int sampleCount);
}
