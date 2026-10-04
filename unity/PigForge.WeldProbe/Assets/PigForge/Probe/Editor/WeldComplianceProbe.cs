using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PigForge.WeldProbe.Probe
{
/// <summary>
/// Measures how far a Bad Piggies style weld lets its two bodies drift apart, on the ORIGINAL's
/// own PhysX: Unity 2021.3.45f2, the editor `BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt`
/// pins, and the original's own settings (BPLE 2022.1.9/ProjectSettings/{DynamicsManager,TimeManager}.asset).
/// This is the reference side of docs/specs/weld-compliance.md: PigForge welds adjacent parts into
/// one compound body (ADR-011) and therefore has no weld compliance at all, and the original has
/// no authored spring to copy -- if its welds are soft at all, the softness has to come from
/// PhysX solver behaviour (the one authored switch is Joint.enablePreprocessing, see
/// Contraption.cs:1540 and the Unity scripting reference).
///
/// Setup mirrored from the original, one line per source:
///   - joint: ConfigurableJoint, all six motions Locked, anchors at half the relative position,
///     enablePreprocessing = a &amp;&amp; b, breakForce = gs(a) + gs(b)   (Contraption.cs:1507-1546)
///   - body: drag 0.2, angularDrag 0.05, gravity on, Interpolate, 2.5D constraints
///     (freeze Z position + freeze X/Y rotation)                      (BasePart.cs:1184-1196)
///   - physics: fixed timestep 0.02, solver iterations 6/1, max angular speed 7, contact offset
///     0.005, sleep threshold 0.005, gravity -9.81, bounce threshold 2  (BPLE 2022.1.9 ProjectSettings)
/// The frames' colliders are 1x1x1 boxes in every prefab (tasks/bple-jointstrength-report.json),
/// so a primitive cube is the honest shape; the two bodies sit a unit apart so no contact ever
/// resists the measured drift. "drift" is the relative X offset minus its resting value.
///
/// Driven headlessly: unity run unity/PigForge.WeldProbe -- -executeMethod
/// PigForge.WeldProbe.Probe.WeldComplianceProbe.Run
/// </summary>
public static class WeldComplianceProbe
{
    private const float FixedTimeStep = 0.02f;   // BPLE TimeManager "Fixed Timestep: 0.02"
    private const int StepCount = 300;           // 6 s
    private const int SeriesStride = 5;
    private const float LoadNewtons = 20f;
    private const int SteadyWindow = 50;
    private const float RestOffsetX = 1f;
    private const float BreakForceWoodWood = 1000f;   // (250 + 250) * 2, Contraption.cs:1541-1543
    private const float BreakForceMetalMetal = 2400f; // (600 + 600) * 2
    private const int Constraints25D = 56;            // freeze Z position, freeze X/Y rotation

    private sealed class Cell
    {
        public string Id;
        public bool Preprocessing;
        public int Constraints;
        public float PartMass;
        public float AnchorMass;
        public bool AnchorKinematic;
        public float BreakForce;
        public bool RampToBreak;
        public bool Impulse;
        public bool Projection;
        public readonly List<float> DriftX = new List<float>();
        public readonly List<float> DriftY = new List<float>();
        public readonly List<float> ZAngle = new List<float>();
        public float MaxJointForce;
        public float BreakAtForce = float.NaN;
        public int BreakAtStep = -1;
    }

    public static void Run()
    {
        ApplyOriginalPhysicsSettings();

        List<Cell> cells = new List<Cell>();

        // A: kinematic anchor, one part under a constant 20 N load along a locked axis. The
        // preprocessing / 2.5D / mass grid answers "how soft is which weld".
        foreach (bool preprocessing in new[] { true, false })
        foreach (int constraints in new[] { Constraints25D, 0 })
        foreach (float mass in new[] { 0.5f, 1f, 4f, 20f })
        {
            cells.Add(new Cell
            {
                Id = string.Format(CultureInfo.InvariantCulture, "load_pp{0}_c{1}_m{2}", preprocessing ? "on" : "off", constraints, mass),
                Preprocessing = preprocessing,
                Constraints = constraints,
                PartMass = mass,
                AnchorKinematic = true,
                BreakForce = BreakForceWoodWood,
            });
        }

        // B: a dynamic 50 kg anchor, i.e. a real rig holding one frame -- the mass ratio the
        // original actually runs with.
        foreach (bool preprocessing in new[] { true, false })
        {
            cells.Add(new Cell
            {
                Id = "rig_pp" + (preprocessing ? "on" : "off"),
                Preprocessing = preprocessing,
                Constraints = Constraints25D,
                PartMass = 0.5f,
                AnchorMass = 50f,
                AnchorKinematic = false,
                BreakForce = BreakForceWoodWood,
            });
        }

        // C: ramp the load until the joint pops, to check the break threshold behaves as the
        // (gs(a)+gs(b)) sum the content now carries.
        foreach (float breakForce in new[] { BreakForceWoodWood, BreakForceMetalMetal })
        {
            cells.Add(new Cell
            {
                Id = string.Format(CultureInfo.InvariantCulture, "break_{0}", breakForce),
                Preprocessing = true,
                Constraints = Constraints25D,
                PartMass = 0.5f,
                AnchorKinematic = true,
                BreakForce = breakForce,
                RampToBreak = true,
            });
        }

        // D: impulse transient -- a 20 m/s kick at a real rig mass ratio, the regime a moving
        // contraption actually hits.
        foreach (bool preprocessing in new[] { true, false })
        {
            cells.Add(new Cell
            {
                Id = "impulse_pp" + (preprocessing ? "on" : "off"),
                Preprocessing = preprocessing,
                Constraints = Constraints25D,
                PartMass = 0.5f,
                AnchorMass = 50f,
                AnchorKinematic = false,
                BreakForce = float.PositiveInfinity,
                Impulse = true,
            });
        }

        // E: control -- the load cell with explicit joint projection, to learn whether any
        // rigidity comes from projection rather than from the solver.
        foreach (bool preprocessing in new[] { true, false })
        {
            cells.Add(new Cell
            {
                Id = "proj_pp" + (preprocessing ? "on" : "off"),
                Preprocessing = preprocessing,
                Constraints = Constraints25D,
                PartMass = 0.5f,
                AnchorKinematic = true,
                BreakForce = BreakForceWoodWood,
                Projection = true,
            });
        }

        foreach (Cell cell in cells)
        {
            RunCell(cell);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[weld-probe] {0}: maxDriftX={1:0.######} steadyDriftX={2:0.######} maxDriftY={3:0.######} maxZ={4:0.######}deg maxJointForce={5:0.###}{6}",
                cell.Id,
                Max(cell.DriftX),
                Steady(cell.DriftX),
                Max(cell.DriftY),
                Max(cell.ZAngle),
                cell.MaxJointForce,
                cell.BreakAtStep >= 0 ? string.Format(CultureInfo.InvariantCulture, " broke@step{0} force={1:0.###}", cell.BreakAtStep, cell.BreakAtForce) : string.Empty));
        }

        string outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "replays", "weld-compliance-probe.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, WriteJson(cells), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log("[weld-probe] wrote " + outputPath);
    }

    /// <summary>Mirrors BPLE 2022.1.9/ProjectSettings/{DynamicsManager,TimeManager}.asset.</summary>
    private static void ApplyOriginalPhysicsSettings()
    {
        Physics.gravity = new Vector3(0f, -9.81f, 0f);
        Physics.defaultSolverIterations = 6;
        Physics.defaultSolverVelocityIterations = 1;
        Physics.sleepThreshold = 0.005f;
        Physics.defaultContactOffset = 0.005f;
        Physics.bounceThreshold = 2f;
        Physics.defaultMaxDepenetrationVelocity = 10f;
        Physics.defaultMaxAngularSpeed = 7f;
        Physics.autoSyncTransforms = true;
#if UNITY_6000_0_OR_NEWER
        Physics.simulationMode = SimulationMode.Script;
#else
        // Unity 2021 has no Physics.simulationMode yet; autoSimulation = false plus an explicit
        // Physics.Simulate per step is the same thing and keeps the probe deterministic.
        Physics.autoSimulation = false;
#endif
    }

    private static void RunCell(Cell cell)
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }

        GameObject anchor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        anchor.name = "Anchor";
        anchor.transform.position = Vector3.zero;
        Rigidbody anchorBody = anchor.AddComponent<Rigidbody>();
        ConfigureBody(anchorBody, cell.AnchorKinematic ? 1f : cell.AnchorMass, cell.Constraints, kinematic: cell.AnchorKinematic);

        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = "Part";
        part.transform.position = new Vector3(RestOffsetX, 0f, 0f); // clear air: no contact can resist
        Rigidbody partBody = part.AddComponent<Rigidbody>();
        ConfigureBody(partBody, cell.PartMass, cell.Constraints, kinematic: false);

        ConfigurableJoint joint = AttachWeld(part, partBody, anchor, anchorBody, cell.Preprocessing, cell.BreakForce, cell.Projection);
        if (cell.Impulse)
        {
            partBody.velocity = new Vector3(20f, 0f, 0f);
        }

        for (int step = 0; step < StepCount; step++)
        {
            float force = cell.Impulse ? 0f : (cell.RampToBreak ? LoadNewtons * (1f + step) : LoadNewtons);
            if (force > 0f)
            {
                partBody.AddForce(new Vector3(force, 0f, 0f), ForceMode.Force);
            }

            Physics.Simulate(FixedTimeStep);

            if (joint == null)
            {
                cell.BreakAtStep = step;
                cell.BreakAtForce = force;
                break;
            }

            cell.MaxJointForce = Mathf.Max(cell.MaxJointForce, joint.currentForce.magnitude);
            cell.DriftX.Add(part.transform.position.x - anchor.transform.position.x - RestOffsetX);
            cell.DriftY.Add(part.transform.position.y - anchor.transform.position.y);
            cell.ZAngle.Add(Normalize(part.transform.eulerAngles.z));
        }

        UnityEngine.Object.DestroyImmediate(part);
        UnityEngine.Object.DestroyImmediate(anchor);
    }

    private static void ConfigureBody(Rigidbody body, float mass, int constraints, bool kinematic)
    {
        body.mass = mass;
#if UNITY_6000_0_OR_NEWER
        body.linearDamping = 0.2f;   // BasePart.cs:1192
        body.angularDamping = 0.05f; // BasePart.cs:1193
#else
        body.drag = 0.2f;            // BasePart.cs:1192 (Unity 2021 name)
        body.angularDrag = 0.05f;    // BasePart.cs:1193
#endif
        body.useGravity = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = (RigidbodyConstraints)constraints;
        body.isKinematic = kinematic;
    }

    private static ConfigurableJoint AttachWeld(
        GameObject part, Rigidbody partBody, GameObject anchor, Rigidbody anchorBody, bool preprocessing, float breakForce, bool projection)
    {
        ConfigurableJoint joint = partBody.gameObject.AddComponent<ConfigurableJoint>();
        joint.autoConfigureConnectedAnchor = false;
        joint.anchor = part.transform.InverseTransformPoint(anchor.transform.position) * 0.5f;
        joint.connectedAnchor = anchor.transform.InverseTransformPoint(part.transform.position) * 0.5f;
        joint.xMotion = ConfigurableJointMotion.Locked;
        joint.yMotion = ConfigurableJointMotion.Locked;
        joint.zMotion = ConfigurableJointMotion.Locked;
        joint.angularXMotion = ConfigurableJointMotion.Locked;
        joint.angularYMotion = ConfigurableJointMotion.Locked;
        joint.angularZMotion = ConfigurableJointMotion.Locked;
        joint.connectedBody = anchorBody;
        joint.enablePreprocessing = preprocessing;
        joint.breakForce = breakForce;
        if (projection)
        {
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
        }
        return joint;
    }

    private static float Normalize(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }

    private static float Max(List<float> values)
    {
        float max = 0f;
        for (int index = 0; index < values.Count; index++)
        {
            max = Mathf.Max(max, Mathf.Abs(values[index]));
        }
        return max;
    }

    private static float Steady(List<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }
        int start = Mathf.Max(0, values.Count - SteadyWindow);
        float sum = 0f;
        for (int index = start; index < values.Count; index++)
        {
            sum += values[index];
        }
        return sum / (values.Count - start);
    }

    private static string WriteJson(List<Cell> cells)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.weld-compliance-probe\",\n");
        json.Append("  \"probeVersion\": 1,\n");
        json.Append("  \"reference\": \"unity 2021.3.45f2, real PhysX, original joint setup (Contraption.cs:1507-1546)\",\n");
        json.Append("  \"physics\": {\"fixedTimeStep\": 0.02, \"solverIterations\": 6, \"solverVelocityIterations\": 1, \"maxAngularSpeed\": 7, \"gravity\": -9.81, \"contactOffset\": 0.005, \"sleepThreshold\": 0.005},\n");
        json.Append("  \"loadNewtons\": 20,\n");
        json.Append("  \"stepCount\": ").Append(StepCount).Append(",\n");
        json.Append("  \"seriesStride\": ").Append(SeriesStride).Append(",\n");
        json.Append("  \"cells\": [\n");
        for (int index = 0; index < cells.Count; index++)
        {
            Cell cell = cells[index];
            json.Append("    {\n");
            json.Append("      \"id\": \"").Append(cell.Id).Append("\",\n");
            json.Append("      \"enablePreprocessing\": ").Append(cell.Preprocessing ? "true" : "false").Append(",\n");
            json.Append("      \"projection\": ").Append(cell.Projection ? "\"positionAndRotation\"" : "\"default\"").Append(",\n");
            json.Append("      \"impulse\": ").Append(cell.Impulse ? "true" : "false").Append(",\n");
            json.Append("      \"rigidbodyConstraints\": ").Append(cell.Constraints).Append(",\n");
            json.Append("      \"partMass\": ").Append(Number(cell.PartMass)).Append(",\n");
            json.Append("      \"anchorMass\": ").Append(Number(cell.AnchorKinematic ? 0f : cell.AnchorMass)).Append(",\n");
            json.Append("      \"anchorKinematic\": ").Append(cell.AnchorKinematic ? "true" : "false").Append(",\n");
            json.Append("      \"breakForce\": ").Append(Number(cell.BreakForce)).Append(",\n");
            json.Append("      \"maxJointForce\": ").Append(Number(cell.MaxJointForce)).Append(",\n");
            json.Append("      \"maxDriftX\": ").Append(Number(Max(cell.DriftX))).Append(",\n");
            json.Append("      \"steadyDriftX\": ").Append(Number(Steady(cell.DriftX))).Append(",\n");
            json.Append("      \"maxDriftY\": ").Append(Number(Max(cell.DriftY))).Append(",\n");
            json.Append("      \"maxZAngleDeg\": ").Append(Number(Max(cell.ZAngle))).Append(",\n");
            json.Append("      \"breakAtStep\": ").Append(cell.BreakAtStep).Append(",\n");
            json.Append("      \"breakAtForce\": ").Append(float.IsNaN(cell.BreakAtForce) ? "null" : Number(cell.BreakAtForce)).Append(",\n");
            json.Append("      \"driftX\": ").Append(Series(cell.DriftX)).Append(",\n");
            json.Append("      \"driftY\": ").Append(Series(cell.DriftY)).Append("\n");
            json.Append("    }");
            json.Append(index == cells.Count - 1 ? "\n" : ",\n");
        }
        json.Append("  ]\n}\n");
        return json.ToString();
    }

    private static string Series(List<float> values)
    {
        StringBuilder builder = new StringBuilder("[");
        for (int index = 0; index < values.Count; index += SeriesStride)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }
            builder.Append(Number(values[index]));
        }
        return builder.Append(']').ToString();
    }

    private static string Number(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
}
