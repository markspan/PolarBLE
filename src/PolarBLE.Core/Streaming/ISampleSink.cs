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
    /// <param name="interleaved">
    /// Sample values, channel-interleaved: length must be
    /// <paramref name="sampleCount"/> times <see cref="ChannelCount"/>.
    /// </param>
    /// <param name="timestamps">One timestamp per sample.</param>
    /// <param name="sampleCount">Number of samples in this frame.</param>
    void PushChunk(ReadOnlySpan<float> interleaved, ReadOnlySpan<double> timestamps, int sampleCount);
}
