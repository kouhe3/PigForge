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
/// Measures docs/specs/part-mirror.md (§5.1's algebra and §5.2's "the original side") on the
/// ORIGINAL's editor: Unity 2021.3.45f2, the version BPLE 2022.1.9/ProjectSettings/
/// ProjectVersion.txt pins, so the Quaternion arithmetic and the PhysX integration are the ones
/// the original actually ran on.
///
/// Part 1 -- the 8 build poses. Tail.SetRotation(int) (Tail.cs:106-121) decodes
///   `rotation = gridRotation * 2 + (flipped ? 1 : 0)` into `Quaternion.Euler(num3, num4, num5)`
/// with num2 = rotation % 8 / 2, flag = rotation % 2 == 1, num5 = 90 * num2,
/// num3 = (flag &amp;&amp; (num2 == 1 || num2 == 3)) ? 180 : 0, num4 = (flag &amp;&amp; (num2 == 0 || num2 == 2)) ? 180 : 0.
/// BasePart.SetFlipped(true) (BasePart.cs:639-651) instead writes AngleAxis(180, Vector3.up) and
/// BasePart.SetRotation(GridRotation) (BasePart.cs:829-832, GetRotationAngle above it) writes
/// AngleAxis(90 * num2, Vector3.forward). §5.1 claims the decoded pose equals
/// `Rz(90*num2) * Ry(180)`; every rotation 0..7 prints both quaternions, both sets of axes and the
/// dot/angle disagreement, so the claim is checked by the engine's own Quaternion rather than by
/// the hand matrix algebra in the spec.
///
/// Part 2 -- the wing/tail lift under PhysX. The two FixedUpdate bodies and their response curves
/// are transcribed verbatim:
///   Wings.cs:76-88 (curve), Wings.cs:91-102 (EnsureRigidbody), Wings.cs:104-118 (FixedUpdate)
///   Tail.cs:32-41 (curve), Tail.cs:44-55 (EnsureRigidbody), Tail.cs:57-75 (FixedUpdate)
///   ResponseCurve.cs:26-52 (AddPoint/Get, piecewise linear clamped at both ends)
/// with the prefab liftConstants (0.8 wooden wing, 1.5 metal wing, 0.2 wooden tail, 1.0 metal
/// tail; tasks/bple-aero-report.json). A bare GameObject (no collider, non-contact) carries a
/// Rigidbody at the origin: mass 1, no drag, interpolation None, 2.5D constraints 56
/// (BasePart.cs:1194-1196), gravity off so the lift is the only force. The part's local rotation is
/// assigned (plain yaw-0 and the mirrored pose AngleAxis(180, Vector3.up)), linearVelocity is set
/// to the probe velocity, the transcribed FixedUpdate runs, then exactly one
/// Physics.Simulate(0.02f) (the project's own TimeManager fixed step; autoSimulation is already
/// off). dv = velocity_after - velocity_before. Both raw dv and the per-SECOND force
/// `mass * dv / 0.02` are reported: PigForge's fixed tick is 60 Hz and the original's is 50 Hz, so
/// the newton value is the tick-independent quantity (ADR-013 decision 4).
///
/// Part 3 -- PigForge's expectation (src/PigForge.Core/Runtime/Aerodynamics.cs) computed by a
/// SEPARATE function that shares no code with the transcribed classes, so a transcription slip
/// inside this probe shows up as this probe's own two computations disagreeing. Per-row
/// disagreements are reported.
///
/// Output: replays/mirror-aero-probe.json.
/// Driven headlessly:
///   unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1800 \
///     -- -executeMethod PigForge.WeldProbe.Probe.MirrorAeroProbe.Run -logFile -
/// </summary>
public static class MirrorAeroProbe
{
    private const float FixedTimeStep = 0.02f;      // BPLE TimeManager "Fixed Timestep: 0.02"
    private const int Constraints25D = 56;          // freeze Z position + freeze X/Y rotation (BasePart.cs:1194-1196)
    private const float BodyMass = 1f;              // the probe body's mass (kg)
    private const float MaximumForce = 100f;        // Wings.cs:115 / Tail.cs:73 ClampMagnitude(_, 100f)
    private const float WingLiftWooden = 0.8f;      // Part_WoodenWings_*_SET liftConstant
    private const float TailLiftWooden = 0.2f;      // Part_WoodenTail_*_SET liftConstant
    private const float WingLiftMetal = 1.5f;       // Part_MetalWings_*_SET
    private const float TailLiftMetal = 1.0f;       // Part_MetalTail_*_SET
    private const float OriginalLinearDrag = 1f;    // Wings.cs:95 / Tail.cs:48
    private const float OriginalAngularDrag = 0.2f; // Wings.cs:96 / Tail.cs:49

    /// <summary>The 20-row matrix: 5 velocities x (Wings 0.8, Tail 0.2) x (plain, mirrored).</summary>
    private static readonly Vector3[] ProbeVelocities =
    {
        new Vector3(10f, 0f, 0f),
        new Vector3(10f, 5f, 0f),
        new Vector3(2f, 0f, 0f),
        new Vector3(30f, 0f, 0f),   // |v|^2 = 900, so the 100 N clamp must bite
        new Vector3(0f, 0f, 0f),
    };

    [MenuItem("PigForge/WeldProbe/Mirror Aero Probe")]
    public static void Run()
    {
        ApplyOriginalPhysicsSettings();

        List<PoseRow> poses = BuildPoses();

        Wings wing = new Wings();
        wing.Start();
        Tail tail = new Tail();
        tail.Start();

        List<AeroRow> rows = new List<AeroRow>();
        foreach (Vector3 velocity in ProbeVelocities)
        {
            rows.Add(Measure(wing, tail, "Wings", WingLiftWooden, false, velocity, 0f, 0f));
            rows.Add(Measure(wing, tail, "Wings", WingLiftWooden, true, velocity, 0f, 0f));
            rows.Add(Measure(wing, tail, "Tail", TailLiftWooden, false, velocity, 0f, 0f));
            rows.Add(Measure(wing, tail, "Tail", TailLiftWooden, true, velocity, 0f, 0f));
        }

        AeroRow damped = Measure(wing, tail, "Wings", WingLiftWooden, false, new Vector3(10f, 5f, 0f), OriginalLinearDrag, OriginalAngularDrag);

        foreach (PoseRow pose in poses)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[mirror-aero] pose rotation={0} gridRotation={1} flipped={2} euler=({3},{4},{5}) q=({6:0.#########},{7:0.#########},{8:0.#########},{9:0.#########}) right=({10:0.#########},{11:0.#########},{12:0.#########}) compositionRight=({13:0.#########},{14:0.#########},{15:0.#########}) mirrorDotRight={16:0.#########} mirrorAngle={17:0.#########} mirrorQD={18:0.#########} yawAngle={19:0.#########} yawQD={20:0.#########} mirrorAxesExact={21} mirrorQExact={22} yawAxesExact={23} yawQExact={24}",
                pose.Rotation, pose.Num2, pose.Flipped, pose.Num3, pose.Num4, pose.Num5,
                pose.Original.x, pose.Original.y, pose.Original.z, pose.Original.w,
                pose.OriginalRight.x, pose.OriginalRight.y, pose.OriginalRight.z,
                pose.CompositionRight.x, pose.CompositionRight.y, pose.CompositionRight.z,
                pose.DotRight, pose.MirrorAngleDegrees, pose.MirrorSignedComponentDiff, pose.YawAngleDegrees, pose.YawSignedComponentDiff,
                pose.MirrorAxesBitwiseEqual, pose.MirrorQuaternionBitwiseEqual, pose.YawAxesBitwiseEqual, pose.YawQuaternionBitwiseEqual));
        }

