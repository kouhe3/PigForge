using PigForge.Server;

if (args.Contains("--demo-ws"))
{
    using CancellationTokenSource cancel = new();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancel.Cancel();
    };
    return await DemoSnapshotHost.RunAsync(cancel.Token);
}

if (args.Contains("--play"))
{
    using CancellationTokenSource cancel = new();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancel.Cancel();
    };
    return await PlayHost.RunAsync(cancel.Token);
}

Console.WriteLine("PigForge.Server initialized.");
Console.WriteLine("Pass --demo-ws to tick one Bepu room and broadcast PGFS snapshots on ws://127.0.0.1:5088/snapshots.");
Console.WriteLine("Pass --play for the slope build loop on ws://127.0.0.1:5088/play.");
return 0;
