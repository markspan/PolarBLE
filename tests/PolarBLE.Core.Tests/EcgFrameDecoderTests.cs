using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class EcgFrameDecoderTests
{
    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0)]
    [InlineData(0x01, 0x00, 0x00, 1)]
    [InlineData(0xFF, 0xFF, 0xFF, -1)]
    [InlineData(0x00, 0x00, 0x80, -8388608)]  // most negative 24-bit value
    [InlineData(0xFF, 0xFF, 0x7F, 8388607)]   // most positive 24-bit value
    [InlineData(0xD0, 0x07, 0x00, 2000)]
    public void Decode_SignExtendsTwentyFourBitSamples(byte b0, byte b1, byte b2, int expected)
    {
        byte[] payload = [b0, b1, b2];

        Span<int> destination = new int[1];
        var count = EcgFrameDecoder.Decode(payload, destination);

        Assert.Equal(1, count);
        Assert.Equal(expected, destination[0]);
    }

    [Fact]
    public void Decode_ReadsEverySampleInOrder()
    {
        byte[] payload = [0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xFF, 0xFF];

        Span<int> destination = new int[3];
        var count = EcgFrameDecoder.Decode(payload, destination);

        int[] expected = [1, 2, -1];
        Assert.Equal(3, count);
        Assert.Equal(expected, destination.ToArray());
    }

    /// <summary>
    /// The original loop condition allowed the final iteration to index one or two bytes past
    /// the end of a payload whose length was not a multiple of three, throwing on a BLE
    /// callback thread and taking the process down mid-recording.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void Decode_IgnoresATrailingPartialSample(int payloadLength)
    {
        var payload = new byte[payloadLength];

        var expected = payloadLength / 3;
        Assert.Equal(expected, EcgFrameDecoder.SampleCount(payload));

        Span<int> destination = new int[expected == 0 ? 1 : expected];
        Assert.Equal(expected, EcgFrameDecoder.Decode(payload, destination));
    }

    [Fact]
    public void Decode_RejectsAnUndersizedDestination()
    {
        byte[] payload = [0x01, 0x00, 0x00, 0x02, 0x00, 0x00];

        Assert.Throws<ArgumentException>(() =>
        {
            Span<int> tooSmall = new int[1];
            EcgFrameDecoder.Decode(payload, tooSmall);
        });
    }

    [Fact]
    public void Decode_RepresentsEveryTwentyFourBitValueExactlyAsAFloat()
    {
        byte[] payload = [0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F];

        Span<float> destination = new float[2];
        EcgFrameDecoder.Decode(payload, destination);

        Assert.Equal([-8388608f, 8388607f], destination.ToArray());
    }
}
