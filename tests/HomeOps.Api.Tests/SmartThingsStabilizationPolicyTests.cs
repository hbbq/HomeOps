using HomeOps.Api.Acquisition;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmartThingsStabilizationPolicyTests
{
    [Fact]
    public void TemperatureDeadband_UsesLastExposedValueAsBaseline()
    {
        const decimal deadband = 0.3m;
        var exposed = 20.0m;

        foreach (var observed in new[] { 20.1m, 20.2m })
        {
            Assert.False(SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(
                observed, exposed, "C", deadband));
        }

        Assert.True(SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(
            20.3m, exposed, "C", deadband));
        exposed = 20.3m;
        Assert.False(SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(
            20.1m, exposed, "C", deadband));
    }

    [Theory]
    [InlineData("F")]
    [InlineData("°F")]
    public void TemperatureDeadband_ConvertsThresholdForFahrenheit(string unit)
    {
        Assert.False(SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(68.5m, 68m, unit, 0.3m));
        Assert.True(SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(68.54m, 68m, unit, 0.3m));
    }

    [Fact]
    public void Policies_AreLimitedToSmartThingsCapabilityKeys()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var motion = new MeasurementSample(
            "smartthings", "device", "Device", "main/motionSensor/motion", "Motion", "boolean", null, 1m, timestamp);
        var otherSource = motion with { Source = "other" };
        var temperature = motion with
        {
            PointKey = "main/temperatureMeasurement/temperature",
            PointName = "Temperature",
            Kind = "temperature"
        };

        Assert.True(SmartThingsStabilizationPolicy.IsMotion(motion));
        Assert.False(SmartThingsStabilizationPolicy.IsMotion(otherSource));
        Assert.True(SmartThingsStabilizationPolicy.IsTemperature(temperature));
    }

    [Fact]
    public void Motion_ExposesActiveImmediatelyButHoldsInactive()
    {
        Assert.True(SmartThingsStabilizationPolicy.ShouldExposeMotionImmediately(0m, 1m));
        Assert.False(SmartThingsStabilizationPolicy.ShouldExposeMotionImmediately(1m, 0m));
        Assert.True(SmartThingsStabilizationPolicy.ShouldExposeMotionImmediately(null, 0m));
    }

    [Fact]
    public void Motion_EachActiveObservationRestartsTheFullHold()
    {
        var firstObservation = new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);
        var secondObservation = firstObservation.AddMinutes(1);
        var hold = TimeSpan.FromMinutes(2);

        Assert.Equal(
            firstObservation.AddMinutes(2),
            SmartThingsStabilizationPolicy.GetMotionHoldDueAt(firstObservation, hold));
        Assert.Equal(
            secondObservation.AddMinutes(2),
            SmartThingsStabilizationPolicy.GetMotionHoldDueAt(secondObservation, hold));
    }
}
