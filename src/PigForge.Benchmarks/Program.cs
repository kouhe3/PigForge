using System.Globalization;
using PigForge.Benchmarks;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

SceneReport[] reports =
[
    BaselineRunner.RunTypical(),
    BaselineRunner.RunStress(),
    BaselineRunner.RunConcurrent()
];

Console.WriteLine("scene\tticks\tbodies\trooms\tp50_ms\tp95_ms\tp99_ms\talloc_p50\talloc_p95\tgc_pause_ms\tworking_set\tcommitted\tsnapshot_max");
foreach (SceneReport report in reports)
{
    Console.WriteLine(string.Join('\t',
        report.Scene,
        report.Ticks.ToString(CultureInfo.InvariantCulture),
        report.Bodies.ToString(CultureInfo.InvariantCulture),
        report.ConcurrentRooms.ToString(CultureInfo.InvariantCulture),
        report.TickP50Ms.ToString("0.000", CultureInfo.InvariantCulture),
        report.TickP95Ms.ToString("0.000", CultureInfo.InvariantCulture),
        report.TickP99Ms.ToString("0.000", CultureInfo.InvariantCulture),
        report.AllocP50Bytes.ToString("0", CultureInfo.InvariantCulture),
        report.AllocP95Bytes.ToString("0", CultureInfo.InvariantCulture),
        report.GcPauseMs.ToString("0.000", CultureInfo.InvariantCulture),
        report.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
        report.TotalCommittedBytes.ToString(CultureInfo.InvariantCulture),
        report.SnapshotBytesMax.ToString(CultureInfo.InvariantCulture)));
}
