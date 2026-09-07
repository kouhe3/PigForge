namespace PigForge.Benchmarks;

/// <summary>
/// Linear-interpolated percentile over an already-sorted sample.
/// Index is <c>(p/100) * (n-1)</c>, matching the common inclusive sample definition.
/// </summary>
public static class TickPercentiles
{
    public static double At(ReadOnlySpan<double> sortedAscending, double percentile)
    {
        if (sortedAscending.IsEmpty)
        {
            throw new ArgumentException("A percentile requires at least one sample.", nameof(sortedAscending));
        }

        if (!double.IsFinite(percentile) || percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be finite and in [0, 100].");
        }

        if (sortedAscending.Length == 1 || percentile <= 0)
        {
            return sortedAscending[0];
        }

        if (percentile >= 100)
        {
            return sortedAscending[^1];
        }

        double index = (percentile / 100d) * (sortedAscending.Length - 1);
        int low = (int)Math.Floor(index);
        int high = (int)Math.Ceiling(index);
        if (low == high)
        {
            return sortedAscending[low];
        }

        double fraction = index - low;
        return sortedAscending[low] + ((sortedAscending[high] - sortedAscending[low]) * fraction);
    }
}
