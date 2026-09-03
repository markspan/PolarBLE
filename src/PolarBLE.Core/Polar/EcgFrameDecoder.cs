namespace PolarBLE.Core.Polar;

/// <summary>
/// Decodes the payload of a PMD ECG frame into microvolt samples.
/// </summary>
/// <remarks>
/// Samples are 3-byte little-endian two's-complement integers. The value is already in
/// microvolts; no scaling is applied.
/// </remarks>
public static class EcgFrameDecoder
{
    /// <summary>The only ECG frame type the H10 emits.</summary>
    public const byte UncompressedFrameType = 0x00;

    /// <summary>Bytes occupied by one ECG sample.</summary>
    public const int BytesPerSample = 3;

    /// <summary>
    /// Number of whole samples in a payload. Any trailing partial sample is excluded; the
    /// original implementation looped while offset was less than the payload length and so
    /// read one or two bytes past the end of a ragged payload.
    /// </summary>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <returns>The count of complete samples.</returns>
    public static int SampleCount(ReadOnlySpan<byte> payload) => payload.Length / BytesPerSample;

    /// <summary>
    /// Decodes every complete sample in <paramref name="payload"/>.
    /// </summary>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <param name="destination">Receives the decoded microvolt values.</param>
    /// <returns>The number of samples written.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small to hold the decoded samples.
    /// </exception>
    public static int Decode(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var count = SampleCount(payload);
        if (destination.Length < count)
        {
            throw new ArgumentException(
                "Destination is too small for the decoded samples.",
                nameof(destination));
        }

        for (var i = 0; i < count; i++)
        {
            var offset = i * BytesPerSample;
            var raw = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);

            // Sign-extend from 24 to 32 bits.
            if ((raw & 0x800000) != 0)
            {
                raw |= unchecked((int)0xFF000000);
            }

            destination[i] = raw;
        }

        return count;
    }
}
