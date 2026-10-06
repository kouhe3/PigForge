using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PigForge.WeldProbe.Probe
{
/// <summary>
/// Measures the ORIGINAL's rocket burn on its own PhysX (Unity 2021.3.45f2, the editor
/// `BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt` pins, with its own DynamicsManager/TimeManager).
/// This is the reference side of docs/specs/play-part-switches.md and ADR-029 decision 3: PigForge's
/// rocket content is still hand-written (thrustPerTick 4/6/8, durationTicks 60/30) while the prefab
/// truth is m_boostForce 50 / m_ignitionTime 1 / m_boostDuration 3 / m_boostEndDuration 1 /
/// m_maximumSpeed 18 (Part_Rocket_01_SET, tasks/bple-rockets-report.json).
///
/// Transcribed verbatim, one line per source:
///   fields       Rocket.cs:9-30            m_boostForce 50, m_ignitionTime 1, m_boostDuration 3,
///                                          m_boostEndDuration 1, m_maximumSpeed 18, m_direction (1,0,0)
///   FixedUpdate  Rocket.cs:228-300         three phases over num = Time.time - m_timeBoostStarted:
///                                          num2 = 1 until num > ignition+boost, then
///                                          num2 = 1 - (num - boost - ignition)/end; the force is
///                                          LimitForceForSpeed(num2 * m_boostForce, dir) handed to
///                                          AddForceAtPosition(force * dir, transform.position,
///                                          ForceMode.Force) with dir = transform.TransformDirection(m_direction);
///                                          past ignition+boost+end the part sets m_enabled = false (and the
///                                          force is applied once more on that very step, because the top-of-method
///                                          guard is what stops the NEXT FixedUpdate).
///   cap          Rocket.cs:529-541         LimitForceForSpeed verbatim (the speed cap): when the velocity
///                                          projected on dir exceeds m_maximumSpeed the force is divided by
///                                          (1 + |v_proj| - m_maximumSpeed).
///   bottle gate  Rocket.cs:236-240         while num < m_ignitionTime and the prefab carries an
///                                          m_visualization (the Coke/Soda bottle family; the plain
///                                          Part_Rocket_01_SET prefab has no m_visualization) NO force is
///                                          applied at all -- measured as the second configuration.
///
/// The probe drives a bare GameObject (Rigidbody mass 1, no drag, 2.5D constraints 56 as
/// BasePart.cs:1194-1196, gravity as the project's DynamicsManager has it), auto-simulation off and
/// exactly one Physics.Simulate(0.02f) per transcribed FixedUpdate (the project's own TimeManager step).
/// `num` is the probe clock: step * 0.02 s, with m_timeBoostStarted = 0.
///
/// Part 1 force curve: 300 steps (6 s, past the 5 s burn end) -> per step num, the per-second curve force
///        num2 * m_boostForce, the applied (capped) force, and the measured thrust mass * dv / 0.02.
/// Part 2 speed cap:   the same one-step measurement at pre-set speeds 0/5/20/40 along dir, with the
///        factor 1/(1 + v - maxSpeed) made explicit.
/// Part 3 bottle gate: ignitionSuppressesThrust = true, 90 steps, the first 70 reported rows (the first
///        50 must be zero, the 51st onward full).
/// Part 4 cross-check:  a SEPARATE function (PredictCurveFromNumbers/PredictAppliedFromNumbers) that
///        computes the three-phase curve and the cap from the numbers alone, no PhysX and no code shared
///        with the transcribed Rocket, so a transcription slip shows up as the probe's own two
///        computations disagreeing. The per-row disagreements and their maxima are reported.
///
/// For the record the JSON carries the original's own one-shot semantics: Rocket.OnTouch's
/// `if (m_boostUsed) return;` plus ChangeOneShotPartAmount(..., -1) under the vanilla declaration
/// default SwitchableCokeSodaRocket = false (Rocket.cs:570-581, INDeclarationSettings{Exp}.json), and
/// Explode() (Rocket.cs:627-646) which pushes bodies/TNT around and never destroys the rocket part.
///
/// Output: replays/rocket-burn-probe.json.
/// Driven headlessly:
///   unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1800 \
///     -- -executeMethod PigForge.WeldProbe.Probe.RocketBurnProbe.Run -logFile -
/// </summary>
public static class RocketBurnProbe
{
    private const float FixedTimeStep = 0.02f;   // BPLE TimeManager "Fixed Timestep: 0.02"
    private const int Constraints25D = 56;       // freeze Z position + freeze X/Y rotation (BasePart.cs:1194-1196)
    private const float BodyMass = 1f;           // the probe body's mass (kg)
    private const int ForceCurveSteps = 300;     // 50 Hz x 6 s, past ignition(1)+boost(3)+end(1) = 5 s
    private const int BottleSteps = 90;
    private const int BottleReportedSteps = 70;
    private const float WingClampNewtons = 100f; // Wings.cs:115 / Tail.cs:73 -- NOT a Rocket constant

    // Part_Rocket_01_SET serialized values (tasks/bple-rockets-report.json; defaults Rocket.cs:9-30).
    private const float BoostForce = 50f;
    private const float IgnitionTime = 1f;
    private const float BoostDuration = 3f;
    private const float BoostEndDuration = 1f;
    private const float MaximumSpeed = 18f;
    private static readonly Vector3 Direction = new Vector3(1f, 0f, 0f);

    private static readonly float[] SpeedCapVelocities = { 0f, 5f, 20f, 40f };

    /// <summary>Rocket.cs:266-269 sets m_enabled = false on the first step past the burn end, and
    /// Rocket.cs:229-234 returns on every step after, so the force appears once more on step 251 then
    /// never again. Derived here from the constants alone (5 / 0.02 + 1 = 251), not from the transcribed
    /// code.</summary>
    private static readonly int FirstStepPastBurnEnd =
        Mathf.FloorToInt((IgnitionTime + BoostDuration + BoostEndDuration) / FixedTimeStep) + 1;

    [MenuItem("PigForge/WeldProbe/Rocket Burn Probe")]
    public static void Run()
    {
        ApplyOriginalPhysicsSettings();

        Rocket rocket = new Rocket();

        // Part 1: the plain rocket's force curve, 300 steps (6 s).
        List<BurnStep> curve = RunBurn(rocket, false, ForceCurveSteps, Vector3.zero, "forceCurve");

        // Part 2: the speed cap, one step at each pre-set speed along dir.
        List<BurnStep> speedCap = new List<BurnStep>();
        foreach (float speed in SpeedCapVelocities)
        {
            List<BurnStep> row = RunBurn(rocket, false, 1, Direction * speed, "speedCap" + speed.ToString("0.##", CultureInfo.InvariantCulture));
            speedCap.Add(row[0]);
        }

        // Part 3: the bottle configuration (m_visualization present -> the ignition window is silent).
        List<BurnStep> bottle = RunBurn(rocket, true, BottleSteps, Vector3.zero, "bottleIgnition");

        foreach (BurnStep row in curve)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[rocket-burn] curve step={0} num={1:0.####} num2={2:0.####} curveN={3:0.######} appliedN={4:0.######} measuredN={5:0.######} suppressed={6} stopped={7}",
                row.Step, row.Num, row.ForceScale, row.CurveForceNewtons, row.AppliedForceNewtons, row.MeasuredForceNewtons,
                row.Suppressed, row.ThrustStopped));
        }

        foreach (BurnStep row in speedCap)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[rocket-burn] speedCap v={0:0.##} num={1:0.####} appliedN={2:0.######} measuredN={3:0.######} appliedFactor={4:0.######} formulaFactor={5:0.######}",
                row.InitialSpeed, row.Num, row.AppliedForceNewtons, row.MeasuredForceNewtons, row.AppliedFactor, row.ExpectedCapFactor));
        }

        for (int index = 0; index < BottleReportedSteps && index < bottle.Count; index++)
        {
            BurnStep row = bottle[index];
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[rocket-burn] bottle step={0} num={1:0.####} suppressed={2} curveN={3:0.######} appliedN={4:0.######} measuredN={5:0.######}",
                row.Step, row.Num, row.Suppressed, row.CurveForceNewtons, row.AppliedForceNewtons, row.MeasuredForceNewtons));
        }

        string path = Path.Combine(FindRepositoryRoot(), "unity", "PigForge.WeldProbe", "replays", "rocket-burn-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, WriteJson(curve, speedCap, bottle), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log("[rocket-burn] wrote " + path);

        EditorApplication.Exit(0);
    }

    // =======================================================================================
    // The transcribed original: Rocket.cs:9-30 fields, Rocket.cs:228-300 FixedUpdate,
    // Rocket.cs:236-240 bottle gate and Rocket.cs:529-541 LimitForceForSpeed.
    // =======================================================================================

    private sealed class Rocket
    {
        // Rocket.cs:9-30. Field defaults where the prefab does not override: the prefab
        // (Part_Rocket_01_SET) serializes m_direction (1,0,0), NOT the class default Vector3.up.
        public float m_boostForce = BoostForce;
        public float m_ignitionTime = IgnitionTime;
        public float m_boostDuration = BoostDuration;
        public float m_boostEndDuration = BoostEndDuration;
        public float m_maximumSpeed = MaximumSpeed;
        public Vector3 m_direction = Direction;
        public bool m_enabled;
        public bool m_boostUsed;
        public bool m_visualizationPresent;   // Rocket.cs:68-83 binds "BottleVisualization" from a child
        public float m_timeBoostStarted;

        // Probe-only observation hooks, assigned next to the original's own locals.
        public float LastNum2;
        public float LastCurveForce;
        public float LastAppliedForce;
        public bool LastSuppressed;
        public bool LastThrustStopped;
        public bool LastEnabled;

        /// <summary>Starts the burn the way Rocket.OnTouch does (Rocket.cs:570-581): enable the part,
        /// stamp the start time and take the one-shot.</summary>
        public void Begin(bool bottle)
        {
            m_enabled = true;
            m_timeBoostStarted = 0f;
            m_boostUsed = true;
            m_visualizationPresent = bottle;
        }

        /// <summary>
        /// Rocket.cs:228-300 verbatim for the vanilla SwitchableCokeSodaRocket = false branch, minus the
        /// particle/audio/visualisation side effects (the probe has no such objects). The two INSettings
        /// gates are constants here: flag = false, so `!flag` is always true.
        /// </summary>
        public void FixedUpdate(Rigidbody rigidbody, Transform transform, float time)
        {
            LastNum2 = 0f;
            LastCurveForce = 0f;
            LastAppliedForce = 0f;
            LastSuppressed = false;
            LastThrustStopped = false;

            if (!m_enabled)
            {
                // Rocket.cs:229-234
                LastEnabled = m_enabled;
                return;
            }

            float num = time - m_timeBoostStarted;                 // Rocket.cs:235
            if (num < m_ignitionTime && m_visualizationPresent)    // Rocket.cs:236-240 (flag == false -> return)
            {
                LastSuppressed = true;
                LastEnabled = m_enabled;
                return;
            }

            if (num > m_ignitionTime + m_boostDuration + m_boostEndDuration)   // Rocket.cs:266-269
            {
                // m_explodes is 0 for Part_Rocket_01_SET, so no Explode(); the particle teardown is
                // side-effect only. The force below is still applied on this same step.
                m_enabled = false;
                LastThrustStopped = true;
            }

            float num2 = 1f;                                        // Rocket.cs:270-275
            if (num > m_ignitionTime + m_boostDuration)
            {
                num2 = 1f - (num - m_boostDuration - m_ignitionTime) / m_boostEndDuration;
            }

            // Rocket.cs:287-295 (the !num3 && !flag2 branch; Avoidance/Tracking IN features are off)
            Vector3 zero = Vector3.zero;
            Vector3 position = transform.position + zero * 0.5f;
            Vector3 vector = transform.TransformDirection(m_direction);
            float num4 = LimitForceForSpeed(rigidbody, num2 * m_boostForce, vector);
            rigidbody.AddForceAtPosition(num4 * vector, position, ForceMode.Force);

            LastNum2 = num2;
            LastCurveForce = num2 * m_boostForce;
            LastAppliedForce = num4;
            LastEnabled = m_enabled;
        }

        /// <summary>Rocket.cs:529-541 verbatim (base.rigidbody is passed in).</summary>
        private float LimitForceForSpeed(Rigidbody rigidbody, float forceMagnitude, Vector3 forceDir)
        {
            Vector3 velocity = rigidbody.velocity;
            float num = Vector3.Dot(velocity.normalized, forceDir);
            if (num > 0f)
            {
                Vector3 vector = velocity * num;
                if (vector.magnitude > m_maximumSpeed)
                {
                    return forceMagnitude / (1f + vector.magnitude - m_maximumSpeed);
                }
            }
            return forceMagnitude;
        }
    }

    // =======================================================================================
    // One measurement = one body, one transcribed FixedUpdate, one Physics.Simulate.
    // =======================================================================================

    private sealed class BurnStep
    {
        public string Label;
        public int Step;
        public float Num;
        public float InitialSpeed;              // part 2 only: the pre-set speed along dir
        public float ForceScale;                // num2 as transcribed
        public float CurveForceNewtons;         // num2 * m_boostForce (transcribed, before the cap)
        public float AppliedForceNewtons;       // LimitForceForSpeed output (transcribed, what was applied)
        public float AppliedFactor;             // AppliedForceNewtons / CurveForceNewtons (1 when uncapped)
        public float ExpectedCapFactor;         // 1/(1 + v - maxSpeed), the arithmetic form from the spec
        public bool Suppressed;                 // bottle ignition window: no force at all
        public bool ThrustStopped;              // m_enabled flipped false on this step
        public bool EnabledAfter;
        public float IndependentCurveNewtons;   // part 4, numbers alone, no PhysX
        public float IndependentAppliedNewtons; // part 4, numbers alone including the cap
        public Vector3 VelocityBefore;
        public Vector3 VelocityAfter;
        public Vector3 DeltaVelocity;
        public float MeasuredForceNewtons;          // mass * (dv . dir) / dt -- the thrust along dir
        public Vector3 MeasuredForceVectorNewtons;  // mass * dv / dt (gravity contributes in Y)
        public float CurveVsMeasuredNewtons;
        public float IndependentAppliedVsTranscribedNewtons;
        public float IndependentAppliedVsMeasuredNewtons;
        public float MeasuredVsTranscribedAppliedNewtons;
    }

    private static List<BurnStep> RunBurn(Rocket rocket, bool bottle, int steps, Vector3 initialVelocity, string label)
    {
        ClearScene();

        GameObject probe = new GameObject("RocketBurnBody_" + label);
        probe.transform.position = Vector3.zero;
        probe.transform.rotation = Quaternion.identity;

        Rigidbody body = probe.AddComponent<Rigidbody>();
        body.mass = BodyMass;
        body.drag = 0f;                                                    // "no drag"
        body.angularDrag = 0f;
        body.useGravity = true;                                            // gravity as DynamicsManager has it
        body.interpolation = RigidbodyInterpolation.None;                  // read the raw simulated velocity
        body.constraints = (RigidbodyConstraints)Constraints25D;           // BasePart.cs:1194-1196
        body.isKinematic = false;
        body.velocity = initialVelocity;

        rocket.Begin(bottle);
        Vector3 dir = probe.transform.TransformDirection(rocket.m_direction);

        List<BurnStep> rows = new List<BurnStep>();
        for (int step = 0; step < steps; step++)
        {
            Vector3 before = body.velocity;
            float num = step * FixedTimeStep;                              // num = Time.time - m_timeBoostStarted, m_timeBoostStarted = 0
            rocket.FixedUpdate(body, probe.transform, num);
            Physics.Simulate(FixedTimeStep);
            Vector3 after = body.velocity;
            Vector3 delta = after - before;

            BurnStep row = new BurnStep
            {
                Label = label,
                Step = step,
                Num = num,
                InitialSpeed = Vector3.Dot(initialVelocity, dir),
                ForceScale = rocket.LastNum2,
                CurveForceNewtons = rocket.LastCurveForce,
                AppliedForceNewtons = rocket.LastAppliedForce,
                AppliedFactor = rocket.LastCurveForce > 0f ? rocket.LastAppliedForce / rocket.LastCurveForce : 1f,
                ExpectedCapFactor = ExpectedCapFactor(velocity: initialVelocity, dir: dir),
                Suppressed = rocket.LastSuppressed,
                ThrustStopped = rocket.LastThrustStopped,
                EnabledAfter = rocket.LastEnabled,
                VelocityBefore = before,
                VelocityAfter = after,
                DeltaVelocity = delta,
                MeasuredForceNewtons = Vector3.Dot(delta, dir) * (BodyMass / FixedTimeStep),
                MeasuredForceVectorNewtons = delta * (BodyMass / FixedTimeStep),
            };

            // Part 4: the same curve recomputed from the numbers alone, zero code shared with Rocket above.
            row.IndependentCurveNewtons = PredictCurveFromNumbers(num, step, bottle);
            row.IndependentAppliedNewtons = PredictAppliedFromNumbers(num, step, before, dir, bottle);
            row.CurveVsMeasuredNewtons = Mathf.Abs(row.CurveForceNewtons - row.MeasuredForceNewtons);
            row.IndependentAppliedVsTranscribedNewtons = Mathf.Abs(row.IndependentAppliedNewtons - row.AppliedForceNewtons);
            row.IndependentAppliedVsMeasuredNewtons = Mathf.Abs(row.IndependentAppliedNewtons - row.MeasuredForceNewtons);
            row.MeasuredVsTranscribedAppliedNewtons = Mathf.Abs(row.MeasuredForceNewtons - row.AppliedForceNewtons);
            rows.Add(row);
        }

        UnityEngine.Object.DestroyImmediate(probe);
        return rows;
    }

    /// <summary>1/(1 + v_proj - maxSpeed) if v_proj > maxSpeed, else 1 -- the factor the cap divides by.</summary>
    private static float ExpectedCapFactor(Vector3 velocity, Vector3 dir)
    {
        float speed = Vector3.Dot(velocity, dir);
        return speed > MaximumSpeed ? 1f / (1f + speed - MaximumSpeed) : 1f;
    }

    // =======================================================================================
    // Part 4: the same arithmetic from the numbers alone.
    // =======================================================================================

    /// <summary>Rocket.cs:270-275 from the numbers alone: 1 until num > ignition+boost, then the linear
    /// ramp; the bottle gate of :236-240 is folded in as the caller's `bottle` flag.</summary>
    private static float PredictCurveFromNumbers(float num, int step, bool bottle)
    {
        if (bottle && num < IgnitionTime)
        {
            return 0f;
        }

        if (step > FirstStepPastBurnEnd)
        {
            return 0f;
        }

        float num2 = 1f;
        if (num > IgnitionTime + BoostDuration)
        {
            num2 = 1f - (num - BoostDuration - IgnitionTime) / BoostEndDuration;
        }
        return num2 * BoostForce;
    }

    private static float PredictAppliedFromNumbers(float num, int step, Vector3 velocityBefore, Vector3 dir, bool bottle)
    {
        float forceMagnitude = PredictCurveFromNumbers(num, step, bottle);
        float dot = Vector3.Dot(velocityBefore.normalized, dir);
        if (dot > 0f)
        {
            Vector3 projection = velocityBefore * dot;
            if (projection.magnitude > MaximumSpeed)
            {
                return forceMagnitude / (1f + projection.magnitude - MaximumSpeed);
            }
        }
        return forceMagnitude;
    }

    // =======================================================================================
    // Physics settings and helpers.
    // =======================================================================================

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
        Time.fixedDeltaTime = FixedTimeStep;
        Time.maximumDeltaTime = 0.05f;
