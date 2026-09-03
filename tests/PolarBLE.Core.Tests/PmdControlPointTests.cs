using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class PmdControlPointTests
{
    // The exact byte arrays the original implementation hand-wrote and shipped. They are known
    // to work against real hardware, so the refactored builder must reproduce them verbatim.
    private static readonly byte[] OriginalEcgWrite =
        [0x02, 0x00, 0x00, 0x01, 0x82, 0x00, 0x01, 0x01, 0x0E, 0x00];

    private static readonly byte[] OriginalAccWrite =
        [0x02, 0x02, 0x00, 0x01, 0xC8, 0x00, 0x01, 0x01, 0x10, 0x00, 0x02, 0x01, 0x08, 0x00];

    [Fact]
    public void BuildEcgStart_ReproducesTheOriginalByteSequence()
    {
        Assert.Equal(OriginalEcgWrite, PolarH10Configuration.BuildEcgStart());
    }

    [Fact]
    public void BuildAccStart_ReproducesTheOriginalByteSequence()
    {
        Assert.Equal(OriginalAccWrite, PolarH10Configuration.BuildAccStart());
    }

    [Fact]
    public void BuildStart_EncodesSettingValuesLittleEndian()
    {
        var request = PmdControlPoint.BuildStart(
            PmdMeasurementType.Acc,
            new PmdSetting(PmdSettingType.SampleRate, 200));

        // opcode, measurement type, then type / array length / uint16 little-endian
        Assert.Equal([0x02, 0x02, 0x00, 0x01, 0xC8, 0x00], request);
    }

    [Fact]
    public void BuildStop_IsOpcodeAndMeasurementType()
    {
        Assert.Equal([0x03, 0x00], PmdControlPoint.BuildStop(PmdMeasurementType.Ecg));
    }

    [Fact]
    public void TryParseResponse_ReadsASuccessfulAcknowledgement()
    {
        byte[] frame = [0xF0, 0x02, 0x00, 0x00, 0x00];

        Assert.True(PmdControlPoint.TryParseResponse(frame, out var response));
        Assert.Equal(PmdOpCode.StartMeasurement, response.OpCode);
        Assert.Equal(PmdMeasurementType.Ecg, response.MeasurementType);
        Assert.Equal(PmdError.Success, response.Error);
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public void TryParseResponse_SurfacesTheErrorCodeThatUsedToBeDiscarded()
    {
        // A rejected configuration: this is precisely the case the original code could not see.
        byte[] frame = [0xF0, 0x02, 0x02, 0x05];

        Assert.True(PmdControlPoint.TryParseResponse(frame, out var response));
        Assert.Equal(PmdMeasurementType.Acc, response.MeasurementType);
        Assert.Equal(PmdError.InvalidParameter, response.Error);
        Assert.False(response.IsSuccess);
    }

    [Theory]
    [InlineData(new byte[] { 0xF0, 0x02, 0x00 })]      // too short
    [InlineData(new byte[] { 0x00, 0x02, 0x00, 0x00 })] // not a response frame
    [InlineData(new byte[0])]
    public void TryParseResponse_RejectsMalformedFrames(byte[] frame)
    {
        Assert.False(PmdControlPoint.TryParseResponse(frame, out _));
    }
}
