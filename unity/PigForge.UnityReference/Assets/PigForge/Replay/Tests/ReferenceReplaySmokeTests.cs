using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PigForge.UnityReference;
using UnityEngine;
using UnityEngine.TestTools;

namespace PigForge.UnityReference.Tests
{
/// <summary>
/// Play Mode smoke test for the Unity reference adapter: creates the ground + box scene,
/// runs the fixed-tick export, and asserts the replay document is produced and well-formed.
/// </summary>
public sealed class ReferenceReplaySmokeTests
{
    private const string OutputFileName = "unity-reference-replay.json";

    [UnityTest]
    public IEnumerator ExportSimpleSceneProducesGroundAndBoxFixedTickReplay()
    {
        yield return null;

        Assert.IsTrue(Application.isPlaying, "The replay export smoke test must run in Play Mode.");

        string outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "replays", OutputFileName));
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        var host = new GameObject("ReferenceReplaySmokeTestHost");
        try
        {
            var exporter = host.AddComponent<ReferenceReplayExporter>();

            exporter.ExportSimpleScene();

            Assert.IsTrue(File.Exists(outputPath), $"Expected the replay export at {outputPath}");
            string json = File.ReadAllText(outputPath);
            Assert.That(json, Does.Contain("\"format\": \"pigforge.physics.replay\""));
            Assert.That(json, Does.Contain("\"protocolVersion\": 1"));
            Assert.That(json, Does.Contain("\"outcome\": \"SUCCESS\""));

            Match stateHash = Regex.Match(json, "\"stateHash\": \"([0-9a-f]{64})\"");
            Assert.IsTrue(stateHash.Success, "finalResult.stateHash must be lowercase sha256 hex.");
            Assert.That(stateHash.Groups[1].Value, Is.Not.EqualTo(new string('0', 64)), "State hash must not be all zeros.");
        }
        finally
        {
            UnityEngine.Object.Destroy(host);
        }
    }
}
}
