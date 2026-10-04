using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PigForge.WeldProbe.Probe
{
/// <summary>
/// Measures the ORIGINAL's telescoping boxing glove on its own PhysX (Unity 2021.3.45f2 with the
/// original's own ProjectSettings). This is the reference side of docs/specs/boxing-glove.md §6:
/// PigForge has no glove at all today (part 28 runs the spring "bounce pad" impulse instead).
///
/// Setup mirrored from the original, one line per source:
///   - glove body  : separate rigidbody, mass 0.5, its own collider        (SpringBoxingGlove.cs:215-222)
///   - joint       : ConfigurableJoint, angular all Locked, x/z Locked, yMotion Limited,
///                   linearLimitSpring 0/0, linearLimit(1, bounce 0),
///                   yDrive{positionSpring 60, positionDamper 3, maximumForce inf},
///                   xDrive{1000, 5}, projectionMode PositionAndRotation, projectionDistance 0.1,
///                   enablePreprocessing false, targetPosition (0,0,0)      (SpringBoxingGlove.cs:170-207)
///   - shoot       : targetPosition = (+-deviationX, targetDistanceY * BoxingGloveLength, 0),
///                   linearLimitSpring.spring = 0.1                        (SpringBoxingGlove.cs:224-262)
///   - wind        : targetPosition 0, yDrive{spring 10*targetDistanceY*len, damper 2.5},
///                   glove mass 0.01, glove collider off                    (SpringBoxingGlove.cs:280-330)
/// The probe holds the host fixed (kinematic) and records how far and how fast the glove travels,
/// how long the wind-back takes, and whether the target distance matches m_targetDistanceY.
///
/// Driven headlessly: unity run unity/PigForge.WeldProbe -- -executeMethod
/// PigForge.WeldProbe.Probe.BoxingGloveProbe.Run
/// </summary>
public static class BoxingGloveProbe
{
    private const float FixedTimeStep = 0.02f;    // BPLE TimeManager "Fixed Timestep: 0.02"
    // Class defaults (SpringBoxingGlove.cs:36-46) -- the prefabs OVERRIDE four of them
    // (tasks/bple-springs-report.json: drive 380, damper 3.5, deviationX 0, shootTime 0.4),
    // so both sets are measured: the prefab's is the reference, the class default is the control.
    private const float TargetDistanceY = 2.5f;   // m_targetDistanceY, no prefab override
    private const float WindingTime = 1f;         // m_WindingTime, no prefab override
    private const float GloveMass = 0.5f;         // :215-222
    private const float WindMass = 0.01f;         // :307-311
    private const float Limit = 1f;               // :176-180 linearLimit.limit
    private const float ShootLimitSpring = 0.1f;  // :248-250
    private const float PrefabDrive = 380f;       // Part_SpringBoxingGlove_*_SET override
    private const float PrefabDriveDamper = 3.5f;
    private const float PrefabShootTime = 0.4f;
    private const float PrefabDeviationX = 0f;
    private const float ClassDrive = 60f;
    private const float ClassDriveDamper = 3f;
    private const float ClassShootTime = 1f;
    private const float ClassDeviationX = 0.01f;

    public static void Run()
    {
        ApplyOriginalPhysicsSettings();

        List<Result> results = new List<Result>
        {
            Measure("prefab_len1", gloveLength: 1f, drive: PrefabDrive, damper: PrefabDriveDamper, shootTime: PrefabShootTime, deviationX: PrefabDeviationX),
            Measure("prefab_len2", gloveLength: 2f, drive: PrefabDrive, damper: PrefabDriveDamper, shootTime: PrefabShootTime, deviationX: PrefabDeviationX),
            Measure("classdefault_len1", gloveLength: 1f, drive: ClassDrive, damper: ClassDriveDamper, shootTime: ClassShootTime, deviationX: ClassDeviationX),
        };

        string path = Path.Combine(FindRepositoryRoot(), "unity", "PigForge.WeldProbe", "replays", "boxing-glove-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, WriteJson(results), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log($"boxing glove probe -> {path}");
    }

    private sealed class Result
    {
        public string Id;
        public float GloveLength;
        public float Drive;
        public float ShootTime;
        public float TargetDistance;
        public float PeakDistanceY;
        public float PeakStepTime;
        public float DistanceAfterOneSecond;
        public float WindBackSeconds;
        public float MassDuringWind;
        public readonly List<float> LocalY = new List<float>();
    }

    private static Result Measure(string id, float gloveLength, float drive, float damper, float shootTime, float deviationX)
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }

        Result result = new Result
        {
            Id = id,
            GloveLength = gloveLength,
            Drive = drive,
            ShootTime = shootTime,
            TargetDistance = TargetDistanceY * gloveLength,
        };

        GameObject hostObject = new GameObject("host");
        GameObject gloveObject = new GameObject("glove");
        try
        {
            Rigidbody host = hostObject.AddComponent<Rigidbody>();
            host.isKinematic = true;
            host.useGravity = false;

            Rigidbody glove = gloveObject.AddComponent<Rigidbody>();
            glove.mass = GloveMass;
            glove.useGravity = false;               // the original's glove hangs on its drive, not gravity
            glove.drag = 0.2f;
            glove.angularDrag = 0.05f;
            glove.constraints = (RigidbodyConstraints)56;
            gloveObject.transform.position = hostObject.transform.position;
            // BoxingGlove*.prefab ships a SphereCollider radius 0.3 (tasks/bple-springs-report.json).
            gloveObject.AddComponent<SphereCollider>().radius = 0.3f;

            ConfigurableJoint joint = CreateGloveJoint(hostObject, gloveObject, drive, damper);

            // WindedUp: settle with the drive at its default target (0) -> the glove sits at home.
            for (int step = 0; step < 50; step++)
            {
                Step();
            }

            // Shoot: the original's own target position and the softened limit spring.
            joint.targetPosition = new Vector3(deviationX, TargetDistanceY * gloveLength, 0f);
            SoftJointLimitSpring spring = joint.linearLimitSpring;
            spring.spring = ShootLimitSpring;
            joint.linearLimitSpring = spring;

            float peak = 0f;
            float peakTime = 0f;
            int shootSteps = Mathf.RoundToInt(shootTime / FixedTimeStep);
            for (int step = 0; step < 150; step++)
            {
                Step();
                float localY = gloveObject.transform.localPosition.y;
                result.LocalY.Add(localY);
                if (Mathf.Abs(localY) > peak)
                {
                    peak = Mathf.Abs(localY);
                    peakTime = step * FixedTimeStep;
                }

                if (step == shootSteps - 1)
                {
                    result.DistanceAfterOneSecond = localY;
                }
            }

            result.PeakDistanceY = peak;
            result.PeakStepTime = peakTime;

            // Wind: back to the origin with the winding drive, the lighter mass and no collider.
            joint.targetPosition = Vector3.zero;
            JointDrive windDrive = joint.yDrive;
            windDrive.positionSpring = 10f * TargetDistanceY * gloveLength;
            windDrive.positionDamper = 2.5f;
            joint.yDrive = windDrive;
            glove.mass = WindMass;
            result.MassDuringWind = glove.mass;
            gloveObject.GetComponent<Collider>().enabled = false;

            int windSteps = -1;
            for (int step = 0; step < 400; step++)
            {
                Step();
                if (gloveObject.transform.localPosition.magnitude < 0.1f)
                {
                    windSteps = step;
                    break;
                }
            }

            result.WindBackSeconds = windSteps < 0 ? float.NaN : windSteps * FixedTimeStep;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gloveObject);
            UnityEngine.Object.DestroyImmediate(hostObject);
        }

        return result;
    }

    /// <summary>SpringBoxingGlove.InitilizeBoxingGlove (SpringBoxingGlove.cs:170-207), inlined.</summary>
    private static ConfigurableJoint CreateGloveJoint(GameObject host, GameObject glove, float drive, float damper)
    {
        ConfigurableJoint joint = glove.AddComponent<ConfigurableJoint>();
        joint.connectedBody = host.GetComponent<Rigidbody>();
        joint.angularXMotion = ConfigurableJointMotion.Locked;
        joint.angularYMotion = ConfigurableJointMotion.Locked;
        joint.angularZMotion = ConfigurableJointMotion.Locked;
        joint.xMotion = ConfigurableJointMotion.Locked;
        joint.yMotion = ConfigurableJointMotion.Limited;
        joint.zMotion = ConfigurableJointMotion.Locked;
        SoftJointLimitSpring limitSpring = default;
        limitSpring.spring = 0f;
        limitSpring.damper = 0f;
        SoftJointLimit limit = default;
        limit.limit = Limit;
        limit.bounciness = 0f;
        joint.linearLimit = limit;
        joint.linearLimitSpring = limitSpring;
        joint.yDrive = new JointDrive
        {
            positionSpring = drive,
            positionDamper = damper,
            maximumForce = float.MaxValue,
        };
        joint.xDrive = new JointDrive
        {
            positionSpring = 1000f,
            positionDamper = 5f,
            maximumForce = float.MaxValue,
        };
        joint.targetPosition = Vector3.zero;
        joint.projectionMode = JointProjectionMode.PositionAndRotation;
        joint.projectionDistance = 0.1f;
        joint.projectionAngle = 0f;
        joint.enablePreprocessing = false;
        return joint;
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
        Physics.autoSimulation = false;
#endif
    }

    private static void Step() => Physics.Simulate(FixedTimeStep);

    private static string WriteJson(List<Result> results)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.boxing-glove-probe\",\n");
        json.Append("  \"probeVersion\": 1,\n");
        json.Append("  \"reference\": \"unity 2021.3.45f2, real PhysX, the original's own SpringBoxingGlove joint setup\",\n");
        json.Append("  \"physics\": {\"fixedTimeStep\": 0.02, \"solverIterations\": 6, \"solverVelocityIterations\": 1},\n");
        json.Append("  \"targetDistanceY\": ").Append(Number(TargetDistanceY)).Append(",\n");
        json.Append("  \"prefabShootTime\": ").Append(Number(PrefabShootTime)).Append(",\n");
        json.Append("  \"windingTime\": ").Append(Number(WindingTime)).Append(",\n");
        json.Append("  \"prefabDrive\": ").Append(Number(PrefabDrive)).Append(",\n");
        json.Append("  \"prefabDriveDamper\": ").Append(Number(PrefabDriveDamper)).Append(",\n");
        json.Append("  \"prefabDeviationX\": ").Append(Number(PrefabDeviationX)).Append(",\n");
        json.Append("  \"gloveMass\": ").Append(Number(GloveMass)).Append(",\n");
        json.Append("  \"cells\": [\n");
        for (int index = 0; index < results.Count; index++)
        {
            Result result = results[index];
            json.Append("    {\n");
            json.Append("      \"id\": \"").Append(result.Id).Append("\",\n");
            json.Append("      \"boxingGloveLength\": ").Append(Number(result.GloveLength)).Append(",\n");
            json.Append("      \"drive\": ").Append(Number(result.Drive)).Append(",\n");
            json.Append("      \"shootTime\": ").Append(Number(result.ShootTime)).Append(",\n");
            json.Append("      \"targetDistance\": ").Append(Number(result.TargetDistance)).Append(",\n");
            json.Append("      \"peakDistanceY\": ").Append(Number(result.PeakDistanceY)).Append(",\n");
            json.Append("      \"peakStepTime\": ").Append(Number(result.PeakStepTime)).Append(",\n");
            json.Append("      \"distanceAfterOneSecond\": ").Append(Number(result.DistanceAfterOneSecond)).Append(",\n");
            json.Append("      \"windBackSeconds\": ").Append(Number(result.WindBackSeconds)).Append(",\n");
            json.Append("      \"massDuringWind\": ").Append(Number(result.MassDuringWind)).Append(",\n");
            json.Append("      \"localY\": ").Append(Series(result.LocalY, 5)).Append("\n");
            json.Append("    }");
            json.Append(index == results.Count - 1 ? "\n" : ",\n");
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
