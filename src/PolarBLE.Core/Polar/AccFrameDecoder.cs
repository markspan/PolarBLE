using System.Buffers.Binary;

namespace PolarBLE.Core.Polar;

/// <summary>
/// Decodes the payload of a PMD accelerometer frame into milli-g samples.
/// </summary>
/// <remarks>
/// <para>
/// Two encodings exist and they are <em>not</em> distinguished by a resolution field. Frame
/// type 0x01 is uncompressed: six bytes per sample, three little-endian 16-bit axes. Frame
/// type 0x02 is delta-compressed: an uncompressed reference sample, then a series of blocks,
/// each introduced by a delta bit width and a sample count, whose bit-packed signed deltas
/// accumulate onto the running value.
/// </para>
/// <para>
/// The original implementation inferred the resolution as (frameType + 1) * 8, which is not a
/// protocol field at all. That happened to produce the correct 6-byte stride for frame type
/// 0x01 and silently produced noise for frame type 0x02.
/// </para>
/// </remarks>
public static class AccFrameDecoder
{
    /// <summary>Uncompressed frame: three 16-bit axes per sample.</summary>
    public const byte UncompressedFrameType = 0x01;

    /// <summary>Delta-compressed frame: a reference sample followed by packed deltas.</summary>
    public const byte DeltaFrameType = 0x02;

    /// <summary>Number of axes in one accelerometer sample.</summary>
    public const int Axes = 3;

    /// <summary>Bytes occupied by one uncompressed sample.</summary>
    public const int UncompressedBytesPerSample = Axes * sizeof(short);

    /// <summary>
    /// Counts the samples a payload will decode to, without decoding them.
    /// </summary>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <returns>The number of samples the payload contains.</returns>
    /// <exception cref="NotSupportedException"><paramref name="frameType"/> is unknown.</exception>
    public static int SampleCount(byte frameType, ReadOnlySpan<byte> payload) => frameType switch
    {
        UncompressedFrameType => payload.Length / UncompressedBytesPerSample,
        DeltaFrameType => CountDeltaSamples(payload),
        _ => throw new NotSupportedException("Unsupported accelerometer frame type."),
    };

    /// <summary>
    /// Decodes a payload into interleaved x, y, z milli-g values.
    /// </summary>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <param name="destination">
    /// Receives interleaved axes; its length must be at least three times the sample count.
    /// </param>
    /// <returns>The number of samples (not values) written.</returns>
    /// <exception cref="NotSupportedException"><paramref name="frameType"/> is unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static int Decode(byte frameType, ReadOnlySpan<byte> payload, Span<short> destination)
    {
        var count = SampleCount(frameType, payload);
        if (destination.Length < count * Axes)
        {
            throw new ArgumentException(
                "Destination is too small for the decoded samples.",
                nameof(destination));
        }

        return frameType == UncompressedFrameType
            ? DecodeUncompressed(payload, destination, count)
            : DecodeDelta(payload, destination);
    }

    private static int DecodeUncompressed(ReadOnlySpan<byte> payload, Span<short> destination, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var offset = i * UncompressedBytesPerSample;
            for (var axis = 0; axis < Axes; axis++)
            {
                destination[(i * Axes) + axis] =
                    BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + (axis * sizeof(short)))..]);
            }
        }

        return count;
    }

    /// <summary>
    /// Walks the block headers of a delta frame to establish the exact sample count. This is
    /// cheap: it reads the two-byte block headers and skips over the packed bits.
    /// </summary>
    private static int CountDeltaSamples(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < UncompressedBytesPerSample)
        {
            return 0;
        }

        // The reference sample is itself an output sample.
        var count = 1;
        var offset = UncompressedBytesPerSample;

        while (offset + 2 <= payload.Length)
        {
            int bitWidth = payload[offset];
            int samples = payload[offset + 1];
            offset += 2;

            if (samples == 0)
            {
                continue;
            }

            var blockBytes = ((bitWidth * Axes * samples) + 7) / 8;
            if (offset + blockBytes > payload.Length)
            {
                // Truncated block: ignore it rather than read past the end.
                break;
            }

            count += samples;
            offset += blockBytes;
        }

        return count;
    }

    private static int DecodeDelta(ReadOnlySpan<byte> payload, Span<short> destination)
    {
        if (payload.Length < UncompressedBytesPerSample)
        {
            return 0;
        }

        Span<int> current =
        [
            BinaryPrimitives.ReadInt16LittleEndian(payload),
            BinaryPrimitives.ReadInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(payload[4..]),
        ];

        for (var axis = 0; axis < Axes; axis++)
        {
            destination[axis] = (short)current[axis];
        }

        var written = 1;
        var offset = UncompressedBytesPerSample;

        while (offset + 2 <= payload.Length)
        {
            int bitWidth = payload[offset];
            int samples = payload[offset + 1];
            offset += 2;

            if (samples == 0)
            {
                continue;
            }

            var blockBytes = ((bitWidth * Axes * samples) + 7) / 8;
            if (offset + blockBytes > payload.Length)
            {
                break;
            }

            var reader = new BitReader(payload.Slice(offset, blockBytes));
            for (var s = 0; s < samples; s++)
            {
                for (var axis = 0; axis < Axes; axis++)
                {
                    current[axis] = Math.Clamp(
                        current[axis] + reader.ReadSigned(bitWidth),
                        short.MinValue,
                        short.MaxValue);
                    destination[(written * Axes) + axis] = (short)current[axis];
                }

                written++;
            }

            offset += blockBytes;
        }

        return written;
    }

    /// <summary>
    /// Reads LSB-first bit fields that are packed without byte alignment.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitPosition;

        /// <summary>Reads a two's-complement signed field of the given width.</summary>
        public int ReadSigned(int bitWidth)
        {
            if (bitWidth == 0)
            {
                return 0;
            }

            var value = 0;
            for (var i = 0; i < bitWidth; i++)
            {
                var bit = (_data[_bitPosition >> 3] >> (_bitPosition & 7)) & 1;
                value |= bit << i;
                _bitPosition++;
            }

            // Sign-extend from bitWidth to 32 bits.
            if (bitWidth < 32 && (value & (1 << (bitWidth - 1))) != 0)
            {
                value |= ~0 << bitWidth;
            }

            return value;
        }
    }
}
