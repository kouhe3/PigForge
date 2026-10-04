using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PigForge.WeldProbe.Probe
{
/// <summary>
/// Measures the ORIGINAL's own spring on its own PhysX (Unity 2021.3.45f2, the editor
/// `BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt` pins, with its own DynamicsManager/TimeManager).
/// This is the reference side of docs/specs/spring-joint.md §6: PigForge replaced the spring with a
/// "bounce pad" impulse (GameplayRules.RunSprings) and has to be rebuilt as the joint below.
///
/// Setup mirrored from the original, one line per source:
///   - path A (bungee)  : SpringJoint, ConfigureSpringJoint(0, 0, 250, 20), anchor (0,-0.5,0),
///                        breakForce = 250 (or 1200 with the IN StrongSpringConnection), preprocessing on
///                        (Spring.cs:104-118, JointExtensions.cs:5-12)
///   - path B (limit)   : ConfigurableJoint, angular all Locked, x/z Locked, yMotion Limited,
///                        configuredInWorldSpace, linearLimitSpring(250, 20), linearLimit(0.1, bounce 1),
///                        preprocessing off, breakForce = 250            (Spring.cs:119-127)
///   - body             : drag 0.2, angularDrag 0.05, 2.5D constraints 56 (BasePart.cs:1184-1196)
///   - physics          : fixed 0.02, solver 6/1, gravity -9.81, bounce threshold 2 (BPLE ProjectSettings)
/// Each cell hangs a body of known mass under a kinematic anchor through the original's own joint and
/// measures: static sag (load / k), the free oscillation frequency (sqrt(k/m)/2pi) and its decay, and
/// the force at which the joint breaks.
///
/// Driven headlessly: unity run unity/PigForge.WeldProbe -- -executeMethod
/// PigForge.WeldProbe.Probe.SpringProbe.Run
/// </summary>
public static class SpringProbe
{
    private const float FixedTimeStep = 0.02f;   // BPLE TimeManager "Fixed Timestep: 0.02"
    private const float Stiffness = 250f;        // Spring.cs:7  SPRING_LIMIT_SPRING
    private const float Damper = 20f;            // Spring.cs:9  SPRING_DAMPING
    private const float Limit = 0.1f;            // Spring.cs:11 SPRING_LIMIT
    private const float Bounciness = 1f;         // Spring.cs:13 SPRING_BOUNCINESS
    private const int Constraints25D = 56;       // freeze Z position, freeze X/Y rotation
    private const float AnchorOffsetY = 0.5f;    // Spring.cs:106 anchor (0, -0.5, 0)

    private sealed class Cell
    {
        public string Id;
        public bool Bungee;
        /// <summary>false = the original's own config (Unity's default autoConfigureConnectedAnchor),
        /// true = explicit anchors, which is the diagnostic that exposes the spring law itself.</summary>
        public bool ExplicitAnchor;
        public float Mass;
        public float BreakForce;
        public float InitialSeparation;
        public float SeparationAtRest;
        public float SagY;
        public float JointForceAtRest;
        public float EffectiveStiffness;
        public float FrequencyHz;
        public float DampingRatio;
        public float BreakAtForce = float.NaN;
        public int BreakAtStep = -1;
        public readonly List<float> Separation = new List<float>();
        public readonly List<float> Deflection = new List<float>();
    }

    public static void Run()
    {
        ApplyOriginalPhysicsSettings();

        List<Cell> cells = new List<Cell>
        {
            new Cell { Id = "bungee_auto_mass1",      Bungee = true,  Mass = 1f,   BreakForce = 1200f },
            new Cell { Id = "bungee_auto_mass2",      Bungee = true,  Mass = 2f,   BreakForce = 1200f },
            new Cell { Id = "bungee_auto_break250",   Bungee = true,  Mass = 1f,   BreakForce = 250f },
            new Cell { Id = "bungee_explicit_mass1",  Bungee = true,  ExplicitAnchor = true, Mass = 1f, BreakForce = 1200f },
            new Cell { Id = "limit_auto_mass1",       Bungee = false, Mass = 1f,   BreakForce = 1200f },
            new Cell { Id = "limit_explicit_mass1",   Bungee = false, ExplicitAnchor = true, Mass = 1f, BreakForce = 1200f },
        };

        foreach (Cell cell in cells)
        {
            Measure(cell);
        }

        string path = Path.Combine(FindRepositoryRoot(), "unity", "PigForge.WeldProbe", "replays", "spring-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, WriteJson(cells));
        Debug.Log($"spring probe -> {path}");
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

    /// <summary>Hangs a body of the cell's mass under a kinematic anchor through the original's own
    /// spring joint, holds it still until it settles (static sag), then displaces it and records the
    /// free oscillation; finally ramps the load until the joint reports a break.</summary>
    private static void Measure(Cell cell)
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }

        GameObject anchorObject = new GameObject("anchor");
        GameObject bodyObject = new GameObject("body");
        try
        {
            Rigidbody anchor = anchorObject.AddComponent<Rigidbody>();
            anchor.isKinematic = true;
            anchor.useGravity = false;
            anchorObject.transform.position = Vector3.zero;

            Rigidbody body = bodyObject.AddComponent<Rigidbody>();
            body.mass = cell.Mass;
            body.drag = 0.2f;
            body.angularDrag = 0.05f;
            body.useGravity = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.constraints = (RigidbodyConstraints)Constraints25D;
            bodyObject.transform.position = new Vector3(0f, -1f, 0f);
            bodyObject.AddComponent<BoxCollider>().size = new Vector3(0.35f, 0.2f, 0.5f);

            Joint joint = CreateOriginalJoint(anchorObject, bodyObject, cell);
            cell.InitialSeparation = Separation(bodyObject, anchorObject);

            // Settle: 5 s of pure gravity through the spring -> the static sag of the load.
            for (int step = 0; step < 250; step++)
            {
                Step();
            }

            cell.SeparationAtRest = Separation(bodyObject, anchorObject);
            cell.SagY = cell.SeparationAtRest - cell.InitialSeparation;
            cell.JointForceAtRest = joint.currentForce.magnitude;
            cell.EffectiveStiffness = cell.SagY > 0f ? cell.JointForceAtRest / cell.SagY : float.NaN;

            // Free oscillation: one step of extra downward impulse, then release, sampled EVERY
            // step (the period is ~0.4 s = 20 steps, so a coarser stride cannot see it).
            body.AddForce(new Vector3(0f, -cell.Mass * 4f, 0f), ForceMode.VelocityChange);
            float rest = cell.SeparationAtRest;
            List<float> series = new List<float>();
            for (int step = 0; step < 400; step++)
            {
                Step();
                float separation = Separation(bodyObject, anchorObject);
                series.Add(separation);
                cell.Deflection.Add(separation - rest);
            }

            cell.Separation.AddRange(series);
            cell.FrequencyHz = MeasureFrequency(series, FixedTimeStep);
            cell.DampingRatio = MeasureDampingRatio(series);

            // Break: reload and ramp a downward force until the joint goes away.
            float applied = 0f;
            for (int step = 0; step < 2000; step++)
            {
                applied += 5f;
                body.AddForce(new Vector3(0f, -applied, 0f), ForceMode.Force);
                Step();
                if (joint == null || !joint)
                {
                    cell.BreakAtStep = step;
                    cell.BreakAtForce = applied;
                    break;
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(bodyObject);
            UnityEngine.Object.DestroyImmediate(anchorObject);
        }
    }

    private static Joint CreateOriginalJoint(GameObject anchor, GameObject body, Cell cell)
    {
        if (cell.Bungee)
        {
            SpringJoint springJoint = body.AddComponent<SpringJoint>();
            springJoint.connectedBody = anchor.GetComponent<Rigidbody>();
            // JointExtensions.ConfigureSpringJoint(0, 0, 250, 20), inlined (the probe project does
            // not reference BPLE's assemblies).
            springJoint.minDistance = 0f;
            springJoint.maxDistance = 0f;
            springJoint.spring = Stiffness;
            springJoint.damper = Damper;
            springJoint.anchor = new Vector3(0f, -AnchorOffsetY, 0f);
            springJoint.breakForce = cell.BreakForce;
            springJoint.enablePreprocessing = true;
            // The original leaves autoConfigureConnectedAnchor at Unity's default (true).
            springJoint.autoConfigureConnectedAnchor = !cell.ExplicitAnchor;
            if (cell.ExplicitAnchor)
            {
                springJoint.connectedAnchor = new Vector3(0f, AnchorOffsetY, 0f);
            }

            return springJoint;
        }

        ConfigurableJoint joint = body.AddComponent<ConfigurableJoint>();
        joint.connectedBody = anchor.GetComponent<Rigidbody>();
        joint.autoConfigureConnectedAnchor = !cell.ExplicitAnchor;
        if (cell.ExplicitAnchor)
        {
            joint.connectedAnchor = new Vector3(0f, AnchorOffsetY, 0f);
        }

        joint.anchor = new Vector3(0f, -AnchorOffsetY, 0f);
        joint.angularXMotion = ConfigurableJointMotion.Locked;
        joint.angularYMotion = ConfigurableJointMotion.Locked;
        joint.angularZMotion = ConfigurableJointMotion.Locked;
        joint.xMotion = ConfigurableJointMotion.Locked;
        joint.yMotion = ConfigurableJointMotion.Limited;
        joint.zMotion = ConfigurableJointMotion.Locked;
        joint.enablePreprocessing = false;
        joint.configuredInWorldSpace = true;
        SoftJointLimitSpring limitSpring = joint.linearLimitSpring;
        limitSpring.spring = Stiffness;
        limitSpring.damper = Damper;
        joint.linearLimitSpring = limitSpring;
        SoftJointLimit limit = joint.linearLimit;
        limit.limit = Limit;
        limit.bounciness = Bounciness;
        joint.linearLimit = limit;
        joint.breakForce = cell.BreakForce;
        return joint;
    }

    /// <summary>Zero crossings of the series minus its own mean, over the number of full periods
    /// observed -> frequency, plus the logarithmic decrement of successive peaks -> damping ratio.</summary>
    private static float MeasureFrequency(List<float> series, float step)
    {
        float mean = 0f;
        for (int index = 0; index < series.Count; index++)
        {
            mean += series[index];
        }

        mean /= series.Count;
        int crossings = 0;
        int firstCrossing = -1;
        int lastCrossing = -1;
        for (int index = 1; index < series.Count; index++)
        {
            bool previousBelow = series[index - 1] < mean;
            bool currentBelow = series[index] < mean;
            if (previousBelow != currentBelow)
            {
                crossings++;
                if (firstCrossing < 0)
                {
                    firstCrossing = index;
                }

                lastCrossing = index;
            }
        }

        if (crossings < 3)
        {
            return float.NaN;
        }

        float seconds = (lastCrossing - firstCrossing) * step;
        return (crossings - 1) / 2f / seconds;
    }

    private static float MeasureDampingRatio(List<float> series)
    {
        List<float> peaks = new List<float>();
        for (int index = 1; index < series.Count - 1; index++)
        {
            if (series[index] > series[index - 1] && series[index] >= series[index + 1])
            {
                peaks.Add(series[index]);
            }
        }

        if (peaks.Count < 2)
        {
            return float.NaN;
        }

        float mean = 0f;
        for (int index = 0; index < series.Count; index++)
        {
            mean += series[index];
        }

        mean /= series.Count;
        float first = Mathf.Abs(peaks[0] - mean);
        float last = Mathf.Abs(peaks[peaks.Count - 1] - mean);
        if (first <= 0f || last <= 0f)
        {
            return float.NaN;
        }

        float decrement = Mathf.Log(first / last) / Mathf.Max(1, peaks.Count - 1);
        return decrement / Mathf.Sqrt((4f * Mathf.PI * Mathf.PI) + (decrement * decrement));
    }

    private static float Separation(GameObject body, GameObject anchor) =>
        Vector3.Distance(body.transform.position, anchor.transform.position);

    private static void Step() => Physics.Simulate(FixedTimeStep);

    private static string WriteJson(List<Cell> cells)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.spring-probe\",\n");
        json.Append("  \"probeVersion\": 1,\n");
        json.Append("  \"reference\": \"unity 2021.3.45f2, real PhysX, the original's own Spring.cs joint setup\",\n");
        json.Append("  \"physics\": {\"fixedTimeStep\": 0.02, \"solverIterations\": 6, \"solverVelocityIterations\": 1, \"gravity\": -9.81},\n");
        json.Append("  \"stiffness\": ").Append(Number(Stiffness)).Append(",\n");
        json.Append("  \"damper\": ").Append(Number(Damper)).Append(",\n");
        json.Append("  \"limit\": ").Append(Number(Limit)).Append(",\n");
        json.Append("  \"bounciness\": ").Append(Number(Bounciness)).Append(",\n");
        json.Append("  \"cells\": [\n");
        for (int index = 0; index < cells.Count; index++)
        {
            Cell cell = cells[index];
            json.Append("    {\n");
            json.Append("      \"id\": \"").Append(cell.Id).Append("\",\n");
            json.Append("      \"joint\": \"").Append(cell.Bungee ? "bungee" : "limit").Append("\",\n");
            json.Append("      \"mass\": ").Append(Number(cell.Mass)).Append(",\n");
            json.Append("      \"breakForce\": ").Append(Number(cell.BreakForce)).Append(",\n");
            json.Append("      \"expectedSag\": ").Append(Number(cell.Mass * 9.81f / Stiffness)).Append(",\n");
            json.Append("      \"expectedFrequencyHz\": ").Append(Number(Mathf.Sqrt(Stiffness / cell.Mass) / (2f * Mathf.PI))).Append(",\n");
            json.Append("      \"expectedDampingRatio\": ").Append(Number(Damper / (2f * Mathf.Sqrt(Stiffness * cell.Mass)))).Append(",\n");
            json.Append("      \"initialSeparation\": ").Append(Number(cell.InitialSeparation)).Append(",\n");
            json.Append("      \"separationAtRest\": ").Append(Number(cell.SeparationAtRest)).Append(",\n");
            json.Append("      \"sagY\": ").Append(Number(cell.SagY)).Append(",\n");
            json.Append("      \"jointForceAtRest\": ").Append(Number(cell.JointForceAtRest)).Append(",\n");
            json.Append("      \"effectiveStiffness\": ").Append(Number(cell.EffectiveStiffness)).Append(",\n");
            json.Append("      \"frequencyHz\": ").Append(Number(cell.FrequencyHz)).Append(",\n");
            json.Append("      \"dampingRatio\": ").Append(Number(cell.DampingRatio)).Append(",\n");
            json.Append("      \"breakAtStep\": ").Append(cell.BreakAtStep).Append(",\n");
            json.Append("      \"breakAtForce\": ").Append(float.IsNaN(cell.BreakAtForce) ? "null" : Number(cell.BreakAtForce)).Append(",\n");
            json.Append("      \"separation\": ").Append(Series(cell.Separation, 1)).Append(",\n");
            json.Append("      \"deflection\": ").Append(Series(cell.Deflection, 1)).Append("\n");
            json.Append("    }");
            json.Append(index == cells.Count - 1 ? "\n" : ",\n");
        }
        json.Append("  ]\n}\n");
        return json.ToString();
    }

    private static string Series(List<float> values, int stride)
    {
        StringBuilder builder = new StringBuilder("[");
        for (int index = 0; index < values.Count; index += stride)
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
        float.IsNaN(value) ? "null" : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(Application.dataPath);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new InvalidOperationException("Could not locate content/parts.json above the probe project.");
        }

        return directory.FullName;
    }
}
}
