using PigForge.Benchmarks;

namespace PigForge.Server.Tests;

public sealed class PerformanceBaselineTests
{
    [Fact]
    public void PercentileInterpolatesInclusiveSample()
    {
        double[] samples = { 1, 2, 3, 4, 5 };

        Assert.Equal(1, TickPercentiles.At(samples, 0));
        Assert.Equal(3, TickPercentiles.At(samples, 50));
        Assert.Equal(5, TickPercentiles.At(samples, 100));
        Assert.Equal(2, TickPercentiles.At(samples, 25));
    }

    [Fact]
    public void PercentileRejectsEmptyAndOutOfRange()
    {
        Assert.Throws<ArgumentException>(() => TickPercentiles.At(ReadOnlySpan<double>.Empty, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => TickPercentiles.At(new double[] { 1 }, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TickPercentiles.At(new double[] { 1 }, 101));
    }

    [Fact]
    public void TypicalBaselineEmitsRequiredMetrics()
    {
        SceneReport report = BaselineRunner.RunTypical();

        Assert.Equal("typical", report.Scene);
        Assert.Equal(240, report.Ticks);
        Assert.Equal(4, report.Bodies);
        Assert.Equal(1, report.ConcurrentRooms);
        Assert.True(report.TickP50Ms <= report.TickP95Ms);
        Assert.True(report.TickP95Ms <= report.TickP99Ms);
        Assert.True(report.SnapshotBytesMax > 0);
        Assert.True(report.WorkingSetBytes > 0);
        Assert.True(report.TotalCommittedBytes > 0);
        Assert.True(report.GcPauseMs >= 0);
    }
}