        foreach (AeroRow row in rows)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[mirror-aero] {0}: aoA={1:0.#######} coeff={2:0.#######} transcribed=({3:0.#########},{4:0.#########},{5:0.#########}) measured=({6:0.#########},{7:0.#########},{8:0.#########}) predicted=({9:0.#########},{10:0.#########},{11:0.#########}) clamped={12} measuredVsPredicted={13:0.#########} transcribedVsPredicted={14:0.#########}",
                row.Id, row.AttackAngleDegrees, row.Coefficient,
                row.TranscribedForce.x, row.TranscribedForce.y, row.TranscribedForce.z,
                row.MeasuredForce.x, row.MeasuredForce.y, row.MeasuredForce.z,
                row.PredictedForce.x, row.PredictedForce.y, row.PredictedForce.z,
                row.Clamped, row.MeasuredVsPredicted, row.TranscribedVsPredicted));
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[mirror-aero] dampedRow drag={0} angularDrag={1} dv=({2:0.#########},{3:0.#########},{4:0.#########}) measuredN=({5:0.#########},{6:0.#########},{7:0.#########}) liftThenDampN=({8:0.#########},{9:0.#########},{10:0.#########}) residual={11:0.#########}",
            damped.LinearDrag, damped.AngularDrag, damped.DeltaVelocity.x, damped.DeltaVelocity.y, damped.DeltaVelocity.z,
            damped.MeasuredForce.x, damped.MeasuredForce.y, damped.MeasuredForce.z,
            damped.IntegratorForce.x, damped.IntegratorForce.y, damped.IntegratorForce.z, damped.IntegratorResidual));

        string path = Path.Combine(FindRepositoryRoot(), "unity", "PigForge.WeldProbe", "replays", "mirror-aero-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, WriteJson(poses, rows, damped), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log("[mirror-aero] wrote " + path);
    }

    // =======================================================================================
    // Part 1: the 8 build poses (Tail.cs:106-121) against Rz(90 * num2) * Ry(180).
    // =======================================================================================

    private sealed class PoseRow
    {
        public int Rotation;
        public int Num2;                // gridRotation (GridRotation enum value, 0..3)
        public bool Flipped;            // flag = rotation % 2 == 1
        public int Num3;                // Euler x
        public int Num4;                // Euler y
        public int Num5;                // Euler z = 90 * num2

        public Quaternion Original;     // Quaternion.Euler(num3, num4, num5)  (Tail.cs:120)
        public Quaternion Yaw;          // AngleAxis(90 * num2, Vector3.forward) (BasePart.cs:829-832)
        public Quaternion Flip;         // AngleAxis(180, Vector3.up)            (BasePart.cs:639-651)
        public Quaternion Composition;  // Yaw * Flip = Rz(yaw) * Ry(180)

        public Vector3 OriginalRight;
        public Vector3 OriginalUp;
        public Vector3 OriginalForward;
        public Vector3 CompositionRight;
        public Vector3 CompositionUp;
        public Vector3 CompositionForward;

        public float DotRight;
        public float DotUp;
        public float DotForward;
        // Against the mirror composition Yaw * Flip = Rz(yaw) * Ry(180).
        public float MirrorQuaternionDot;
        public float MirrorAngleDegrees;
        public float MirrorSignedComponentDiff;    // double-cover aware: min over q and -q
        public float MirrorRawComponentDiff;       // raw: up to 2 when the two are q and -q
        public bool MirrorQuaternionBitwiseEqual;  // after the sign choice
        public bool MirrorAxesBitwiseEqual;
        // Against the plain build yaw Rz(yaw) on its own -- what the unflipped poses are.
        public float YawQuaternionDot;
        public float YawAngleDegrees;
        public float YawSignedComponentDiff;
        public float YawDotRight;
        public float YawDotUp;
        public float YawDotForward;
        public bool YawQuaternionBitwiseEqual;
        public bool YawAxesBitwiseEqual;
    }

    private static List<PoseRow> BuildPoses()
    {
        List<PoseRow> rows = new List<PoseRow>();
        for (int rotation = 0; rotation < 8; rotation++)
        {
            int num = rotation % 8;
            int num2 = num / 2;
            bool flag = num % 2 == 1;
            int num3 = ((flag && (num2 == 1 || num2 == 3)) ? 180 : 0);
            int num4 = ((flag && (num2 == 0 || num2 == 2)) ? 180 : 0);
            int num5 = 90 * num2;

            PoseRow row = new PoseRow
            {
                Rotation = rotation,
                Num2 = num2,
                Flipped = flag,
                Num3 = num3,
                Num4 = num4,
                Num5 = num5,
            };

            row.Original = Quaternion.Euler(num3, num4, num5);
            // GetRotationAngle(GridRotation) is exactly 90 * the enum value (BasePart.cs:820-833).
            row.Yaw = Quaternion.AngleAxis(num5, Vector3.forward);
            row.Flip = Quaternion.AngleAxis(180f, Vector3.up);
            row.Composition = row.Yaw * row.Flip;

            row.OriginalRight = row.Original * Vector3.right;
            row.OriginalUp = row.Original * Vector3.up;
            row.OriginalForward = row.Original * Vector3.forward;
            row.CompositionRight = row.Composition * Vector3.right;
            row.CompositionUp = row.Composition * Vector3.up;
            row.CompositionForward = row.Composition * Vector3.forward;

            Vector3 yawRight = row.Yaw * Vector3.right;
            Vector3 yawUp = row.Yaw * Vector3.up;
            Vector3 yawForward = row.Yaw * Vector3.forward;

            row.DotRight = Vector3.Dot(row.OriginalRight, row.CompositionRight);
            row.DotUp = Vector3.Dot(row.OriginalUp, row.CompositionUp);
            row.DotForward = Vector3.Dot(row.OriginalForward, row.CompositionForward);
            row.MirrorQuaternionDot = Quaternion.Dot(row.Original, row.Composition);
            row.MirrorAngleDegrees = Quaternion.Angle(row.Original, row.Composition);
            row.MirrorSignedComponentDiff = SignedComponentDiff(row.Original, row.Composition);
            row.MirrorRawComponentDiff = MaxAbsDifference(row.Original, row.Composition);
            row.MirrorQuaternionBitwiseEqual = SignedBitwiseEqual(row.Original, row.Composition);
            row.MirrorAxesBitwiseEqual =
                Identical(row.OriginalRight, row.CompositionRight) &&
                Identical(row.OriginalUp, row.CompositionUp) &&
                Identical(row.OriginalForward, row.CompositionForward);

            row.YawQuaternionDot = Quaternion.Dot(row.Original, row.Yaw);
            row.YawAngleDegrees = Quaternion.Angle(row.Original, row.Yaw);
            row.YawSignedComponentDiff = SignedComponentDiff(row.Original, row.Yaw);
            row.YawDotRight = Vector3.Dot(row.OriginalRight, yawRight);
            row.YawDotUp = Vector3.Dot(row.OriginalUp, yawUp);
            row.YawDotForward = Vector3.Dot(row.OriginalForward, yawForward);
            row.YawQuaternionBitwiseEqual = SignedBitwiseEqual(row.Original, row.Yaw);
            row.YawAxesBitwiseEqual =
                Identical(row.OriginalRight, yawRight) &&
                Identical(row.OriginalUp, yawUp) &&
                Identical(row.OriginalForward, yawForward);

            rows.Add(row);
        }
        return rows;
    }

    private static float MaxAbsDifference(Quaternion a, Quaternion b) => Mathf.Max(
        Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y)),
        Mathf.Max(Mathf.Abs(a.z - b.z), Mathf.Abs(a.w - b.w)));

    /// <summary>Component difference with the double cover handled: q and -q are one rotation.</summary>
    private static float SignedComponentDiff(Quaternion a, Quaternion b) => Mathf.Min(
        MaxAbsDifference(a, b),
        MaxAbsDifference(a, new Quaternion(-b.x, -b.y, -b.z, -b.w)));

    private static bool SignedBitwiseEqual(Quaternion a, Quaternion b) =>
        (a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w) ||
        (a.x == -b.x && a.y == -b.y && a.z == -b.z && a.w == -b.w);

    private static float MaxAbsDifference(Vector3 a, Vector3 b) =>
        Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));

    private static bool Identical(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

    // =======================================================================================
    // Part 2: the transcribed original classes.
    // =======================================================================================

    /// <summary>
    /// ResponseCurve.cs:8-52, transcribed verbatim: the DataPoint struct, AddPoint and the
    /// piecewise-linear Get with both ends clamped (the loop picks the first segment containing x).
    /// </summary>
    private sealed class ResponseCurve
    {
        private struct DataPoint
        {
            public float x;

            public float y;

            public DataPoint(float x, float y)
            {
                this.x = x;
                this.y = y;
            }
        }

        private List<DataPoint> data = new List<DataPoint>();

        public void AddPoint(float x, float value)
        {
            data.Add(new DataPoint(x, value));
        }

        public float Get(float x)
        {
            if (data.Count < 2)
            {
                return 0f;
            }
            DataPoint dataPoint = data[0];
            DataPoint dataPoint2 = data[data.Count - 1];
            if (x < dataPoint.x)
            {
                return dataPoint.y;
            }
            if (x > dataPoint2.x)
            {
                return dataPoint2.y;
            }
            for (int i = 0; i < data.Count - 1; i++)
            {
                if (x >= data[i].x && x <= data[i + 1].x)
                {
                    dataPoint = data[i];
                    dataPoint2 = data[i + 1];
                    break;
                }
            }
            return Mathf.Lerp(dataPoint.y, dataPoint2.y, (x - dataPoint.x) / (dataPoint2.x - dataPoint.x));
        }
    }

    /// <summary>
    /// Wings.cs: only the lift path. Fields the probe has no counterpart for (contraption,
    /// INSettings, m_enabled) are folded into the dropped gate documented in FixedUpdate; nothing
    /// inside the force math is touched. LastForce / LastAttackAngle / LastCoefficient are
    /// probe-only observation hooks assigned next to the original's own locals.
    /// </summary>
    private sealed class Wings
    {
        public float liftConstant = 0.8f;   // Wings.cs:5 (prefabs override with 0.8 wooden / 1.5 metal)
        public Vector3 WindVelocity;        // BasePart field; always zero here -- PigForge has no wind zones

        private readonly ResponseCurve liftCoefficients = new ResponseCurve();

        public Vector3 LastForce;
        public float LastAttackAngle;
        public float LastCoefficient;

        // Wings.cs:76-88
        public void Start()
        {
            liftCoefficients.AddPoint(-180f, 0f);
            liftCoefficients.AddPoint(-135f, -0.2f);
            liftCoefficients.AddPoint(-90f, 0f);
            liftCoefficients.AddPoint(-45f, -0.2f);
            liftCoefficients.AddPoint(-10f, 0f);
            liftCoefficients.AddPoint(10f, 1.5f);
            liftCoefficients.AddPoint(15f, 1.75f);
            liftCoefficients.AddPoint(19f, 0.8f);
            liftCoefficients.AddPoint(22f, 0.1f);
            liftCoefficients.AddPoint(45f, 0.2f);
            liftCoefficients.AddPoint(90f, 0f);
            liftCoefficients.AddPoint(135f, -0.2f);
            liftCoefficients.AddPoint(180f, 0f);
        }

        // Wings.cs:91-102
        public static void EnsureRigidbody(Rigidbody rigidbody, float mass)
        {
            rigidbody.constraints = (RigidbodyConstraints)56;
            rigidbody.mass = mass;
            rigidbody.drag = 1f;
            rigidbody.angularDrag = 0.2f;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        }

        // Wings.cs:104-118 verbatim, minus the gate
        // `if ((bool)base.contraption && base.contraption.IsRunning && (!INSettings.GetBool(INFeature.SwitchableWing) || m_enabled))`
        // -- the probe drives a live part, and SwitchableWing's declaration default is false, so
        // that gate is always true. `m_flipped` is passed in (the probe assigns the pose, and
        // Tail.SetRotation(int) sets m_flipped = flag).
        public void FixedUpdate(Rigidbody rigidbody, Transform transform, bool m_flipped)
        {
            Vector3 vector = rigidbody.velocity - WindVelocity;
            WindVelocity = Vector3.zero;
            Vector3 right = transform.right;
            float x = ((!m_flipped) ? 1f : (-1f)) * Mathf.Sign(Vector3.Cross(vector, right).z) * Vector3.Angle(vector, right);
            float num = liftCoefficients.Get(x);
            Vector3 vector2 = Vector3.Cross(transform.forward, vector.normalized);
            Vector3 vector3 = liftConstant * vector.sqrMagnitude * num * vector2;
            vector3 = Vector3.ClampMagnitude(vector3, 100f);
            rigidbody.AddForce(vector3, ForceMode.Force);

            LastAttackAngle = x;
            LastCoefficient = num;
            LastForce = vector3;
        }
    }

    /// <summary>Tail.cs, same reduction as <see cref="Wings"/>: the curve of Start (:32-41), the
    /// rigidbody defaults (:44-55) and FixedUpdate (:57-75) verbatim.</summary>
    private sealed class Tail
    {
        public float liftConstant = 0.2f;   // Tail.cs:6 (prefabs override with 0.2 wooden / 1.0 metal)
        public Vector3 WindVelocity;        // always zero here

        private readonly ResponseCurve liftCoefficients = new ResponseCurve();

        public Vector3 LastForce;
        public float LastAttackAngle;
        public float LastCoefficient;

        // Tail.cs:32-41
        public void Start()
        {
            liftCoefficients.AddPoint(-180f, 0f);
            liftCoefficients.AddPoint(-135f, -1.5f);
            liftCoefficients.AddPoint(-90f, 0f);
            liftCoefficients.AddPoint(-45f, -1.5f);
            liftCoefficients.AddPoint(-10f, 0f);
            liftCoefficients.AddPoint(10f, 1f);
            liftCoefficients.AddPoint(45f, 1.5f);
            liftCoefficients.AddPoint(90f, 0f);
            liftCoefficients.AddPoint(135f, -1.5f);
            liftCoefficients.AddPoint(180f, 0f);
        }

        // Tail.cs:44-55
        public static void EnsureRigidbody(Rigidbody rigidbody, float mass)
        {
            rigidbody.constraints = (RigidbodyConstraints)56;
            rigidbody.mass = mass;
            rigidbody.drag = 1f;
            rigidbody.angularDrag = 0.2f;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        }

        // Tail.cs:57-75 verbatim, minus the same always-true gate.
        public void FixedUpdate(Rigidbody rigidbody, Transform transform, bool m_flipped)
        {
            Vector3 vector = rigidbody.velocity - WindVelocity;
            WindVelocity = Vector3.zero;
            Vector3 right = transform.right;
            float num = ((!m_flipped) ? 1f : (-1f));
            float num2 = Vector3.Angle(new Vector3(num, 0f, 0f), right);
            num2 = Mathf.Sign(Vector3.Cross(new Vector3(1f, 0f, 0f), right).z) * num2;
            right = Quaternion.AngleAxis(0.4f * (num2 - 30f), transform.forward) * right;
            float x = num * Mathf.Sign(Vector3.Cross(vector, right).z) * Vector3.Angle(vector, right);
            float num3 = liftCoefficients.Get(x);
            Vector3 vector2 = Vector3.Cross(transform.forward, vector.normalized);
            Vector3 vector3 = liftConstant * vector.sqrMagnitude * num3 * vector2;
            vector3 = Vector3.ClampMagnitude(vector3, 100f);
            rigidbody.AddForce(vector3, ForceMode.Force);

            LastAttackAngle = x;
            LastCoefficient = num3;
            LastForce = vector3;
        }
    }

    // =======================================================================================
    // Part 2: one measurement = one body, one transcribed FixedUpdate, one Physics.Simulate.
    // =======================================================================================

    private sealed class AeroRow
    {
        public string Id;
        public string Part;                 // "Wings" | "Tail" (the original's class names)
        public float LiftConstant;
        public string Pose;                 // "plain_yaw0" | "mirrored_Ry180"
        public bool Mirrored;
        public Vector3 ProbeVelocity;
        public float LinearDrag;
        public float AngularDrag;
        public float AttackAngleDegrees;
        public float Coefficient;
        public Vector3 TranscribedForce;    // what the transcribed class handed to AddForce
        public Vector3 PredictedForce;      // PigForge's Aerodynamics, recomputed separately
        public Vector3 VelocityBefore;
        public Vector3 VelocityAfter;
        public Vector3 DeltaVelocity;
        public Vector3 MeasuredForce;       // mass * dv / dt, newtons (per second)
        public bool Clamped;
        public float MeasuredVsPredicted;
        public float TranscribedVsPredicted;
        public string BodyInterpolation;
        // Damped row only: the order BodyDefaultsProbe measured in PhysX 4.1 -- the accumulated
        // force is integrated first, then the velocity is scaled by (1 - drag * dt).
        public Vector3 IntegratorForce;
        public float IntegratorResidual;
        public Vector3 DampingOnlyDeltaVelocity;
        public string Note = string.Empty;
    }

    private static AeroRow Measure(Wings wing, Tail tail, string part, float liftConstant, bool mirrored, Vector3 velocity, float drag, float angularDrag)
    {
        ClearScene();

        GameObject probe = new GameObject("MirrorAeroBody");
        probe.transform.position = Vector3.zero;
        // Mirrored build pose = BasePart.SetFlipped(true) / Tail.SetRotation's flag branch.
        probe.transform.localRotation = mirrored ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.identity;

        Rigidbody body = probe.AddComponent<Rigidbody>();
        body.mass = BodyMass;
        body.drag = drag;
        body.angularDrag = angularDrag;
        body.useGravity = false;                                        // the lift is the only force
        body.interpolation = RigidbodyInterpolation.None;               // read the raw simulated velocity
        body.constraints = (RigidbodyConstraints)Constraints25D;        // BasePart.cs:1194-1196
        body.isKinematic = false;
        if (drag > 0f)
        {
            // The damped row goes through the original's own EnsureRigidbody
            // (Wings.cs:91-102 / Tail.cs:44-55): mass, drag 1, angularDrag 0.2, constraints 56,
            // interpolation Interpolate.
            if (part == "Wings")
            {
                Wings.EnsureRigidbody(body, BodyMass);
            }
            else
            {
                Tail.EnsureRigidbody(body, BodyMass);
            }
            body.useGravity = false;
        }
        body.velocity = velocity;

        Vector3 before = body.velocity;
        Vector3 transcribed;
        float attackAngle;
        float coefficient;
        if (part == "Wings")
        {
            wing.liftConstant = liftConstant;
            wing.FixedUpdate(body, probe.transform, mirrored);
            transcribed = wing.LastForce;
            attackAngle = wing.LastAttackAngle;
            coefficient = wing.LastCoefficient;
        }
        else
        {
            tail.liftConstant = liftConstant;
            tail.FixedUpdate(body, probe.transform, mirrored);
            transcribed = tail.LastForce;
            attackAngle = tail.LastAttackAngle;
            coefficient = tail.LastCoefficient;
        }

        Physics.Simulate(FixedTimeStep);
        Vector3 after = body.velocity;
        Vector3 deltaVelocity = after - before;

        AeroRow row = new AeroRow
        {
            Id = RowId(part, liftConstant, mirrored, velocity, drag),
            Part = part,
            LiftConstant = liftConstant,
            Pose = mirrored ? "mirrored_Ry180" : "plain_yaw0",
            Mirrored = mirrored,
            ProbeVelocity = velocity,
            LinearDrag = drag,
            AngularDrag = angularDrag,
            AttackAngleDegrees = attackAngle,
            Coefficient = coefficient,
            TranscribedForce = transcribed,
            PredictedForce = PredictForce(part, liftConstant, mirrored, velocity, probe.transform.rotation),
            VelocityBefore = before,
            VelocityAfter = after,
            DeltaVelocity = deltaVelocity,
            MeasuredForce = deltaVelocity * (BodyMass / FixedTimeStep),
            Clamped = Mathf.Abs(transcribed.magnitude - MaximumForce) < 1e-4f,
            BodyInterpolation = body.interpolation.ToString(),
        };
        row.TranscribedVsPredicted = (row.TranscribedForce - row.PredictedForce).magnitude;
        row.MeasuredVsPredicted = (row.MeasuredForce - row.PredictedForce).magnitude;

        if (drag > 0f)
        {
            // dv = ((before + F/m*dt) * (1 - drag*dt)) - before   (physx DyBodyCoreIntegrator)
            Vector3 afterModel = (before + (transcribed * (1f / BodyMass) * FixedTimeStep)) * (1f - (drag * FixedTimeStep));
            row.IntegratorForce = (afterModel - before) * (BodyMass / FixedTimeStep);
            row.IntegratorResidual = (row.MeasuredForce - row.IntegratorForce).magnitude;
            row.DampingOnlyDeltaVelocity = before * (-drag * FixedTimeStep);
            row.Note = "reference only: measuredForceNewtons already contains PhysX's own linear damping, so compare it against integratorForceNewtons (residual above), not against the drag-free predictedForceNewtons.";
        }

        UnityEngine.Object.DestroyImmediate(probe);
        return row;
    }

    private static string RowId(string part, float liftConstant, bool mirrored, Vector3 velocity, float drag)
    {
        string kind = part == "Wings" ? "wing" : "tail";
        string pose = mirrored ? "mirrored" : "plain";
        string dragTag = drag > 0f ? "_drag" + drag.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}_lift{1:0.##}_{2}{3}_v{4:0.##}_{5:0.##}_{6:0.##}",
            kind, liftConstant, pose, dragTag, velocity.x, velocity.y, velocity.z);
    }

    // =======================================================================================
    // Part 3: PigForge's prediction, a second independent implementation.
    // =======================================================================================

    /// <summary>
    /// src/PigForge.Core/Runtime/Aerodynamics.cs by hand (this project references no PigForge
    /// assembly): Force + WingAngleOfAttack/TailAngleOfAttack + WingCoefficient/TailCoefficient
    /// over docs/specs/part-mirror.md §2 rows 8/9/10, including `Sign(f) = f &gt;= 0 ? 1 : -1`.
    /// Shares no code with the transcribed Wings/Tail above.
    /// </summary>
    private static Vector3 PredictForce(string part, float liftConstant, bool mirrored, Vector3 velocity, Quaternion pose)
    {
        Vector3 right = pose * Vector3.right;
        Vector3 forward = pose * Vector3.forward;
        float coefficient;
        if (part == "Wings")
        {
            float sign = Sign(Vector3.Cross(velocity, right).z);
            float x = (mirrored ? -1f : 1f) * sign * Vector3.Angle(velocity, right);
            coefficient = Interpolate(WingCurveX, WingCurveY, x);
        }
        else
        {
            float num = mirrored ? -1f : 1f;
            float twist = Sign(Vector3.Cross(new Vector3(1f, 0f, 0f), right).z) * Vector3.Angle(new Vector3(num, 0f, 0f), right);
            right = Quaternion.AngleAxis(0.4f * (twist - 30f), forward) * right;
            float sign = Sign(Vector3.Cross(velocity, right).z);
            float x = num * sign * Vector3.Angle(velocity, right);
            coefficient = Interpolate(TailCurveX, TailCurveY, x);
        }

        float speedSquared = Vector3.Dot(velocity, velocity);
        if (speedSquared <= 0f || coefficient == 0f)
        {
            return Vector3.zero;
        }

        Vector3 lift = Vector3.Cross(forward, velocity.normalized) * (speedSquared * (liftConstant * coefficient));
        return Vector3.ClampMagnitude(lift, MaximumForce);
    }

    /// <summary>Unity's Mathf.Sign: 1 for zero (Aerodynamics.Sign).</summary>
    private static float Sign(float value) => value < 0f ? -1f : 1f;

    /// <summary>Aerodynamics.Interpolate: piecewise linear, clamped at both ends.</summary>
    private static float Interpolate(float[] xs, float[] ys, float x)
    {
        if (x <= xs[0])
        {
            return ys[0];
        }

        for (int index = 0; index < xs.Length - 1; index++)
        {
            if (x <= xs[index + 1])
            {
                float span = xs[index + 1] - xs[index];
                return ys[index] + ((ys[index + 1] - ys[index]) * ((x - xs[index]) / span));
            }
        }

        return ys[ys.Length - 1];
    }

    private static readonly float[] WingCurveX = { -180f, -135f, -90f, -45f, -10f, 10f, 15f, 19f, 22f, 45f, 90f, 135f, 180f };

    private static readonly float[] WingCurveY = { 0f, -0.2f, 0f, -0.2f, 0f, 1.5f, 1.75f, 0.8f, 0.1f, 0.2f, 0f, -0.2f, 0f };

    private static readonly float[] TailCurveX = { -180f, -135f, -90f, -45f, -10f, 10f, 45f, 90f, 135f, 180f };

    private static readonly float[] TailCurveY = { 0f, -1.5f, 0f, -1.5f, 0f, 1f, 1.5f, 0f, -1.5f, 0f };

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
        // Physics.Simulate per step is the same thing, and it is what BodyDefaultsProbe measures on.
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

    private static string WriteJson(List<PoseRow> poses, List<AeroRow> rows, AeroRow damped)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": \"pigforge.weldprobe.mirror-aero\",\n");
        json.Append("  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
        json.Append("  \"physics\": {\n");
        json.Append("    \"fixedTimeStep\": ").Append(N(FixedTimeStep)).Append(",\n");
        json.Append("    \"autoSimulation\": false,\n");
        json.Append("    \"gravity\": ").Append(V(Physics.gravity)).Append(",\n");
        json.Append("    \"bodyUseGravity\": false,\n");
        json.Append("    \"bodyMass\": ").Append(N(BodyMass)).Append(",\n");
        json.Append("    \"bodyConstraints\": ").Append(Constraints25D).Append(",\n");
        json.Append("    \"bodyInterpolation\": \"None\",\n");
        json.Append("    \"maximumForceNewtons\": ").Append(N(MaximumForce)).Append(",\n");
        json.Append("    \"bodyOrigin\": [0, 0, 0]\n");
        json.Append("  },\n");
        json.Append("  \"prefabLiftConstants\": { \"wingWooden\": ").Append(N(WingLiftWooden))
            .Append(", \"wingMetal\": ").Append(N(WingLiftMetal))
            .Append(", \"tailWooden\": ").Append(N(TailLiftWooden))
            .Append(", \"tailMetal\": ").Append(N(TailLiftMetal)).Append(" },\n");
        json.Append("  \"probeVelocities\": [");
        for (int index = 0; index < ProbeVelocities.Length; index++)
        {
            json.Append(index > 0 ? ", " : string.Empty).Append(V(ProbeVelocities[index]));
        }
        json.Append("],\n");
        json.Append("  \"velocityConvention\": \"the listed vector is the world velocity assigned to the body; at the plain yaw-0 pose the part frame IS the world frame, so it is also the part-local velocity there. The mirrored row applies the same world velocity to a Ry(180) body, which is exactly the docs/specs/part-mirror.md §5.2 comparison (same vx, opposite lift).\",\n");

        // ---- Part 1 ----
        json.Append("  \"buildPoses\": [\n");
        for (int index = 0; index < poses.Count; index++)
        {
            PoseRow pose = poses[index];
            json.Append("    {\n");
            json.Append("      \"rotation\": ").Append(pose.Rotation).Append(",\n");
            json.Append("      \"gridRotation\": ").Append(pose.Num2).Append(",\n");
            json.Append("      \"flipped\": ").Append(Bool(pose.Flipped)).Append(",\n");
            json.Append("      \"isMirroredPose\": ").Append(Bool(pose.Flipped)).Append(",\n");
            json.Append("      \"euler\": [").Append(pose.Num3).Append(", ").Append(pose.Num4).Append(", ").Append(pose.Num5).Append("],\n");
            json.Append("      \"quaternion\": ").Append(Q(pose.Original)).Append(",\n");
            json.Append("      \"right\": ").Append(V(pose.OriginalRight)).Append(",\n");
            json.Append("      \"up\": ").Append(V(pose.OriginalUp)).Append(",\n");
            json.Append("      \"forward\": ").Append(V(pose.OriginalForward)).Append(",\n");
            json.Append("      \"yawQuaternion\": ").Append(Q(pose.Yaw)).Append(",\n");
            json.Append("      \"flipQuaternion\": ").Append(Q(pose.Flip)).Append(",\n");
            json.Append("      \"compositionQuaternion\": ").Append(Q(pose.Composition)).Append(",\n");
            json.Append("      \"compositionRight\": ").Append(V(pose.CompositionRight)).Append(",\n");
            json.Append("      \"compositionUp\": ").Append(V(pose.CompositionUp)).Append(",\n");
            json.Append("      \"compositionForward\": ").Append(V(pose.CompositionForward)).Append(",\n");
            json.Append("      \"dotRight\": ").Append(NP(pose.DotRight)).Append(",\n");
            json.Append("      \"dotUp\": ").Append(NP(pose.DotUp)).Append(",\n");
            json.Append("      \"dotForward\": ").Append(NP(pose.DotForward)).Append(",\n");
            json.Append("      \"mirrorQuaternionDot\": ").Append(NP(pose.MirrorQuaternionDot)).Append(",\n");
            json.Append("      \"mirrorAngleDegrees\": ").Append(NP(pose.MirrorAngleDegrees)).Append(",\n");
            json.Append("      \"mirrorSignedComponentDiff\": ").Append(NP(pose.MirrorSignedComponentDiff)).Append(",\n");
            json.Append("      \"mirrorRawComponentDiff\": ").Append(NP(pose.MirrorRawComponentDiff)).Append(",\n");
            json.Append("      \"mirrorQuaternionBitwiseEqual\": ").Append(Bool(pose.MirrorQuaternionBitwiseEqual)).Append(",\n");
            json.Append("      \"mirrorAxesBitwiseEqual\": ").Append(Bool(pose.MirrorAxesBitwiseEqual)).Append(",\n");
            json.Append("      \"yawQuaternionDot\": ").Append(NP(pose.YawQuaternionDot)).Append(",\n");
            json.Append("      \"yawAngleDegrees\": ").Append(NP(pose.YawAngleDegrees)).Append(",\n");
            json.Append("      \"yawSignedComponentDiff\": ").Append(NP(pose.YawSignedComponentDiff)).Append(",\n");
            json.Append("      \"yawDotRight\": ").Append(NP(pose.YawDotRight)).Append(",\n");
            json.Append("      \"yawDotUp\": ").Append(NP(pose.YawDotUp)).Append(",\n");
            json.Append("      \"yawDotForward\": ").Append(NP(pose.YawDotForward)).Append(",\n");
            json.Append("      \"yawQuaternionBitwiseEqual\": ").Append(Bool(pose.YawQuaternionBitwiseEqual)).Append(",\n");
            json.Append("      \"yawAxesBitwiseEqual\": ").Append(Bool(pose.YawAxesBitwiseEqual)).Append("\n");
            json.Append(index == poses.Count - 1 ? "    }\n" : "    },\n");
        }
        json.Append("  ],\n");

        float maxMirrorAngleFlipped = 0f;
        float maxMirrorDiffFlipped = 0f;
        float minMirrorDotFlipped = 1f;
        bool mirrorBitsFlipped = true;
        bool mirrorAxesBitsFlipped = true;
        float maxYawAngleUnflipped = 0f;
        float maxYawDiffUnflipped = 0f;
        float minYawDotUnflipped = 1f;
        bool yawBitsUnflipped = true;
        bool yawAxesBitsUnflipped = true;
        float maxMirrorAngleUnflipped = 0f;
        foreach (PoseRow pose in poses)
        {
            if (pose.Flipped)
            {
                maxMirrorAngleFlipped = Mathf.Max(maxMirrorAngleFlipped, pose.MirrorAngleDegrees);
                maxMirrorDiffFlipped = Mathf.Max(maxMirrorDiffFlipped, pose.MirrorSignedComponentDiff);
                minMirrorDotFlipped = Mathf.Min(minMirrorDotFlipped, Mathf.Min(pose.DotRight, Mathf.Min(pose.DotUp, pose.DotForward)));
                mirrorBitsFlipped &= pose.MirrorQuaternionBitwiseEqual;
                mirrorAxesBitsFlipped &= pose.MirrorAxesBitwiseEqual;
            }
            else
            {
                maxYawAngleUnflipped = Mathf.Max(maxYawAngleUnflipped, pose.YawAngleDegrees);
                maxYawDiffUnflipped = Mathf.Max(maxYawDiffUnflipped, pose.YawSignedComponentDiff);
                minYawDotUnflipped = Mathf.Min(minYawDotUnflipped, Mathf.Min(pose.YawDotRight, Mathf.Min(pose.YawDotUp, pose.YawDotForward)));
                yawBitsUnflipped &= pose.YawQuaternionBitwiseEqual;
                yawAxesBitsUnflipped &= pose.YawAxesBitwiseEqual;
                maxMirrorAngleUnflipped = Mathf.Max(maxMirrorAngleUnflipped, pose.MirrorAngleDegrees);
            }
        }

        json.Append("  \"poseCheck\": {\n");
        json.Append("    \"flippedRotations\": [1, 3, 5, 7],\n");
        json.Append("    \"unflippedRotations\": [0, 2, 4, 6],\n");
        json.Append("    \"flippedPosesEqualRzYawTimesRy180WithinFloat\": ").Append(Bool(maxMirrorAngleFlipped <= 1e-3f)).Append(",\n");
        json.Append("    \"maxMirrorAngleDegreesFlipped\": ").Append(NP(maxMirrorAngleFlipped)).Append(",\n");
        json.Append("    \"maxMirrorSignedComponentDiffFlipped\": ").Append(NP(maxMirrorDiffFlipped)).Append(",\n");
        json.Append("    \"minMirrorAxisDotFlipped\": ").Append(NP(minMirrorDotFlipped)).Append(",\n");
        json.Append("    \"flippedQuaternionsBitwiseEqualToRzYawTimesRy180\": ").Append(Bool(mirrorBitsFlipped)).Append(",\n");
        json.Append("    \"flippedAxesBitwiseEqualToRzYawTimesRy180\": ").Append(Bool(mirrorAxesBitsFlipped)).Append(",\n");
        json.Append("    \"unflippedPosesEqualPlainRzYawWithinFloat\": ").Append(Bool(maxYawAngleUnflipped <= 1e-3f)).Append(",\n");
        json.Append("    \"maxYawAngleDegreesUnflipped\": ").Append(NP(maxYawAngleUnflipped)).Append(",\n");
        json.Append("    \"maxYawSignedComponentDiffUnflipped\": ").Append(NP(maxYawDiffUnflipped)).Append(",\n");
        json.Append("    \"minYawAxisDotUnflipped\": ").Append(NP(minYawDotUnflipped)).Append(",\n");
        json.Append("    \"unflippedQuaternionsBitwiseEqualToRzYaw\": ").Append(Bool(yawBitsUnflipped)).Append(",\n");
        json.Append("    \"unflippedAxesBitwiseEqualToRzYaw\": ").Append(Bool(yawAxesBitsUnflipped)).Append(",\n");
        json.Append("    \"maxMirrorAngleDegreesUnflipped\": ").Append(NP(maxMirrorAngleUnflipped)).Append(",\n");
        json.Append("    \"conclusion\": \"")
            .Append("every m_autoAlign: 2 (FlipVertically) build pose -- the 4 flipped rotations 1/3/5/7 -- is Yaw*Flip = Rz(90*num2)*Ry(180): max Quaternion.Angle ")
            .Append(NP(maxMirrorAngleFlipped)).Append(" deg, min axis dot ").Append(NP(minMirrorDotFlipped))
            .Append(", max sign-normalised component diff ").Append(NP(maxMirrorDiffFlipped))
            .Append(", all four bitwise identical after the double-cover sign choice = ")
            .Append(mirrorBitsFlipped ? "yes" : "no (float rounding in Quaternion.Euler)")
            .Append("; the 4 unflipped rotations 0/2/4/6 are plain Rz(90*num2) instead: max Quaternion.Angle ")
            .Append(NP(maxYawAngleUnflipped)).Append(" deg, max sign-normalised component diff ").Append(NP(maxYawDiffUnflipped))
            .Append(", bitwise identical = ").Append(yawBitsUnflipped ? "yes" : "no")
            .Append("; those unflipped poses are ").Append(NP(maxMirrorAngleUnflipped))
            .Append(" deg away from the mirror composition (they are not mirrored).\"");
        json.Append("\n  },\n");

        // ---- Part 2 ----
        json.Append("  \"aeroRows\": [\n");
        for (int index = 0; index < rows.Count; index++)
        {
            AppendRow(json, rows[index], "    ");
            json.Append(index == rows.Count - 1 ? "\n" : ",\n");
        }
        json.Append("  ],\n");

        float maxTranscribedVsPredicted = 0f;
        float maxMeasuredVsPredicted = 0f;
        float maxMeasuredVsTranscribed = 0f;
        List<string> clamped = new List<string>();
        foreach (AeroRow row in rows)
        {
            maxTranscribedVsPredicted = Mathf.Max(maxTranscribedVsPredicted, row.TranscribedVsPredicted);
            maxMeasuredVsPredicted = Mathf.Max(maxMeasuredVsPredicted, row.MeasuredVsPredicted);
            maxMeasuredVsTranscribed = Mathf.Max(maxMeasuredVsTranscribed, (row.MeasuredForce - row.TranscribedForce).magnitude);
            if (row.Clamped)
            {
                clamped.Add(row.Id);
            }
        }

        json.Append("  \"aeroCheck\": {\n");
        json.Append("    \"rowCount\": ").Append(rows.Count).Append(",\n");
        json.Append("    \"maxTranscribedVsPredictedNewtons\": ").Append(NP(maxTranscribedVsPredicted)).Append(",\n");
        json.Append("    \"maxMeasuredVsPredictedNewtons\": ").Append(NP(maxMeasuredVsPredicted)).Append(",\n");
        json.Append("    \"maxMeasuredVsTranscribedNewtons\": ").Append(NP(maxMeasuredVsTranscribed)).Append(",\n");
        json.Append("    \"clampedRowIds\": [");
        for (int index = 0; index < clamped.Count; index++)
        {
            json.Append(index > 0 ? ", " : string.Empty).Append('"').Append(clamped[index]).Append('"');
        }
        json.Append("]\n");
        json.Append("  },\n");

        json.Append("  \"dampedRow\": ");
        AppendRow(json, damped, "  ");
        json.Append(",\n");

        json.Append("  \"notes\": [\n");
        json.Append("    \"Part 1 is Tail.SetRotation(int) (Tail.cs:106-121) verbatim on the original editor's Quaternion; BasePart.SetFlipped(true) (BasePart.cs:639-651) is the Flip factor and BasePart.SetRotation(GridRotation) (BasePart.cs:829-832) the Yaw factor.\",\n");
        json.Append("    \"Part 2 transcribes Wings.cs:76-88/91-102/104-118, Tail.cs:32-41/44-55/57-75 and ResponseCurve.cs:26-52 verbatim (only the always-true contraption/INSettings gate is dropped, and probe-only observation hooks are assigned next to the original's locals).\",\n");
        json.Append("    \"The body has no collider and gravity is off, so the measured dv is the lift alone; the measurement rows carry drag 0, the damped row carries the original's drag 1 / angularDrag 0.2 (Wings.cs:95-96, Tail.cs:48-49).\",\n");
        json.Append("    \"measuredForceNewtons = mass * dv / 0.02 is a per-second force (ADR-013 decision 4): the original's tick is 0.02 s, PigForge's is 1/60 s.\",\n");
        json.Append("    \"The metal prefabs (wing 1.5, tail 1.0) are the same curves scaled by liftConstant and were not re-rowed; the 20 rows use the wooden 0.8 / 0.2.\",\n");
        json.Append("    \"predictedForceNewtons is PigForge's src/PigForge.Core/Runtime/Aerodynamics.cs recomputed by a separate function inside this probe, so transcribed-vs-predicted disagreement is a transcription check, not a physics claim.\",\n");
        json.Append("    \"q and -q are the same rotation: mirrorRawComponentDiff is the raw component difference (up to 2 when the engine returns the other sign), mirrorSignedComponentDiff takes the smaller of the q and -q differences, and mirrorAngleDegrees is Quaternion.Angle (0 means the same rotation).\"\n");
        json.Append("  ]\n");
        json.Append("}\n");
        return json.ToString();
    }

    private static void AppendRow(StringBuilder json, AeroRow row, string indent)
    {
        json.Append(indent).Append("{\n");
        json.Append(indent).Append("  \"id\": \"").Append(row.Id).Append("\",\n");
        json.Append(indent).Append("  \"part\": \"").Append(row.Part).Append("\",\n");
        json.Append(indent).Append("  \"liftConstant\": ").Append(N(row.LiftConstant)).Append(",\n");
        json.Append(indent).Append("  \"pose\": \"").Append(row.Pose).Append("\",\n");
        json.Append(indent).Append("  \"mirrored\": ").Append(Bool(row.Mirrored)).Append(",\n");
        json.Append(indent).Append("  \"probeVelocity\": ").Append(V(row.ProbeVelocity)).Append(",\n");
        json.Append(indent).Append("  \"drag\": ").Append(N(row.LinearDrag)).Append(",\n");
        json.Append(indent).Append("  \"angularDrag\": ").Append(N(row.AngularDrag)).Append(",\n");
        json.Append(indent).Append("  \"interpolation\": \"").Append(row.BodyInterpolation).Append("\",\n");
        json.Append(indent).Append("  \"attackAngleDegrees\": ").Append(NP(row.AttackAngleDegrees)).Append(",\n");
        json.Append(indent).Append("  \"coefficient\": ").Append(NP(row.Coefficient)).Append(",\n");
        json.Append(indent).Append("  \"transcribedForceNewtons\": ").Append(V(row.TranscribedForce)).Append(",\n");
        json.Append(indent).Append("  \"predictedForceNewtons\": ").Append(V(row.PredictedForce)).Append(",\n");
        json.Append(indent).Append("  \"velocityBefore\": ").Append(V(row.VelocityBefore)).Append(",\n");
        json.Append(indent).Append("  \"velocityAfter\": ").Append(V(row.VelocityAfter)).Append(",\n");
        json.Append(indent).Append("  \"deltaVelocity\": ").Append(V(row.DeltaVelocity)).Append(",\n");
        json.Append(indent).Append("  \"measuredForceNewtons\": ").Append(V(row.MeasuredForce)).Append(",\n");
        json.Append(indent).Append("  \"measuredForceMagnitudeNewtons\": ").Append(NP(row.MeasuredForce.magnitude)).Append(",\n");
        json.Append(indent).Append("  \"predictedForceMagnitudeNewtons\": ").Append(NP(row.PredictedForce.magnitude)).Append(",\n");
        json.Append(indent).Append("  \"clamped\": ").Append(Bool(row.Clamped)).Append(",\n");
        json.Append(indent).Append("  \"measuredVsPredictedNewtons\": ").Append(NP(row.MeasuredVsPredicted)).Append(",\n");
        json.Append(indent).Append("  \"transcribedVsPredictedNewtons\": ").Append(NP(row.TranscribedVsPredicted));
        if (row.LinearDrag > 0f)
        {
            json.Append(",\n");
            json.Append(indent).Append("  \"dampingOnlyDeltaVelocity\": ").Append(V(row.DampingOnlyDeltaVelocity)).Append(",\n");
            json.Append(indent).Append("  \"integratorForceNewtons\": ").Append(V(row.IntegratorForce)).Append(",\n");
            json.Append(indent).Append("  \"integratorResidualNewtons\": ").Append(NP(row.IntegratorResidual)).Append(",\n");
            json.Append(indent).Append("  \"note\": \"").Append(row.Note).Append("\"");
        }
        json.Append("\n").Append(indent).Append("}");
    }

    private static string Q(Quaternion value) =>
        "[" + NP(value.x) + ", " + NP(value.y) + ", " + NP(value.z) + ", " + NP(value.w) + "]";

    private static string V(Vector3 value) =>
        "[" + NP(value.x) + ", " + NP(value.y) + ", " + NP(value.z) + "]";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string N(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NP(float value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
}
