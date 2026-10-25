using System.Buffers.Binary;
using System.Numerics;

namespace PolarBLE.Core.Polar;

/// <summary>
/// Decodes the payload of a PMD accelerometer frame into milli-g samples.
/// </summary>
/// <remarks>
/// <para>
/// Two encodings exist and they are distinguished by the frame type, not by a resolution
/// field. Frame type 0x01 is uncompressed: six bytes per sample, three little-endian 16-bit
/// axes. Frame type 0x02 is delta-compressed: an uncompressed reference sample, then a series
/// of blocks, each introduced by a delta bit width and a sample count, whose bit-packed
/// signed deltas accumulate onto the running value.
/// </para>
/// <para>
/// Malformed input is never guessed at. A truncated or impossible delta block ends decoding
/// at the last complete sample, and an unknown frame type is rejected outright.
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

    /// <summary>The widest delta field this decoder accepts; anything wider is malformed.</summary>
    public const int MaxDeltaBitWidth = 32;

    /// <summary>Whether frames of the given type can be decoded.</summary>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <returns>True for the uncompressed and delta-compressed frame types.</returns>
    public static bool IsSupported(byte frameType) =>
        frameType is UncompressedFrameType or DeltaFrameType;

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
        _ => throw UnsupportedFrameType(frameType),
    };

    /// <summary>
    /// Decodes a payload into interleaved x, y, z milli-g values.
    /// </summary>
    /// <typeparam name="T">
    /// The numeric type to decode into, typically <see cref="short"/> for analysis or
    /// <see cref="float"/> for streaming.
    /// </typeparam>
    /// <param name="frameType">The frame type byte from the header.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <param name="destination">
    /// Receives interleaved axes; its length must be at least three times the sample count.
    /// </param>
    /// <returns>The number of samples (not values) written.</returns>
    /// <exception cref="NotSupportedException"><paramref name="frameType"/> is unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static int Decode<T>(byte frameType, ReadOnlySpan<byte> payload, Span<T> destination)
        where T : INumberBase<T>
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

    private static NotSupportedException UnsupportedFrameType(byte frameType) =>
        new($"Unsupported accelerometer frame type 0x{frameType:X2}.");

    private static int DecodeUncompressed<T>(ReadOnlySpan<byte> payload, Span<T> destination, int count)
        where T : INumberBase<T>
    {
        for (var i = 0; i < count * Axes; i++)
        {
            destination[i] = T.CreateTruncating(
                BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i * sizeof(short), sizeof(short))));
        }

        return count;
    }

    private static int CountDeltaSamples(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < UncompressedBytesPerSample)
        {
            return 0;
        }

        // The reference sample is itself an output sample.
        var count = 1;
        var blocks = new DeltaBlockReader(payload[UncompressedBytesPerSample..]);
        while (blocks.TryReadNext(out var block))
        {
            count += block.SampleCount;
        }

        return count;
    }

    private static int DecodeDelta<T>(ReadOnlySpan<byte> payload, Span<T> destination)
        where T : INumberBase<T>
    {
        if (payload.Length < UncompressedBytesPerSample)
        {
            return 0;
        }

        // The running value is held at full int width so that a delta can never wrap it; it is
        // clamped to the 16-bit range the sensor itself produces.
        Span<int> current = stackalloc int[Axes];
        for (var axis = 0; axis < Axes; axis++)
        {
            current[axis] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(axis * sizeof(short), sizeof(short)));
            destination[axis] = T.CreateTruncating(current[axis]);
        }

        var written = 1;
        var blocks = new DeltaBlockReader(payload[UncompressedBytesPerSample..]);
        while (blocks.TryReadNext(out var block))
        {
            var bits = new BitReader(block.PackedDeltas);
            for (var sample = 0; sample < block.SampleCount; sample++)
            {
                for (var axis = 0; axis < Axes; axis++)
                {
                    // Summed at 64 bits: a full-width 32-bit delta would otherwise wrap the int
                    // before it is clamped and flip the sign of the result.
                    current[axis] = (int)Math.Clamp(
                        (long)current[axis] + bits.ReadSigned(block.BitWidth),
                        short.MinValue,
                        short.MaxValue);
                    destination[(written * Axes) + axis] = T.CreateTruncating(current[axis]);
                }

                written++;
            }
        }

        return written;
    }

    /// <summary>One block of a delta-compressed frame.</summary>
    private readonly ref struct DeltaBlock(int bitWidth, int sampleCount, ReadOnlySpan<byte> packedDeltas)
    {
        /// <summary>Width in bits of every delta in the block.</summary>
        public int BitWidth { get; } = bitWidth;

        /// <summary>Number of samples, each carrying one delta per axis.</summary>
        public int SampleCount { get; } = sampleCount;

        /// <summary>The bit-packed deltas, exactly as long as the block needs.</summary>
        public ReadOnlySpan<byte> PackedDeltas { get; } = packedDeltas;
    }

    /// <summary>
    /// Walks the blocks of a delta-compressed frame. Counting and decoding share this single
    /// walker, so the two can never disagree about where a frame ends.
    /// </summary>
    private ref struct DeltaBlockReader(ReadOnlySpan<byte> blocks)
    {
        private const int BlockHeaderLength = 2;

        private readonly ReadOnlySpan<byte> _blocks = blocks;
        private int _offset;

        /// <summary>
        /// Reads the next non-empty block. Returns false at the end of the frame, and also on a
        /// truncated block or an impossible bit width: whatever follows malformed data cannot
        /// be trusted, so decoding stops at the last complete sample.
        /// </summary>
        public bool TryReadNext(out DeltaBlock block)
        {
            while (_offset + BlockHeaderLength <= _blocks.Length)
            {
                int bitWidth = _blocks[_offset];
                int samples = _blocks[_offset + 1];
                _offset += BlockHeaderLength;

                if (bitWidth > MaxDeltaBitWidth)
                {
                    break;
                }

                if (samples == 0)
                {
                    continue;
                }

                var length = ((bitWidth * Axes * samples) + 7) / 8;
                if (_offset + length > _blocks.Length)
                {
                    break;
                }

                block = new DeltaBlock(bitWidth, samples, _blocks.Slice(_offset, length));
                _offset += length;
                return true;
            }

            _offset = _blocks.Length;
            block = default;
            return false;
        }
    }

    /// <summary>
    /// Reads LSB-first bit fields that are packed without byte alignment.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitPosition;

        /// <summary>Reads a two's-complement signed field of the given width (0 to 32 bits).</summary>
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

            // Sign-extend from bitWidth to 32 bits. A 32-bit field is already full width.
            var unusedBits = 32 - bitWidth;
            return (value << unusedBits) >> unusedBits;
        }
    }
}
