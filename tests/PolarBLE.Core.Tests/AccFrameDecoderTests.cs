using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class AccFrameDecoderTests
{
    [Fact]
    public void Uncompressed_DecodesInterleavedSignedAxes()
    {
        // Two samples: (1, -1, 32767) and (-32768, 0, 5)
        byte[] payload =
        [
            0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F,
            0x00, 0x80, 0x00, 0x00, 0x05, 0x00,
        ];

        Span<short> destination = new short[6];
        var count = AccFrameDecoder.Decode(AccFrameDecoder.UncompressedFrameType, payload, destination);

        short[] expected = [1, -1, 32767, -32768, 0, 5];
        Assert.Equal(2, count);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public void Uncompressed_IgnoresATrailingPartialSample()
    {
        // One whole sample plus three stray bytes.
        byte[] payload = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0xAA, 0xBB, 0xCC];

        Assert.Equal(1, AccFrameDecoder.SampleCount(AccFrameDecoder.UncompressedFrameType, payload));
    }

    /// <summary>
    /// The regression that matters most: a delta-compressed frame. The original implementation
    /// inferred a 9-byte stride for frame type 0x02 and read the packed delta bits as absolute
    /// 16-bit axes, silently emitting noise.
    /// </summary>
    [Fact]
    public void Delta_AccumulatesPackedDeltasOntoTheReferenceSample()
    {
        // Reference (100, -200, 300), then one block of 2 samples at 4 bits per delta.
        // Deltas, in axis order: (+1, +2, -1) then (-2, 0, +3), packed LSB-first.
        byte[] payload =
        [
            0x64, 0x00,       // x =  100
            0x38, 0xFF,       // y = -200
            0x2C, 0x01,       // z =  300
            0x04,             // 4 bits per delta
            0x02,             // 2 samples in this block
            0x21, 0xEF, 0x30, // packed nibbles: 1, 2, -1, -2, 0, 3
        ];

        Assert.Equal(3, AccFrameDecoder.SampleCount(AccFrameDecoder.DeltaFrameType, payload));

        Span<short> destination = new short[9];
        var count = AccFrameDecoder.Decode(AccFrameDecoder.DeltaFrameType, payload, destination);

        short[] expected =
        [
            100, -200, 300,   // the reference sample is itself an output sample
            101, -198, 299,
            99, -198, 302,
        ];
        Assert.Equal(3, count);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public void Delta_HandlesMultipleBlocksInOneFrame()
    {
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // reference (0, 0, 0)
            0x04, 0x01, 0x21, 0x03,             // block 1: 1 sample, deltas (1, 2, 3)
            0x04, 0x01, 0x21, 0x03,             // block 2: 1 sample, deltas (1, 2, 3)
        ];

        Span<short> destination = new short[9];
        var count = AccFrameDecoder.Decode(AccFrameDecoder.DeltaFrameType, payload, destination);

        short[] expected = [0, 0, 0, 1, 2, 3, 2, 4, 6];
        Assert.Equal(3, count);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public void Delta_IgnoresATruncatedBlockRatherThanReadingPastTheEnd()
    {
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x08, 0x0A, 0x01, // claims 10 samples at 8 bits but supplies one byte
        ];

        Assert.Equal(1, AccFrameDecoder.SampleCount(AccFrameDecoder.DeltaFrameType, payload));

        Span<short> destination = new short[3];
        Assert.Equal(1, AccFrameDecoder.Decode(AccFrameDecoder.DeltaFrameType, payload, destination));
    }

    [Fact]
    public void UnknownFrameType_IsRejectedInsteadOfGuessedAt()
    {
        byte[] payload = new byte[12];

        Assert.Throws<NotSupportedException>(
            () => AccFrameDecoder.SampleCount(0x7F, payload));
    }

    [Fact]
    public void Decode_RejectsAnUndersizedDestination()
    {
        byte[] payload = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00];

        Assert.Throws<ArgumentException>(() =>
        {
            Span<short> tooSmall = new short[2];
            AccFrameDecoder.Decode(AccFrameDecoder.UncompressedFrameType, payload, tooSmall);
        });
    }

    [Theory]
    [InlineData(AccFrameDecoder.UncompressedFrameType, true)]
    [InlineData(AccFrameDecoder.DeltaFrameType, true)]
    [InlineData(0x00, false)]
    [InlineData(0x7F, false)]
    public void IsSupported_AcceptsOnlyTheKnownEncodings(byte frameType, bool expected)
    {
        Assert.Equal(expected, AccFrameDecoder.IsSupported(frameType));
    }

    [Fact]
    public void Delta_StopsAtABlockWithAnImpossibleBitWidth()
    {
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // reference (0, 0, 0)
            0x04, 0x01, 0x21, 0x03,             // valid block: deltas (1, 2, 3)
            0x21, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, // 33-bit deltas cannot exist
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF,
        ];

        Assert.Equal(2, AccFrameDecoder.SampleCount(AccFrameDecoder.DeltaFrameType, payload));
    }

    [Fact]
    public void Delta_ClampsTheRunningValueToTheSixteenBitRange()
    {
        byte[] payload =
        [
            0xFF, 0x7F, 0x00, 0x80, 0x00, 0x00, // reference (32767, -32768, 0)
            0x04, 0x01, 0xF1, 0x00,             // deltas (+1, -1, 0)
        ];

        Span<short> destination = new short[6];
        AccFrameDecoder.Decode(AccFrameDecoder.DeltaFrameType, payload, destination);

        Assert.Equal([short.MaxValue, short.MinValue, 0], destination[3..].ToArray());
    }

    [Fact]
    public void Decode_ProducesTheSameValuesAsFloats()
    {
        byte[] payload = [0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F];

        Span<float> destination = new float[3];
        AccFrameDecoder.Decode(AccFrameDecoder.UncompressedFrameType, payload, destination);

        Assert.Equal([1f, -1f, 32767f], destination.ToArray());
    }

    [Fact]
    public void Delta_SaturatesInsteadOfWrappingOnAFullWidthDelta()
    {
        byte[] payload =
        [
            0xFF, 0x7F, 0x00, 0x00, 0x00, 0x00, // reference (32767, 0, 0)
            0x20, 0x01,                         // one sample of 32-bit deltas
            0xFF, 0xFF, 0xFF, 0x7F,             // x: +2^31 - 1
            0x00, 0x00, 0x00, 0x00,             // y: 0
            0x00, 0x00, 0x00, 0x00,             // z: 0
        ];

        Span<short> destination = new short[6];
        AccFrameDecoder.Decode(AccFrameDecoder.DeltaFrameType, payload, destination);

        Assert.Equal([short.MaxValue, 0, 0], destination[3..].ToArray());
    }
}
