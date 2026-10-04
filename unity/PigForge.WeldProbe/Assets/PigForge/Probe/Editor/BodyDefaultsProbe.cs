using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PigForge.WeldProbe.Probe
{
/// <summary>
/// Measures the Rigidbody / PhysicsManager defaults the original Bad Piggies ran on, on the
/// original's own PhysX (Unity 2021.3.45f2, the editor BPLE 2022.1.9/ProjectSettings/
/// ProjectVersion.txt pins), so PigForge can implement the same numbers in its own physics kernel
/// instead of trusting the Unity documentation.
///
/// Every setting the probe applies is PARSED AT RUN TIME from the original's own project files --
/// nothing is hand-written (tasks/body-defaults-probe-brief.md: "数值不许手写猜测"):
///   BPLE 2022.1.9/ProjectSettings/DynamicsManager.asset
///     m_Gravity {0, -9.81, 0}, m_BounceThreshold 2, m_DefaultMaxDepenetrationVelocity 10,
///     m_SleepThreshold 0.005, m_DefaultContactOffset 0.005, m_DefaultSolverIterations 6,
///     m_DefaultSolverVelocityIterations 1, m_DefaultMaxAngularSpeed 7
///   BPLE 2022.1.9/ProjectSettings/TimeManager.asset
///     Fixed Timestep 0.02, Maximum Allowed Timestep 0.05
/// If either file is unreadable the probe throws instead of silently falling back to a guess.
///
/// Shapes mirror the original: a 1x1x1 box (every original frame collider is a 1x1x1 box), mass 1,
/// 2.5D constraints (freeze Z position + freeze X/Y rotation, BasePart.cs:1194-1196), interpolate.
/// Damping is set per measurement on purpose (BasePart.cs:1192-1193 authors drag 0.2 /
/// angularDrag 0.05).
///
/// Four measurements (each one scene, primitives only, driven by explicit Physics.Simulate):
///   1. omegaCap      -- box, no gravity, seeded at 100 rad/s or driven by a constant 10 N*m
///                       torque around Z, 100 fixed steps -> steady |omega|. Run under
///                       Physics.defaultMaxAngularSpeed 7 (the original's value), 1000 (the real
///                       "uncapped" control) and 0, plus body-level maxAngularVelocity overrides.
///                       NB: in PhysX 4.1 a cap of 0 is NOT "unlimited" -- it zeroes the angular
///                       velocity (see the clamp below), which is why the 1000-cap cells exist.
///   2. linearDrag    -- gravity off, v0 = 10 m/s along X, drag 0.2, 50 steps -> measured per-step
///                       decay v_n / v_(n-1) against three candidates: 1/(1+drag*dt),
///                       exp(-drag*dt) and 1-drag*dt.
///   3. angularDrag   -- gravity off, omega0 = 5 rad/s around Z (below the 7 cap on purpose, so the
///                       clamp cannot interfere), angularDrag 0.05, 50 steps -> same candidates.
///   4. terminalSpeed -- gravity on (-9.81), drag 0.2, mass 1, from rest for 50 s -> speed at 5 s
///                       (the brief's window) and at 50 s against g/drag = 49.05.
///
/// The measured answer for 2/3/4 is the third candidate, 1-c*dt, which is exactly what PhysX 4.1
/// does (physx/source/lowleveldynamics/src/DyBodyCoreIntegrator.h,
/// bodyCoreComputeUnconstrainedVelocity: gravity is added first, then
/// linearVelocity *= 1-linearDamping*dt / angularVelocity *= 1-angularDamping*dt, then the
/// max-angular-velocity clamp scales the velocity vector by sqrt(maxSq/actualSq)).
///
/// Output: replays/body-defaults-probe.json (byte-stable across runs) plus a readable Debug.Log
/// summary. Driven headlessly:
///   unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
///     -- -executeMethod PigForge.WeldProbe.Probe.BodyDefaultsProbe.Run -logFile -
/// </summary>
public static class BodyDefaultsProbe
{
    // The original's project settings, resolved from this probe's own location
    // (<repo>/unity/PigForge.WeldProbe/Assets -> <repo>/../BPLE 2022.1.9/ProjectSettings).
    private const string OriginalSettingsFallback = @"C:\tmp\BAD_PIGGIES\BPLE 2022.1.9\ProjectSettings";

    private const int Constraints25D = 56;      // freeze Z position + freeze X/Y rotation (BasePart.cs:1194-1196)
    private const float BoxMass = 1f;
    private const float BoxInertiaZ = 1f / 6f;  // (1/12) * m * (sx^2 + sy^2) for a 1x1x1 box, m = 1
    private const int OmegaSteps = 100;
    private const float OmegaSeed = 100f;
    private const float OmegaTorque = 10f;
    private const float OmegaUncappedControl = 1000f;   // a cap that cannot bind over 100 steps
    private const int DecaySteps = 50;
    private const float LinearDragInitialSpeed = 10f;
    private const float LinearDragCoefficient = 0.2f;
    private const float AngularDragInitialOmega = 5f;   // below the 7 rad/s cap on purpose
    private const float AngularDragCoefficient = 0.05f;
    private const int FallSteps = 2500;                 // 50 s at dt = 0.02 (the brief asks for 5 s; see the JSON)
    private const int FallSampleStride = 10;

    private const string PhysXDampingSource = "PhysX 4.1 physx/source/lowleveldynamics/src/DyBodyCoreIntegrator.h bodyCoreComputeUnconstrainedVelocity(): gravity added first, then linearVelocity *= 1-linearDamping*dt and angularVelocity *= 1-angularDamping*dt (fsel-clamped at 0), then the max-angular-velocity clamp angularVelocity *= sqrt(maxSq/angVelSq)";

    public static void Run()
    {
        OriginalSettings original = ReadOriginalSettings();
        ApplyOriginalSettings(original);

        List<OmegaCell> omegaCells = new List<OmegaCell>();
        omegaCells.Add(RunOmegaCell(original, "omega_seed_cap7", "seed", original.MaxAngularSpeed, -1f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_seed_cap1000", "seed", OmegaUncappedControl, -1f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_seed_cap0", "seed", 0f, -1f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_seed_bodycap1000", "seed", original.MaxAngularSpeed, OmegaUncappedControl, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_seed_bodycap0", "seed", original.MaxAngularSpeed, 0f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_torque_cap7", "torque", original.MaxAngularSpeed, -1f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_torque_cap1000", "torque", OmegaUncappedControl, -1f, OmegaSteps));
        omegaCells.Add(RunOmegaCell(original, "omega_torque_cap0", "torque", 0f, -1f, OmegaSteps));

        DecayCell linear = RunDecayCell(original, "linear_drag", "linear", LinearDragCoefficient, LinearDragInitialSpeed, DecaySteps);
        DecayCell angular = RunDecayCell(original, "angular_drag", "angular", AngularDragCoefficient, AngularDragInitialOmega, DecaySteps);
        FallCell fall = RunFallCell(original, LinearDragCoefficient, linear.MeasuredRatioFinal, FallSteps);

        foreach (OmegaCell cell in omegaCells)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[body-defaults] {0}: variant={1} projectDefaultMaxAngularSpeed={2:0.######} bodyMaxAngularVelocityAtCreate={3:0.######} bodyMaxAngularVelocity={4:0.######} omegaAfterSeed={5:0.######} omegaStep1={6:0.######} finalOmega={7:0.######} maxOmega={8:0.######}",
                cell.Id, cell.Variant, cell.ProjectDefaultMaxAngularSpeed, cell.BodyMaxAngularVelocityAtCreate,
                cell.BodyMaxAngularVelocity, cell.OmegaAfterSeed, cell.OmegaStep1, cell.FinalOmega, cell.MaxOmega));
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[body-defaults] linear drag={0:0.######}: measuredPerStep(final)={1:0.#########} candidates physX={2:0.#########} exp={3:0.#########} explicit={4:0.#########} maxAbsErrorPhysX={5:0.#########} maxAbsErrorExp={6:0.#########} maxAbsErrorExplicit={7:0.#########} bestFit={8}",
            linear.Coefficient, linear.MeasuredRatioFinal, linear.CandidatePhysX, linear.CandidateExponential, linear.CandidateExplicit,
            linear.MaxAbsErrorPhysX, linear.MaxAbsErrorExponential, linear.MaxAbsErrorExplicit, linear.BestFit));

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[body-defaults] angular drag={0:0.######}: measuredPerStep(final)={1:0.#########} candidates physX={2:0.#########} exp={3:0.#########} explicit={4:0.#########} maxAbsErrorPhysX={5:0.#########} maxAbsErrorExp={6:0.#########} maxAbsErrorExplicit={7:0.#########} bestFit={8}",
            angular.Coefficient, angular.MeasuredRatioFinal, angular.CandidatePhysX, angular.CandidateExponential, angular.CandidateExplicit,
            angular.MaxAbsErrorPhysX, angular.MaxAbsErrorExponential, angular.MaxAbsErrorExplicit, angular.BestFit));

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[body-defaults] terminal speed drag={0:0.######}: at5s={1:0.#########} final({2:0.######}s)={3:0.#########} g/drag={4:0.#########} discreteExplicitTerminal={5:0.#########} maxAbsErrorPhysX={6:0.#########} maxAbsErrorExp={7:0.#########} maxAbsErrorExplicit={8:0.#########} maxAbsErrorGravityAfterDamping={9:0.#########} bestFit={10}",
            fall.Drag, fall.MeasuredSpeed[250], fall.Seconds, fall.MeasuredSpeed[fall.MeasuredSpeed.Count - 1], fall.AnalyticTerminal,
            fall.DiscreteExplicitTerminal, fall.MaxAbsErrorPhysX, fall.MaxAbsErrorExponential, fall.MaxAbsErrorExplicit,
            fall.MaxAbsErrorGravityAfterDamping, fall.BestFit));

        string outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "replays", "body-defaults-probe.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, WriteJson(original, omegaCells, linear, angular, fall), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log("[body-defaults] wrote " + outputPath);
    }

    // ---------------------------------------------------------------------------------------
    // Original settings: parsed from the original's own project files, never guessed.
    // ---------------------------------------------------------------------------------------

    private sealed class OriginalSettings
    {
        public string DynamicsPath;
        public string TimePath;
        public Vector3 Gravity;
        public float BounceThreshold;
        public float MaxDepenetrationVelocity;
        public float SleepThreshold;
        public float ContactOffset;
        public int SolverIterations;
        public int SolverVelocityIterations;
        public float MaxAngularSpeed;
        public float FixedTimeStep;
        public float MaximumAllowedTimestep;
    }

    private static OriginalSettings ReadOriginalSettings()
    {
        string directory = ResolveOriginalSettingsDirectory();
        string dynamicsPath = Path.Combine(directory, "DynamicsManager.asset");
        string timePath = Path.Combine(directory, "TimeManager.asset");
        string dynamics = ReadAssetOrThrow(dynamicsPath);
        string time = ReadAssetOrThrow(timePath);

        return new OriginalSettings
        {
            DynamicsPath = dynamicsPath,
            TimePath = timePath,
            Gravity = ReadVector3OrThrow(dynamics, "m_Gravity", dynamicsPath),
            BounceThreshold = ReadScalarOrThrow(dynamics, "m_BounceThreshold", dynamicsPath),
            MaxDepenetrationVelocity = ReadScalarOrThrow(dynamics, "m_DefaultMaxDepenetrationVelocity", dynamicsPath),
            SleepThreshold = ReadScalarOrThrow(dynamics, "m_SleepThreshold", dynamicsPath),
            ContactOffset = ReadScalarOrThrow(dynamics, "m_DefaultContactOffset", dynamicsPath),
            SolverIterations = (int)ReadScalarOrThrow(dynamics, "m_DefaultSolverIterations", dynamicsPath),
            SolverVelocityIterations = (int)ReadScalarOrThrow(dynamics, "m_DefaultSolverVelocityIterations", dynamicsPath),
            MaxAngularSpeed = ReadScalarOrThrow(dynamics, "m_DefaultMaxAngularSpeed", dynamicsPath),
            FixedTimeStep = ReadScalarOrThrow(time, "Fixed Timestep", timePath),
            MaximumAllowedTimestep = ReadScalarOrThrow(time, "Maximum Allowed Timestep", timePath),
        };
    }

    private static string ResolveOriginalSettingsDirectory()
    {
        // Application.dataPath = <repo>/unity/PigForge.WeldProbe/Assets.
        string relative = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "BPLE 2022.1.9", "ProjectSettings"));
        if (Directory.Exists(relative))
        {
            return relative;
        }
        if (Directory.Exists(OriginalSettingsFallback))
        {
            return OriginalSettingsFallback;
        }
        throw new DirectoryNotFoundException("BodyDefaultsProbe could not find the original's ProjectSettings (tried " + relative + " and " + OriginalSettingsFallback + ").");
    }

    private static string ReadAssetOrThrow(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("BodyDefaultsProbe reads the original's physics settings from the original's own files; missing: " + path);
        }
        return File.ReadAllText(path);
    }

    private static float ReadScalarOrThrow(string text, string key, string path)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }
            string value = line.Substring(key.Length + 1).Trim();
            float parsed;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new InvalidDataException("BodyDefaultsProbe cannot parse " + key + " = '" + value + "' in " + path);
            }
            return parsed;
        }
        throw new InvalidDataException("BodyDefaultsProbe found no " + key + " in " + path);
    }

    private static Vector3 ReadVector3OrThrow(string text, string key, string path)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }
            string value = line.Substring(key.Length + 1).Trim().Trim('{', '}');
            Vector3 result = Vector3.zero;
            foreach (string part in value.Split(','))
            {
                string[] pair = part.Split(':');
                if (pair.Length != 2)
                {
                    continue;
                }
                float parsed = float.Parse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                switch (pair[0].Trim())
                {
                    case "x": result.x = parsed; break;
                    case "y": result.y = parsed; break;
                    case "z": result.z = parsed; break;
                }
            }
            return result;
        }
        throw new InvalidDataException("BodyDefaultsProbe found no " + key + " in " + path);
    }

    private static void ApplyOriginalSettings(OriginalSettings settings)
    {
        Physics.gravity = settings.Gravity;
        Physics.defaultSolverIterations = settings.SolverIterations;
        Physics.defaultSolverVelocityIterations = settings.SolverVelocityIterations;
        Physics.sleepThreshold = settings.SleepThreshold;
        Physics.defaultContactOffset = settings.ContactOffset;
        Physics.bounceThreshold = settings.BounceThreshold;
        Physics.defaultMaxDepenetrationVelocity = settings.MaxDepenetrationVelocity;
        Physics.defaultMaxAngularSpeed = settings.MaxAngularSpeed;
        Physics.autoSyncTransforms = true;
        Time.fixedDeltaTime = settings.FixedTimeStep;
        Time.maximumDeltaTime = settings.MaximumAllowedTimestep;
#if UNITY_6000_0_OR_NEWER
        Physics.simulationMode = SimulationMode.Script;
#else
        // Unity 2021 has no Physics.simulationMode; autoSimulation = false plus an explicit
        // Physics.Simulate per step is the same thing and keeps the probe deterministic.
        Physics.autoSimulation = false;
#endif
    }

    // ---------------------------------------------------------------------------------------
    // Measurement 1: the angular speed cap.
    // ---------------------------------------------------------------------------------------

    private sealed class OmegaCell
    {
        public string Id;
        public string Variant;
        public float ProjectDefaultMaxAngularSpeed;
        public float BodyMaxAngularVelocityAtCreate;
        public bool BodyCapOverridden;
        public float BodyMaxAngularVelocity;
        public float SeedOmega;
        public float OmegaAfterSeed;
        public float OmegaStep1;
        public readonly List<float> Omega = new List<float>();
        public float FinalOmega;
        public float MaxOmega;
    }

    private static OmegaCell RunOmegaCell(OriginalSettings settings, string id, string variant, float projectDefaultMaxAngularSpeed, float bodyCapOverride, int steps)
    {
        ClearScene();

        OmegaCell cell = new OmegaCell
        {
            Id = id,
            Variant = variant,
            ProjectDefaultMaxAngularSpeed = projectDefaultMaxAngularSpeed,
            SeedOmega = variant == "seed" ? OmegaSeed : 0f,
        };

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "OmegaBox";
        box.transform.position = Vector3.zero;

        // Physics.defaultMaxAngularSpeed must be set before the Rigidbody exists: it seeds
        // Rigidbody.maxAngularVelocity at creation time.
        Physics.defaultMaxAngularSpeed = projectDefaultMaxAngularSpeed;
        Rigidbody body = box.AddComponent<Rigidbody>();
        body.mass = BoxMass;
        body.drag = 0f;
        body.angularDrag = 0f;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = (RigidbodyConstraints)Constraints25D;
        body.isKinematic = false;
        cell.BodyMaxAngularVelocityAtCreate = body.maxAngularVelocity;
        if (bodyCapOverride >= 0f)
        {
            body.maxAngularVelocity = bodyCapOverride;
            cell.BodyCapOverridden = true;
        }
        cell.BodyMaxAngularVelocity = body.maxAngularVelocity;

        if (cell.SeedOmega != 0f)
        {
            body.angularVelocity = new Vector3(0f, 0f, cell.SeedOmega);
        }
        cell.OmegaAfterSeed = body.angularVelocity.magnitude;

        for (int step = 0; step < steps; step++)
        {
            if (variant == "torque")
            {
                body.AddTorque(new Vector3(0f, 0f, OmegaTorque), ForceMode.Force);
            }

            Physics.Simulate(settings.FixedTimeStep);

            float magnitude = body.angularVelocity.magnitude;
            cell.Omega.Add(magnitude);
            cell.MaxOmega = Mathf.Max(cell.MaxOmega, magnitude);
            if (step == 0)
            {
                cell.OmegaStep1 = magnitude;
            }
        }

        cell.FinalOmega = cell.Omega.Count > 0 ? cell.Omega[cell.Omega.Count - 1] : 0f;
        UnityEngine.Object.DestroyImmediate(box);
        return cell;
    }

    // ---------------------------------------------------------------------------------------
    // Measurements 2 and 3: linear and angular damping per-step decay.
    // ---------------------------------------------------------------------------------------

    private sealed class DecayCell
    {
        public string Id;
        public string Kind;
        public float Coefficient;
        public float InitialValue;
        public int Steps;
        public readonly List<float> Measured = new List<float>();       // index 0 = initial, then one per step
        public readonly List<float> PerStepRatio = new List<float>();   // v_n / v_(n-1)
        public float MeasuredRatioMin;
        public float MeasuredRatioMax;
        public float MeasuredRatioMean;
        public float MeasuredRatioFinal;
        public float CandidatePhysX;          // 1 / (1 + coefficient * dt)
        public float CandidateExponential;    // exp(-coefficient * dt)
        public float CandidateExplicit;       // 1 - coefficient * dt  (what PhysX 4.1 actually does)
        public readonly List<float> PredictedPhysX = new List<float>();
        public readonly List<float> PredictedExponential = new List<float>();
        public readonly List<float> PredictedExplicit = new List<float>();
        public float MaxAbsErrorPhysX;
        public float MaxAbsErrorExponential;
        public float MaxAbsErrorExplicit;
        public float FinalErrorPhysX;
        public float FinalErrorExponential;
        public float FinalErrorExplicit;
        public string BestFit;
        public float BestFitSeparation;
        public float CrossMax;                // sanity: the axis that must not move
    }

    private static DecayCell RunDecayCell(OriginalSettings settings, string id, string kind, float coefficient, float initialValue, int steps)
    {
        ClearScene();

        float dt = settings.FixedTimeStep;
        bool angular = kind == "angular";

        DecayCell cell = new DecayCell
        {
            Id = id,
            Kind = kind,
            Coefficient = coefficient,
            InitialValue = initialValue,
            Steps = steps,
        };

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = angular ? "AngularDragBox" : "LinearDragBox";
        box.transform.position = Vector3.zero;

        Physics.defaultMaxAngularSpeed = settings.MaxAngularSpeed;
        Rigidbody body = box.AddComponent<Rigidbody>();
        body.mass = BoxMass;
        body.drag = angular ? 0f : coefficient;
        body.angularDrag = angular ? coefficient : 0f;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = (RigidbodyConstraints)Constraints25D;
        body.isKinematic = false;
        if (angular)
        {
            body.angularVelocity = new Vector3(0f, 0f, initialValue);
        }
        else
        {
            body.velocity = new Vector3(initialValue, 0f, 0f);
        }

        cell.Measured.Add(initialValue);
        float previous = initialValue;
        for (int step = 0; step < steps; step++)
        {
            Physics.Simulate(dt);

            float value = angular ? body.angularVelocity.magnitude : body.velocity.x;
            cell.Measured.Add(value);
            cell.PerStepRatio.Add(previous != 0f ? value / previous : 0f);
            previous = value;

            float cross = angular ? Mathf.Abs(body.velocity.x) : body.angularVelocity.magnitude;
            cell.CrossMax = Mathf.Max(cell.CrossMax, cross);
        }

        cell.CandidatePhysX = 1f / (1f + coefficient * dt);
        cell.CandidateExponential = Mathf.Exp(-coefficient * dt);
        cell.CandidateExplicit = ExplicitDampingMultiplier(coefficient, dt);
        cell.PredictedPhysX.AddRange(PredictDecay(initialValue, steps, cell.CandidatePhysX));
        cell.PredictedExponential.AddRange(PredictDecay(initialValue, steps, cell.CandidateExponential));
        cell.PredictedExplicit.AddRange(PredictDecay(initialValue, steps, cell.CandidateExplicit));
        cell.MaxAbsErrorPhysX = MaxAbsError(cell.Measured, cell.PredictedPhysX);
        cell.MaxAbsErrorExponential = MaxAbsError(cell.Measured, cell.PredictedExponential);
        cell.MaxAbsErrorExplicit = MaxAbsError(cell.Measured, cell.PredictedExplicit);
        cell.FinalErrorPhysX = Mathf.Abs(cell.Measured[steps] - cell.PredictedPhysX[steps]);
        cell.FinalErrorExponential = Mathf.Abs(cell.Measured[steps] - cell.PredictedExponential[steps]);
        cell.FinalErrorExplicit = Mathf.Abs(cell.Measured[steps] - cell.PredictedExplicit[steps]);
        cell.BestFit = PickBestFit(
            new[] { cell.MaxAbsErrorPhysX, cell.MaxAbsErrorExponential, cell.MaxAbsErrorExplicit },
            new[] { "physX 1/(1+c*dt)", "exponential exp(-c*dt)", "explicit 1-c*dt" },
            out cell.BestFitSeparation);

        if (cell.PerStepRatio.Count > 0)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            float sum = 0f;
            foreach (float ratio in cell.PerStepRatio)
            {
                min = Mathf.Min(min, ratio);
                max = Mathf.Max(max, ratio);
                sum += ratio;
            }
            cell.MeasuredRatioMin = min;
            cell.MeasuredRatioMax = max;
            cell.MeasuredRatioMean = sum / cell.PerStepRatio.Count;
            cell.MeasuredRatioFinal = cell.PerStepRatio[cell.PerStepRatio.Count - 1];
        }

        UnityEngine.Object.DestroyImmediate(box);
        return cell;
    }

    // ---------------------------------------------------------------------------------------
    // Measurement 4: free-fall terminal speed.
    // ---------------------------------------------------------------------------------------

    private sealed class FallCell
    {
        public string Id = "terminal_speed";
        public float Drag;
        public float GravityY;
        public float Dt;
        public int Steps;
        public float Seconds;
        public float MeasuredDecay;
        public readonly List<float> MeasuredSpeed = new List<float>();
        public readonly List<float> PredictedPhysX = new List<float>();
        public readonly List<float> PredictedExponential = new List<float>();
        public readonly List<float> PredictedExplicit = new List<float>();
        public readonly List<float> PredictedGravityAfterDamping = new List<float>();
        public readonly List<float> PredictedFromMeasuredDecay = new List<float>();
        public float AnalyticTerminal;
        public float DiscreteExplicitTerminal;
        public float ContinuousAnalyticAt5s;
        public float ContinuousAnalyticFinal;
        public float MaxAbsErrorPhysX;
        public float MaxAbsErrorExponential;
        public float MaxAbsErrorExplicit;
        public float MaxAbsErrorGravityAfterDamping;
        public float MaxAbsErrorFromMeasuredDecay;
        public string BestFit;
        public float BestFitSeparation;
    }

    private static FallCell RunFallCell(OriginalSettings settings, float drag, float measuredDecay, int steps)
    {
        ClearScene();

        float dt = settings.FixedTimeStep;
        float gravityMagnitude = Mathf.Abs(settings.Gravity.y);
        float explicitDecay = ExplicitDampingMultiplier(drag, dt);

        FallCell cell = new FallCell
        {
            Drag = drag,
            GravityY = settings.Gravity.y,
            Dt = dt,
            Steps = steps,
            Seconds = steps * dt,
            MeasuredDecay = measuredDecay,
            AnalyticTerminal = gravityMagnitude / drag,
            DiscreteExplicitTerminal = (gravityMagnitude / drag) * explicitDecay,
            ContinuousAnalyticAt5s = (gravityMagnitude / drag) * (1f - Mathf.Exp(-drag * 5f)),
            ContinuousAnalyticFinal = (gravityMagnitude / drag) * (1f - Mathf.Exp(-drag * steps * dt)),
        };

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "FallBox";
        box.transform.position = Vector3.zero;

        Physics.defaultMaxAngularSpeed = settings.MaxAngularSpeed;
        Rigidbody body = box.AddComponent<Rigidbody>();
        body.mass = BoxMass;
        body.drag = drag;
        body.angularDrag = 0f;
        body.useGravity = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = (RigidbodyConstraints)Constraints25D;
        body.isKinematic = false;
        body.velocity = Vector3.zero;

        cell.MeasuredSpeed.Add(0f);
        for (int step = 0; step < steps; step++)
        {
            Physics.Simulate(dt);
            cell.MeasuredSpeed.Add(-body.velocity.y);   // downward speed, positive
        }

        cell.PredictedPhysX.AddRange(PredictFall(gravityMagnitude, dt, steps, 1f / (1f + drag * dt), true));
        cell.PredictedExponential.AddRange(PredictFall(gravityMagnitude, dt, steps, Mathf.Exp(-drag * dt), true));
        cell.PredictedExplicit.AddRange(PredictFall(gravityMagnitude, dt, steps, explicitDecay, true));
        cell.PredictedGravityAfterDamping.AddRange(PredictFall(gravityMagnitude, dt, steps, explicitDecay, false));
        cell.PredictedFromMeasuredDecay.AddRange(PredictFall(gravityMagnitude, dt, steps, measuredDecay, true));
        cell.MaxAbsErrorPhysX = MaxAbsError(cell.MeasuredSpeed, cell.PredictedPhysX);
        cell.MaxAbsErrorExponential = MaxAbsError(cell.MeasuredSpeed, cell.PredictedExponential);
        cell.MaxAbsErrorExplicit = MaxAbsError(cell.MeasuredSpeed, cell.PredictedExplicit);
        cell.MaxAbsErrorGravityAfterDamping = MaxAbsError(cell.MeasuredSpeed, cell.PredictedGravityAfterDamping);
        cell.MaxAbsErrorFromMeasuredDecay = MaxAbsError(cell.MeasuredSpeed, cell.PredictedFromMeasuredDecay);
        cell.BestFit = PickBestFit(
            new[]
            {
                cell.MaxAbsErrorPhysX,
                cell.MaxAbsErrorExponential,
                cell.MaxAbsErrorExplicit,
                cell.MaxAbsErrorGravityAfterDamping,
            },
            new[]
            {
                "physX 1/(1+c*dt)",
                "exponential exp(-c*dt)",
                "explicit 1-c*dt",
                "explicit 1-c*dt with gravity added after damping",
            },
            out cell.BestFitSeparation);

        UnityEngine.Object.DestroyImmediate(box);
        return cell;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    private static void ClearScene()
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }
    }

    /// <summary>PhysX's multiplier: 1 - coefficient * dt, floored at 0 (intrinsics::fsel in the source).</summary>
    private static float ExplicitDampingMultiplier(float coefficient, float dt)
    {
        float multiplier = 1f - coefficient * dt;
        return multiplier > 0f ? multiplier : 0f;
    }

    private static List<float> PredictDecay(float initial, int steps, float decay)
    {
        List<float> values = new List<float> { initial };
        float value = initial;
        for (int index = 0; index < steps; index++)
        {
            value *= decay;
            values.Add(value);
        }
        return values;
    }

    /// <summary>
    /// PhysX 4.1's order: add the gravity impulse, then multiply by the damping factor.
    /// gravityAfterDamping switches to the other plausible order, to show the measurement
    /// discriminates between them.
    /// </summary>
    private static List<float> PredictFall(float gravityMagnitude, float dt, int steps, float decay, bool gravityFirst)
    {
        List<float> values = new List<float> { 0f };
        float value = 0f;
        for (int index = 0; index < steps; index++)
        {
            value = gravityFirst ? (value + gravityMagnitude * dt) * decay : value * decay + gravityMagnitude * dt;
            values.Add(value);
        }
        return values;
    }

    private static float MaxAbsError(List<float> measured, List<float> predicted)
    {
        float worst = 0f;
        int count = Mathf.Min(measured.Count, predicted.Count);
        for (int index = 0; index < count; index++)
        {
            worst = Mathf.Max(worst, Mathf.Abs(measured[index] - predicted[index]));
        }
        return worst;
    }

    private static string PickBestFit(float[] errors, string[] names, out float separation)
    {
        int best = 0;
        int second = -1;
        for (int index = 1; index < errors.Length; index++)
        {
            if (errors[index] < errors[best])
            {
                second = best;
                best = index;
            }
            else if (second < 0 || errors[index] < errors[second])
            {
                second = index;
            }
        }

        separation = second >= 0 && errors[best] > 0f ? errors[second] / errors[best] : float.PositiveInfinity;
        if (second >= 0 && separation < 1.5f)
        {
            return "indistinguishable(" + names[best] + " ~ " + names[second] + ")";
        }
        return names[best];
    }

    private static OmegaCell FindOmegaCell(List<OmegaCell> cells, string id)
    {
        foreach (OmegaCell cell in cells)
        {
            if (cell.Id == id)
            {
                return cell;
            }
        }
        return null;
    }

    private static float OmegaFinal(List<OmegaCell> cells, string id)
    {
        OmegaCell cell = FindOmegaCell(cells, id);
        return cell != null ? cell.FinalOmega : 0f;
    }

    // ---------------------------------------------------------------------------------------
    // JSON output.
    // ---------------------------------------------------------------------------------------

    private static string WriteJson(OriginalSettings settings, List<OmegaCell> omegaCells, DecayCell linear, DecayCell angular, FallCell fall)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.body-defaults-probe\",\n");
        json.Append("  \"probeVersion\": 1,\n");
        json.Append("  \"reference\": \"unity 2021.3.45f2 (BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt pins it), real PhysX 4.1; original physics settings parsed at run time from BPLE 2022.1.9/ProjectSettings/{DynamicsManager,TimeManager}.asset\",\n");
        json.Append("  \"physXSourceOfTruth\": \"").Append(PhysXDampingSource).Append("\",\n");

        json.Append("  \"originalProjectSettings\": {\n");
        json.Append("    \"dynamicsManagerAsset\": \"").Append(Slash(settings.DynamicsPath)).Append("\",\n");
        json.Append("    \"timeManagerAsset\": \"").Append(Slash(settings.TimePath)).Append("\",\n");
        json.Append("    \"gravity\": {\"x\": ").Append(N(settings.Gravity.x)).Append(", \"y\": ").Append(N(settings.Gravity.y)).Append(", \"z\": ").Append(N(settings.Gravity.z)).Append("},\n");
        json.Append("    \"bounceThreshold\": ").Append(N(settings.BounceThreshold)).Append(",\n");
        json.Append("    \"maxDepenetrationVelocity\": ").Append(N(settings.MaxDepenetrationVelocity)).Append(",\n");
        json.Append("    \"sleepThreshold\": ").Append(N(settings.SleepThreshold)).Append(",\n");
        json.Append("    \"contactOffset\": ").Append(N(settings.ContactOffset)).Append(",\n");
        json.Append("    \"solverIterations\": ").Append(settings.SolverIterations).Append(",\n");
        json.Append("    \"solverVelocityIterations\": ").Append(settings.SolverVelocityIterations).Append(",\n");
        json.Append("    \"defaultMaxAngularSpeed\": ").Append(N(settings.MaxAngularSpeed)).Append(",\n");
        json.Append("    \"fixedTimeStep\": ").Append(N(settings.FixedTimeStep)).Append(",\n");
        json.Append("    \"maximumAllowedTimestep\": ").Append(N(settings.MaximumAllowedTimestep)).Append("\n");
        json.Append("  },\n");

        json.Append("  \"probeSetup\": {\n");
        json.Append("    \"shape\": \"1x1x1 box (PrimitiveType.Cube)\",\n");
        json.Append("    \"mass\": ").Append(N(BoxMass)).Append(",\n");
        json.Append("    \"boxInertiaZ\": ").Append(N(BoxInertiaZ)).Append(",\n");
        json.Append("    \"rigidbodyConstraints\": ").Append(Constraints25D).Append(",\n");
        json.Append("    \"rigidbodyConstraintsMeaning\": \"freeze Z position + freeze X/Y rotation (BasePart.cs:1194-1196)\",\n");
        json.Append("    \"omega\": {\"steps\": ").Append(OmegaSteps).Append(", \"seedOmega\": ").Append(N(OmegaSeed)).Append(", \"torqueZ\": ").Append(N(OmegaTorque)).Append(", \"drag\": 0, \"angularDrag\": 0},\n");
        json.Append("    \"linearDrag\": {\"steps\": ").Append(DecaySteps).Append(", \"v0\": ").Append(N(LinearDragInitialSpeed)).Append(", \"coefficient\": ").Append(N(LinearDragCoefficient)).Append("},\n");
        json.Append("    \"angularDrag\": {\"steps\": ").Append(DecaySteps).Append(", \"omega0\": ").Append(N(AngularDragInitialOmega)).Append(", \"coefficient\": ").Append(N(AngularDragCoefficient)).Append("},\n");
        json.Append("    \"fall\": {\"steps\": ").Append(FallSteps).Append(", \"seconds\": ").Append(N(FallSteps * settings.FixedTimeStep)).Append(", \"drag\": ").Append(N(LinearDragCoefficient)).Append(", \"angularDrag\": 0}\n");
        json.Append("  },\n");

        json.Append("  \"omegaCap\": {\n");
        json.Append("    \"note\": \"seed = initial angularVelocity 100 rad/s around Z, no torque; torque = +10 N*m around Z applied before every step; drag = angularDrag = 0. |omega| is read after each Physics.Simulate, so omegaStep1 is the first clamped value. In PhysX 4.1 the clamp scales the angular velocity vector by sqrt(maxSq/actualSq), so maxAngularVelocity = 0 gives sqrt(0/x) = 0 -- a cap of 0 means ZERO angular velocity, not unlimited; the cap1000 cells are the real uncapped control.\",\n");
        json.Append("    \"cells\": [\n");
        for (int index = 0; index < omegaCells.Count; index++)
        {
            OmegaCell cell = omegaCells[index];
            json.Append("      {\n");
            json.Append("        \"id\": \"").Append(cell.Id).Append("\",\n");
            json.Append("        \"variant\": \"").Append(cell.Variant).Append("\",\n");
            json.Append("        \"projectDefaultMaxAngularSpeed\": ").Append(N(cell.ProjectDefaultMaxAngularSpeed)).Append(",\n");
            json.Append("        \"bodyMaxAngularVelocityAtCreate\": ").Append(N(cell.BodyMaxAngularVelocityAtCreate)).Append(",\n");
            json.Append("        \"bodyCapOverridden\": ").Append(cell.BodyCapOverridden ? "true" : "false").Append(",\n");
            json.Append("        \"bodyMaxAngularVelocity\": ").Append(N(cell.BodyMaxAngularVelocity)).Append(",\n");
            json.Append("        \"seedOmega\": ").Append(N(cell.SeedOmega)).Append(",\n");
            json.Append("        \"omegaAfterSeed\": ").Append(N(cell.OmegaAfterSeed)).Append(",\n");
            json.Append("        \"omegaStep1\": ").Append(N(cell.OmegaStep1)).Append(",\n");
            json.Append("        \"finalOmega\": ").Append(N(cell.FinalOmega)).Append(",\n");
            json.Append("        \"maxOmega\": ").Append(N(cell.MaxOmega)).Append(",\n");
            json.Append("        \"omegaSeries\": ").Append(Series(cell.Omega, 1)).Append("\n");
            json.Append("      }");
            json.Append(index == omegaCells.Count - 1 ? "\n" : ",\n");
        }
        json.Append("    ]\n");
        json.Append("  },\n");

        AppendDecay(json, "linearDrag", linear);
        AppendDecay(json, "angularDrag", angular);
        AppendFall(json, fall);

        json.Append("  \"summary\": {\n");
        json.Append("    \"omegaCapSeedCap7\": ").Append(N(OmegaFinal(omegaCells, "omega_seed_cap7"))).Append(",\n");
        json.Append("    \"omegaCapTorqueCap7\": ").Append(N(OmegaFinal(omegaCells, "omega_torque_cap7"))).Append(",\n");
        json.Append("    \"omegaControlSeedCap1000\": ").Append(N(OmegaFinal(omegaCells, "omega_seed_cap1000"))).Append(",\n");
        json.Append("    \"omegaControlTorqueCap1000\": ").Append(N(OmegaFinal(omegaCells, "omega_torque_cap1000"))).Append(",\n");
        json.Append("    \"omegaSeedProjectDefault0\": ").Append(N(OmegaFinal(omegaCells, "omega_seed_cap0"))).Append(",\n");
        json.Append("    \"omegaSeedBodyCap0\": ").Append(N(OmegaFinal(omegaCells, "omega_seed_bodycap0"))).Append(",\n");
        json.Append("    \"omegaCapZeroMeansZeroNotUnlimited\": true,\n");
        json.Append("    \"linearDragPerStepMeasured\": ").Append(NPrecise(linear.MeasuredRatioFinal)).Append(",\n");
        json.Append("    \"linearDragCandidateExplicit\": ").Append(NPrecise(linear.CandidateExplicit)).Append(",\n");
        json.Append("    \"linearDragBestFit\": \"").Append(linear.BestFit).Append("\",\n");
        json.Append("    \"angularDragPerStepMeasured\": ").Append(NPrecise(angular.MeasuredRatioFinal)).Append(",\n");
        json.Append("    \"angularDragCandidateExplicit\": ").Append(NPrecise(angular.CandidateExplicit)).Append(",\n");
        json.Append("    \"angularDragBestFit\": \"").Append(angular.BestFit).Append("\",\n");
        json.Append("    \"terminalSpeedAt5s\": ").Append(NPrecise(fall.MeasuredSpeed[250])).Append(",\n");
        json.Append("    \"terminalSpeedFinal\": ").Append(NPrecise(fall.MeasuredSpeed[fall.MeasuredSpeed.Count - 1])).Append(",\n");
        json.Append("    \"terminalSpeedAnalyticGravityOverDrag\": ").Append(NPrecise(fall.AnalyticTerminal)).Append(",\n");
        json.Append("    \"terminalSpeedAnalyticDiscreteExplicit\": ").Append(NPrecise(fall.DiscreteExplicitTerminal)).Append(",\n");
        json.Append("    \"terminalSpeedBestFit\": \"").Append(fall.BestFit).Append("\"\n");
        json.Append("  }\n}\n");
        return json.ToString();
    }

    private static void AppendDecay(StringBuilder json, string name, DecayCell cell)
    {
        json.Append("  \"").Append(name).Append("\": {\n");
        json.Append("    \"kind\": \"").Append(cell.Kind).Append("\",\n");
        json.Append("    \"coefficient\": ").Append(N(cell.Coefficient)).Append(",\n");
        json.Append("    \"initialValue\": ").Append(N(cell.InitialValue)).Append(",\n");
        json.Append("    \"steps\": ").Append(cell.Steps).Append(",\n");
        json.Append("    \"candidatePhysXPerStep\": ").Append(NPrecise(cell.CandidatePhysX)).Append(",\n");
        json.Append("    \"candidateExponentialPerStep\": ").Append(NPrecise(cell.CandidateExponential)).Append(",\n");
        json.Append("    \"candidateExplicitPerStep\": ").Append(NPrecise(cell.CandidateExplicit)).Append(",\n");
        json.Append("    \"measuredRatioMin\": ").Append(NPrecise(cell.MeasuredRatioMin)).Append(",\n");
        json.Append("    \"measuredRatioMax\": ").Append(NPrecise(cell.MeasuredRatioMax)).Append(",\n");
        json.Append("    \"measuredRatioMean\": ").Append(NPrecise(cell.MeasuredRatioMean)).Append(",\n");
        json.Append("    \"measuredRatioFinal\": ").Append(NPrecise(cell.MeasuredRatioFinal)).Append(",\n");
        json.Append("    \"maxAbsErrorPhysX\": ").Append(NPrecise(cell.MaxAbsErrorPhysX)).Append(",\n");
        json.Append("    \"maxAbsErrorExponential\": ").Append(NPrecise(cell.MaxAbsErrorExponential)).Append(",\n");
        json.Append("    \"maxAbsErrorExplicit\": ").Append(NPrecise(cell.MaxAbsErrorExplicit)).Append(",\n");
        json.Append("    \"finalErrorPhysX\": ").Append(NPrecise(cell.FinalErrorPhysX)).Append(",\n");
        json.Append("    \"finalErrorExponential\": ").Append(NPrecise(cell.FinalErrorExponential)).Append(",\n");
        json.Append("    \"finalErrorExplicit\": ").Append(NPrecise(cell.FinalErrorExplicit)).Append(",\n");
        json.Append("    \"bestFit\": \"").Append(cell.BestFit).Append("\",\n");
        json.Append("    \"bestFitSeparationSecondOverBest\": ").Append(NOrNull(cell.BestFitSeparation)).Append(",\n");
        json.Append("    \"crossAxisMaxAbs\": ").Append(NPrecise(cell.CrossMax)).Append(",\n");
        json.Append("    \"measuredSeries\": ").Append(Series(cell.Measured, 1)).Append(",\n");
        json.Append("    \"perStepRatio\": ").Append(Series(cell.PerStepRatio, 1)).Append(",\n");
        json.Append("    \"predictedPhysX\": ").Append(Series(cell.PredictedPhysX, 1)).Append(",\n");
        json.Append("    \"predictedExponential\": ").Append(Series(cell.PredictedExponential, 1)).Append(",\n");
        json.Append("    \"predictedExplicit\": ").Append(Series(cell.PredictedExplicit, 1)).Append("\n");
        json.Append("  },\n");
    }

    private static void AppendFall(StringBuilder json, FallCell cell)
    {
        int last = cell.MeasuredSpeed.Count - 1;
        int at5s = Mathf.Min(250, last);
        json.Append("  \"terminalSpeed\": {\n");
        json.Append("    \"gravityY\": ").Append(N(cell.GravityY)).Append(",\n");
        json.Append("    \"drag\": ").Append(N(cell.Drag)).Append(",\n");
        json.Append("    \"dt\": ").Append(N(cell.Dt)).Append(",\n");
        json.Append("    \"steps\": ").Append(cell.Steps).Append(",\n");
        json.Append("    \"seconds\": ").Append(N(cell.Seconds)).Append(",\n");
        json.Append("    \"analyticTerminalGravityOverDrag\": ").Append(NPrecise(cell.AnalyticTerminal)).Append(",\n");
        json.Append("    \"analyticDiscreteTerminalExplicit\": ").Append(NPrecise(cell.DiscreteExplicitTerminal)).Append(",\n");
        json.Append("    \"continuousAnalyticAt5s\": ").Append(NPrecise(cell.ContinuousAnalyticAt5s)).Append(",\n");
        json.Append("    \"continuousAnalyticFinal\": ").Append(NPrecise(cell.ContinuousAnalyticFinal)).Append(",\n");
        json.Append("    \"measuredAt5s\": ").Append(NPrecise(cell.MeasuredSpeed[at5s])).Append(",\n");
        json.Append("    \"measuredFinal\": ").Append(NPrecise(cell.MeasuredSpeed[last])).Append(",\n");
        json.Append("    \"predictedAt5sPhysX\": ").Append(NPrecise(cell.PredictedPhysX[at5s])).Append(",\n");
        json.Append("    \"predictedFinalPhysX\": ").Append(NPrecise(cell.PredictedPhysX[last])).Append(",\n");
        json.Append("    \"predictedAt5sExponential\": ").Append(NPrecise(cell.PredictedExponential[at5s])).Append(",\n");
        json.Append("    \"predictedFinalExponential\": ").Append(NPrecise(cell.PredictedExponential[last])).Append(",\n");
        json.Append("    \"predictedAt5sExplicit\": ").Append(NPrecise(cell.PredictedExplicit[at5s])).Append(",\n");
        json.Append("    \"predictedFinalExplicit\": ").Append(NPrecise(cell.PredictedExplicit[last])).Append(",\n");
        json.Append("    \"predictedAt5sGravityAfterDamping\": ").Append(NPrecise(cell.PredictedGravityAfterDamping[at5s])).Append(",\n");
        json.Append("    \"predictedFinalGravityAfterDamping\": ").Append(NPrecise(cell.PredictedGravityAfterDamping[last])).Append(",\n");
        json.Append("    \"predictedAt5sFromMeasuredDecay\": ").Append(NPrecise(cell.PredictedFromMeasuredDecay[at5s])).Append(",\n");
        json.Append("    \"predictedFinalFromMeasuredDecay\": ").Append(NPrecise(cell.PredictedFromMeasuredDecay[last])).Append(",\n");
        json.Append("    \"maxAbsErrorPhysX\": ").Append(NPrecise(cell.MaxAbsErrorPhysX)).Append(",\n");
        json.Append("    \"maxAbsErrorExponential\": ").Append(NPrecise(cell.MaxAbsErrorExponential)).Append(",\n");
        json.Append("    \"maxAbsErrorExplicit\": ").Append(NPrecise(cell.MaxAbsErrorExplicit)).Append(",\n");
        json.Append("    \"maxAbsErrorGravityAfterDamping\": ").Append(NPrecise(cell.MaxAbsErrorGravityAfterDamping)).Append(",\n");
        json.Append("    \"maxAbsErrorFromMeasuredDecay\": ").Append(NPrecise(cell.MaxAbsErrorFromMeasuredDecay)).Append(",\n");
        json.Append("    \"bestFit\": \"").Append(cell.BestFit).Append("\",\n");
        json.Append("    \"bestFitSeparationSecondOverBest\": ").Append(NOrNull(cell.BestFitSeparation)).Append(",\n");
        json.Append("    \"sampleStride\": ").Append(FallSampleStride).Append(",\n");
        json.Append("    \"measuredSpeedSampled\": ").Append(Series(cell.MeasuredSpeed, FallSampleStride)).Append(",\n");
        json.Append("    \"predictedPhysXSampled\": ").Append(Series(cell.PredictedPhysX, FallSampleStride)).Append(",\n");
        json.Append("    \"predictedExponentialSampled\": ").Append(Series(cell.PredictedExponential, FallSampleStride)).Append(",\n");
        json.Append("    \"predictedExplicitSampled\": ").Append(Series(cell.PredictedExplicit, FallSampleStride)).Append("\n");
        json.Append("  },\n");
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
            builder.Append(N(values[index]));
        }
        return builder.Append(']').ToString();
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static string N(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NPrecise(float value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string NOrNull(float value) => float.IsInfinity(value) || float.IsNaN(value) ? "null" : NPrecise(value);
}
}
