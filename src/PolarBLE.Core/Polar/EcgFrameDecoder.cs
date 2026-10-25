using System.Numerics;

namespace PolarBLE.Core.Polar;

/// <summary>
/// Decodes the payload of a PMD ECG frame into microvolt samples.
/// </summary>
/// <remarks>
/// Samples are 3-byte little-endian two's-complement integers. The value is already in
/// microvolts; no scaling is applied. Every 24-bit value is exactly representable in a
/// <see cref="float"/>, so decoding straight into the LSL wire format loses nothing.
/// </remarks>
public static class EcgFrameDecoder
{
    /// <summary>The only ECG frame type the H10 emits.</summary>
    public const byte UncompressedFrameType = 0x00;

    /// <summary>Bytes occupied by one ECG sample.</summary>
    public const int BytesPerSample = 3;

    /// <summary>Number of channels in one ECG sample.</summary>
    public const int Channels = 1;

    /// <summary>Whether frames of the given type can be decoded.</summary>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <returns>True for <see cref="UncompressedFrameType"/>.</returns>
    public static bool IsSupported(byte frameType) => frameType == UncompressedFrameType;

    /// <summary>
    /// Number of whole samples in a payload. A trailing partial sample is excluded, so a
    /// ragged payload can never cause a read past its end.
    /// </summary>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <returns>The count of complete samples.</returns>
    public static int SampleCount(ReadOnlySpan<byte> payload) => payload.Length / BytesPerSample;

    /// <summary>
    /// Decodes every complete sample in <paramref name="payload"/>.
    /// </summary>
    /// <typeparam name="T">
    /// The numeric type to decode into, typically <see cref="int"/> for analysis or
    /// <see cref="float"/> for streaming.
    /// </typeparam>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <param name="destination">Receives the decoded microvolt values.</param>
    /// <returns>The number of samples written.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small to hold the decoded samples.
    /// </exception>
    public static int Decode<T>(ReadOnlySpan<byte> payload, Span<T> destination)
        where T : INumberBase<T>
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
            destination[i] = T.CreateTruncating(ReadInt24(payload.Slice(i * BytesPerSample, BytesPerSample)));
        }

        return count;
    }

    /// <summary>Reads a little-endian 24-bit two's-complement integer.</summary>
    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        var raw = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

        // Shift the sign bit into bit 31 and back to sign-extend from 24 to 32 bits.
        return (raw << 8) >> 8;
    }
}
