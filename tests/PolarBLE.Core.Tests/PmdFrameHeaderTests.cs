using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class PmdFrameHeaderTests
{
    [Fact]
    public void TryParse_SplitsHeaderFromPayload()
    {
        byte[] frame =
        [
            0x02,                                            // measurement type: ACC
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // timestamp = 1 ns
            0x01,                                            // frame type: uncompressed
            0xAA, 0xBB,                                      // payload
        ];

        Assert.True(PmdFrameHeader.TryParse(frame, out var header, out var payload));
        Assert.Equal(PmdMeasurementType.Acc, header.MeasurementType);
        Assert.Equal(1UL, header.TimestampNanoseconds);
        Assert.Equal(AccFrameDecoder.UncompressedFrameType, header.FrameType);

        byte[] expectedPayload = [0xAA, 0xBB];
        Assert.Equal(expectedPayload, payload.ToArray());
    }

    [Fact]
    public void TryParse_ReadsTheTimestampAsLittleEndianNanoseconds()
    {
        // 0x0102030405060708 little-endian
        byte[] frame = [0x00, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, 0x00];

        Assert.True(PmdFrameHeader.TryParse(frame, out var header, out var payload));
        Assert.Equal(0x0102030405060708UL, header.TimestampNanoseconds);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void TimestampUtc_CountsFromThePolarEpoch()
    {
        byte[] frame = new byte[PmdFrameHeader.Length];
        Assert.True(PmdFrameHeader.TryParse(frame, out var header, out _));

        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), header.TimestampUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void TryParse_RejectsAFrameShorterThanTheHeader(int length)
    {
        Assert.False(PmdFrameHeader.TryParse(new byte[length], out _, out _));
    }
}