#if UNITY_6000_0_OR_NEWER
        Physics.simulationMode = SimulationMode.Script;
#else
        // Unity 2021 has no Physics.simulationMode yet: autoSimulation = false plus an explicit
        // Physics.Simulate per step is the same thing, and it is what the sibling probes measure on.
        Physics.autoSimulation = false;
#endif
    }

    private static void ClearScene()
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }
    }

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

    // =======================================================================================
    // JSON.
    // =======================================================================================

    private static string WriteJson(List<BurnStep> curve, List<BurnStep> speedCap, List<BurnStep> bottle)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.weldprobe.rocket-burn\",\n");
        json.Append("  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
        json.Append("  \"physics\": {\n");
        json.Append("    \"fixedTimeStep\": ").Append(N(FixedTimeStep)).Append(",\n");
        json.Append("    \"autoSimulation\": false,\n");
        json.Append("    \"gravity\": ").Append(V(Physics.gravity)).Append(",\n");
        json.Append("    \"bodyUseGravity\": true,\n");
        json.Append("    \"bodyMass\": ").Append(N(BodyMass)).Append(",\n");
        json.Append("    \"bodyDrag\": 0,\n");
        json.Append("    \"bodyAngularDrag\": 0,\n");
        json.Append("    \"bodyConstraints\": ").Append(Constraints25D).Append(",\n");
        json.Append("    \"bodyInterpolation\": \"None\",\n");
        json.Append("    \"bodyOrigin\": [0, 0, 0],\n");
        json.Append("    \"simulateDt\": ").Append(N(FixedTimeStep)).Append("\n");
        json.Append("  },\n");
        json.Append("  \"originalPrefab\": {\n");
        json.Append("    \"name\": \"Part_Rocket_01_SET\",\n");
        json.Append("    \"m_boostForce\": ").Append(N(BoostForce)).Append(",\n");
        json.Append("    \"m_ignitionTime\": ").Append(N(IgnitionTime)).Append(",\n");
        json.Append("    \"m_boostDuration\": ").Append(N(BoostDuration)).Append(",\n");
        json.Append("    \"m_boostEndDuration\": ").Append(N(BoostEndDuration)).Append(",\n");
        json.Append("    \"m_maximumSpeed\": ").Append(N(MaximumSpeed)).Append(",\n");
        json.Append("    \"m_direction\": ").Append(V(Direction)).Append(",\n");
        json.Append("    \"m_explodes\": 0,\n");
        json.Append("    \"hasVisualization\": false,\n");
        json.Append("    \"burnEndsAtSeconds\": ").Append(N(IgnitionTime + BoostDuration + BoostEndDuration)).Append("\n");
        json.Append("  },\n");
        json.Append("  \"notAppliedWingClampNewtons\": ").Append(N(WingClampNewtons)).Append(",\n");
        json.Append("  \"notAppliedWingClampNote\": \"Wings.cs:115 / Tail.cs:73 Vector3.ClampMagnitude(lift, 100f). It is a wing/tail constant and is NOT used by Rocket; m_boostForce 50 is under it, so even if it were it could not bite.\",\n");

        // ---- Part 1 ----
        json.Append("  \"forceCurve\": {\n");
        json.Append("    \"steps\": [\n");
        for (int index = 0; index < curve.Count; index++)
        {
            AppendStep(json, curve[index], "      ", true);
            json.Append(index == curve.Count - 1 ? "\n" : ",\n");
        }
        json.Append("    ],\n");
        json.Append("    \"summary\": ").Append(Summary(curve)).Append("\n");
        json.Append("  },\n");

        // ---- Part 2 ----
        json.Append("  \"speedCap\": {\n");
        json.Append("    \"velocities\": [");
        for (int index = 0; index < SpeedCapVelocities.Length; index++)
        {
            json.Append(index > 0 ? ", " : string.Empty).Append(N(SpeedCapVelocities[index]));
        }
        json.Append("],\n");
        json.Append("    \"rows\": [\n");
        for (int index = 0; index < speedCap.Count; index++)
        {
            AppendStep(json, speedCap[index], "      ", true);
            json.Append(index == speedCap.Count - 1 ? "\n" : ",\n");
        }
        json.Append("    ],\n");
        json.Append("    \"summary\": ").Append(Summary(speedCap)).Append("\n");
        json.Append("  },\n");

        // ---- Part 3 ----
        json.Append("  \"bottleIgnition\": {\n");
        json.Append("    \"ignitionSuppressesThrust\": true,\n");
        json.Append("    \"totalSteps\": ").Append(bottle.Count).Append(",\n");
        json.Append("    \"reportedSteps\": ").Append(Mathf.Min(BottleReportedSteps, bottle.Count)).Append(",\n");
        json.Append("    \"rows\": [\n");
        int reported = Mathf.Min(BottleReportedSteps, bottle.Count);
        for (int index = 0; index < reported; index++)
        {
            AppendStep(json, bottle[index], "      ", true);
            json.Append(index == reported - 1 ? "\n" : ",\n");
        }
        json.Append("    ],\n");

        int firstUnsuppressedStep = -1;
        float firstUnsuppressedForce = 0f;
        bool allSuppressedRowsSilent = true;
        for (int index = 0; index < bottle.Count; index++)
        {
            if (bottle[index].Suppressed && (bottle[index].AppliedForceNewtons != 0f || bottle[index].MeasuredForceNewtons != 0f))
            {
                allSuppressedRowsSilent = false;
            }
            if (firstUnsuppressedStep < 0 && !bottle[index].Suppressed)
            {
                firstUnsuppressedStep = bottle[index].Step;
                firstUnsuppressedForce = bottle[index].AppliedForceNewtons;
            }
        }

        json.Append("    \"summary\": {\n");
        json.Append("      \"firstUnsuppressedStep\": ").Append(firstUnsuppressedStep).Append(",\n");
        json.Append("      \"firstUnsuppressedForceNewtons\": ").Append(NP(firstUnsuppressedForce)).Append(",\n");
        json.Append("      \"suppressedRowCount\": ").Append(firstUnsuppressedStep).Append(",\n");
        json.Append("      \"allSuppressedRowsHaveZeroForce\": ").Append(Bool(allSuppressedRowsSilent)).Append(",\n");
        json.Append("      \"bottleSummary\": ").Append(Summary(bottle)).Append("\n");
        json.Append("    }\n");
        json.Append("  },\n");

        // ---- Part 4 ----
        json.Append("  \"crossCheck\": {\n");
        json.Append("    \"maxCurveVsMeasuredNewtons\": ").Append(NP(Max(curve, row => row.CurveVsMeasuredNewtons))).Append(",\n");
        json.Append("    \"maxIndependentAppliedVsTranscribedNewtons\": ").Append(NP(Max(curve, row => row.IndependentAppliedVsTranscribedNewtons))).Append(",\n");
        json.Append("    \"maxIndependentAppliedVsMeasuredNewtons\": ").Append(NP(Max(curve, row => row.IndependentAppliedVsMeasuredNewtons))).Append(",\n");
        json.Append("    \"maxMeasuredVsTranscribedAppliedNewtons\": ").Append(NP(Max(curve, row => row.MeasuredVsTranscribedAppliedNewtons))).Append(",\n");
        json.Append("    \"firstStepWhereCapEngages\": ").Append(FirstCapStep(curve)).Append(",\n");
        json.Append("    \"curveMatchesMeasuredEveryStep\": ").Append(Bool(Max(curve, row => row.CurveVsMeasuredNewtons) <= 1e-4f)).Append(",\n");
        json.Append("    \"appliedMatchesMeasuredEveryStep\": ").Append(Bool(Max(curve, row => row.MeasuredVsTranscribedAppliedNewtons) <= 1e-4f)).Append(",\n");
        json.Append("    \"independentAppliedMatchesTranscribedEveryStep\": ").Append(Bool(Max(curve, row => row.IndependentAppliedVsTranscribedNewtons) <= 1e-6f)).Append("\n");
        json.Append("  },\n");

        // ---- The record ----
        json.Append("  \"oneShot\": {\n");
        json.Append("    \"m_boostUsedGuard\": \"Rocket.cs:570-581 (Rocket.OnTouch, else branch): `if (m_boostUsed) return;` then `m_enabled = !m_enabled; m_particlesIgnitionInstance.Play(); m_timeBoostStarted = Time.time; m_boostUsed = true; base.contraption.ChangeOneShotPartAmount(m_partType, EffectDirection(), -1);` -- with the vanilla SwitchableCokeSodaRocket = false the part fires exactly once in its lifetime and consumes one one-shot part.\",\n");
        json.Append("    \"switchableCokeSodaRocketDefault\": false,\n");
        json.Append("    \"switchableCokeSodaRocketSource\": \"INDeclarationSettings.json and INDeclarationSettingsExp.json: {\\\"name\\\": \\\"SwitchableCokeSodaRocket\\\", \\\"type\\\": \\\"Boolean\\\", \\\"value\\\": false}.\",\n");
        json.Append("    \"boostIsOneShot\": true,\n");
        json.Append("    \"explodeNeverDestroysPart\": \"Rocket.cs:627-646 (Explode): OverlapSphere + AddExplosionForce on other bodies, TNT.Explode() for nearby TNT, smoke/audio and ShineExplosionLight. It never calls Destroy on the rocket part itself; and Part_Rocket_01_SET has m_explodes = 0, so FixedUpdate (:266-269) does not even call it.\",\n");
        json.Append("    \"partDestroyedByExplode\": false\n");
        json.Append("  },\n");

        json.Append("  \"notes\": [\n");
        json.Append("    \"Fields, FixedUpdate and LimitForceForSpeed are transcribed verbatim from the original: Rocket.cs:9-30, :228-300, :236-240 and :529-541. Only the particle/audio/visualisation side effects and the two INSettings feature lookups (constants on the vanilla profile: SwitchableCokeSodaRocket = false, Avoidance/Tracking unused) are dropped; the force math is untouched. Probe-only observation hooks are assigned next to the original's own locals.\",\n");
        json.Append("    \"num is the probe clock step * 0.02 s with m_timeBoostStarted = 0, so num = Time.time - m_timeBoostStarted exactly as :235 computes it: step 0 -> num 0, and the bottle's first 50 rows (num 0..0.98) hit the :236-240 early return.\",\n");
        json.Append("    \"measuredForceNewtons = mass * (velocityAfter - velocityBefore) . dir / 0.02 is the per-second thrust along dir (ADR-013 decision 4: the original ticks at 50 Hz, PigForge at 60 Hz, so the newton value is the tick-independent quantity). measuredForceVectorNewtons keeps the full vector; its Y component is the project gravity (-9.81).\",\n");
        json.Append("    \"appliedForceNewtons is LimitForceForSpeed(num2 * m_boostForce, dir) -- the capped number the transcribed FixedUpdate handed to AddForceAtPosition, which is what a rigidbody actually receives. curveForceNewtons is num2 * m_boostForce before the cap, so the phases are visible.\",\n");
        json.Append("    \"crossCheck recomputes the same three-phase curve and the same cap from the numbers alone in PredictCurveFromNumbers/PredictAppliedFromNumbers, sharing no code with the transcribed Rocket; independentAppliedVsTranscribedNewtons is the transcription check (0 if the probe transcribed correctly), curveVsMeasuredNewtons exposes where the speed cap (not a transcription slip) makes the raw curve diverge from the rigidbody.\",\n");
        json.Append("    \"The 100 N ClampMagnitude is a Wings.cs/Tail.cs constant only; Rocket has no such clamp, so it never engages here.\"\n");
        json.Append("  ]\n");
        json.Append("}\n");
        return json.ToString();
    }

    private static void AppendStep(StringBuilder json, BurnStep row, string indent, bool includeVelocity)
    {
        json.Append(indent).Append("{\n");
        json.Append(indent).Append("  \"label\": \"").Append(row.Label).Append("\",\n");
        json.Append(indent).Append("  \"step\": ").Append(row.Step).Append(",\n");
        json.Append(indent).Append("  \"num\": ").Append(NP(row.Num)).Append(",\n");
        json.Append(indent).Append("  \"initialSpeed\": ").Append(NP(row.InitialSpeed)).Append(",\n");
        json.Append(indent).Append("  \"forceScale\": ").Append(NP(row.ForceScale)).Append(",\n");
        json.Append(indent).Append("  \"curveForceNewtons\": ").Append(NP(row.CurveForceNewtons)).Append(",\n");
        json.Append(indent).Append("  \"appliedForceNewtons\": ").Append(NP(row.AppliedForceNewtons)).Append(",\n");
        json.Append(indent).Append("  \"appliedFactor\": ").Append(NP(row.AppliedFactor)).Append(",\n");
        json.Append(indent).Append("  \"expectedCapFactor\": ").Append(NP(row.ExpectedCapFactor)).Append(",\n");
        json.Append(indent).Append("  \"suppressed\": ").Append(Bool(row.Suppressed)).Append(",\n");
        json.Append(indent).Append("  \"thrustStopped\": ").Append(Bool(row.ThrustStopped)).Append(",\n");
        json.Append(indent).Append("  \"enabledAfter\": ").Append(Bool(row.EnabledAfter)).Append(",\n");
        json.Append(indent).Append("  \"independentCurveNewtons\": ").Append(NP(row.IndependentCurveNewtons)).Append(",\n");
        json.Append(indent).Append("  \"independentAppliedNewtons\": ").Append(NP(row.IndependentAppliedNewtons)).Append(",\n");
        if (includeVelocity)
        {
            json.Append(indent).Append("  \"velocityBefore\": ").Append(V(row.VelocityBefore)).Append(",\n");
            json.Append(indent).Append("  \"velocityAfter\": ").Append(V(row.VelocityAfter)).Append(",\n");
            json.Append(indent).Append("  \"deltaVelocity\": ").Append(V(row.DeltaVelocity)).Append(",\n");
            json.Append(indent).Append("  \"measuredForceNewtons\": ").Append(NP(row.MeasuredForceNewtons)).Append(",\n");
            json.Append(indent).Append("  \"measuredForceVectorNewtons\": ").Append(V(row.MeasuredForceVectorNewtons)).Append(",\n");
        }
        json.Append(indent).Append("  \"curveVsMeasuredNewtons\": ").Append(NP(row.CurveVsMeasuredNewtons)).Append(",\n");
        json.Append(indent).Append("  \"independentAppliedVsTranscribedNewtons\": ").Append(NP(row.IndependentAppliedVsTranscribedNewtons)).Append(",\n");
        json.Append(indent).Append("  \"independentAppliedVsMeasuredNewtons\": ").Append(NP(row.IndependentAppliedVsMeasuredNewtons)).Append(",\n");
        json.Append(indent).Append("  \"measuredVsTranscribedAppliedNewtons\": ").Append(NP(row.MeasuredVsTranscribedAppliedNewtons)).Append("\n");
        json.Append(indent).Append("}");
    }

    private static string Summary(List<BurnStep> rows)
    {
        float maxCurveVsMeasured = 0f;
        float maxIndependentVsMeasured = 0f;
        float maxMeasuredVsTranscribed = 0f;
        int fullThrustSteps = 0;
        int rampSteps = 0;
        int suppressedSteps = 0;
        int stoppedSteps = 0;
        int zeroForceSteps = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            BurnStep row = rows[index];
            maxCurveVsMeasured = Mathf.Max(maxCurveVsMeasured, row.CurveVsMeasuredNewtons);
            maxIndependentVsMeasured = Mathf.Max(maxIndependentVsMeasured, row.IndependentAppliedVsMeasuredNewtons);
            maxMeasuredVsTranscribed = Mathf.Max(maxMeasuredVsTranscribed, row.MeasuredVsTranscribedAppliedNewtons);
            if (row.Suppressed)
            {
                suppressedSteps++;
            }
            if (row.ThrustStopped)
            {
                stoppedSteps++;
            }
            if (Mathf.Abs(row.ForceScale - 1f) < 1e-6f)
            {
                fullThrustSteps++;
            }
            else if (row.ForceScale > 0f && row.ForceScale < 1f)
            {
                rampSteps++;
            }
            if (row.AppliedForceNewtons == 0f)
            {
                zeroForceSteps++;
            }
        }

        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("      \"rowCount\": ").Append(rows.Count).Append(",\n");
        json.Append("      \"fullThrustSteps\": ").Append(fullThrustSteps).Append(",\n");
        json.Append("      \"rampSteps\": ").Append(rampSteps).Append(",\n");
        json.Append("      \"suppressedSteps\": ").Append(suppressedSteps).Append(",\n");
        json.Append("      \"thrustStoppedSteps\": ").Append(stoppedSteps).Append(",\n");
        json.Append("      \"zeroAppliedForceSteps\": ").Append(zeroForceSteps).Append(",\n");
        json.Append("      \"firstCapEngagementStep\": ").Append(FirstCapStep(rows)).Append(",\n");
        json.Append("      \"maxCurveVsMeasuredNewtons\": ").Append(NP(maxCurveVsMeasured)).Append(",\n");
        json.Append("      \"maxIndependentAppliedVsMeasuredNewtons\": ").Append(NP(maxIndependentVsMeasured)).Append(",\n");
        json.Append("      \"maxMeasuredVsTranscribedAppliedNewtons\": ").Append(NP(maxMeasuredVsTranscribed)).Append(",\n");
        json.Append("      \"curveMatchesMeasuredEveryStep\": ").Append(Bool(maxCurveVsMeasured <= 1e-4f)).Append(",\n");
        json.Append("      \"appliedMatchesMeasuredEveryStep\": ").Append(Bool(maxMeasuredVsTranscribed <= 1e-4f)).Append("\n");
        json.Append("    }");
        return json.ToString();
    }

    private static int FirstCapStep(List<BurnStep> rows)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            if (Mathf.Abs(rows[index].AppliedFactor - 1f) > 1e-6f)
            {
                return rows[index].Step;
            }
        }
        return -1;
    }

    private static float Max(List<BurnStep> rows, Func<BurnStep, float> selector)
    {
        float value = 0f;
        for (int index = 0; index < rows.Count; index++)
        {
            value = Mathf.Max(value, selector(rows[index]));
        }
        return value;
    }

    private static string V(Vector3 value) =>
        "[" + NP(value.x) + ", " + NP(value.y) + ", " + NP(value.z) + "]";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string N(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NP(float value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
}
