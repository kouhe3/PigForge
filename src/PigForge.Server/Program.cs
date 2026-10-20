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
    // `--play --level <path under content/levels>` hosts that level as a goal-based room, so any
    // converted original level can be played; without it the sandbox room runs its own terrain-v1.
    int levelIndex = Array.IndexOf(args, "--level");
    string? levelFile = levelIndex >= 0 && levelIndex + 1 < args.Length ? args[levelIndex + 1] : null;
    // `--dev` gives every connection its own world (`?scenario=`/`?level=`, docs/specs/dev-harness.md);
    // the ordinary shared sandbox/level room stays the default.
    bool dev = args.Contains("--dev");
    return await PlayHost.RunAsync(cancel.Token, levelFile, dev);
}

Console.WriteLine("PigForge.Server initialized.");
Console.WriteLine("Pass --demo-ws to tick one Bepu room and broadcast PGFS snapshots on ws://127.0.0.1:5088/snapshots.");
Console.WriteLine("Pass --play for the persistent sandbox world on ws://127.0.0.1:5088/play.");
Console.WriteLine("Pass --play --level <file> (relative to content/levels) to play that level instead.");
Console.WriteLine("Pass --play --dev for the dev harness: every connection gets ?scenario=<id> or ?level=<path>.");
return 0;
