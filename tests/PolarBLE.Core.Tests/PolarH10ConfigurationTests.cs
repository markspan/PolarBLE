using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class PolarH10ConfigurationTests
{
    [Fact]
    public void CreateMeasurements_StartsEcgThenAccelerometer()
    {
        var measurements = PolarH10Configuration.CreateMeasurements("Polar H10 1234", "A0B1C2D3E4F5");

        Assert.Collection(
            measurements,
            ecg =>
            {
                Assert.Equal(PmdMeasurementType.Ecg, ecg.MeasurementType);
                Assert.Equal(PolarH10Configuration.BuildEcgStart(), ecg.StartCommand.ToArray());
                Assert.Equal("Polar H10 1234_ECG", ecg.Stream.Name);
            },
            acc =>
            {
                Assert.Equal(PmdMeasurementType.Acc, acc.MeasurementType);
                Assert.Equal(PolarH10Configuration.BuildAccStart(), acc.StartCommand.ToArray());
                Assert.Equal("PolarBLE:A0B1C2D3E4F5:ACC", acc.Stream.SourceId);
            });
    }

    [Fact]
    public void CreateMeasurements_DecodersMatchTheirStreamsChannelCounts()
    {
        foreach (var measurement in PolarH10Configuration.CreateMeasurements("Polar H10", "0"))
        {
            Assert.Equal(measurement.Stream.Channels.Count, measurement.Decoder.ChannelCount);
        }
    }

    [Fact]
    public void BuildStopCommand_AddressesTheConfiguredMeasurement()
    {
        var acc = PolarH10Configuration.CreateMeasurements("Polar H10", "0")[1];

        Assert.Equal(PmdControlPoint.BuildStop(PmdMeasurementType.Acc), acc.BuildStopCommand());
    }
}
