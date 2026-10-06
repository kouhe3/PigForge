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
/// Measures the original's motor-wheel drive on the ORIGINAL's editor: Unity 2021.3.45f2, the
/// version BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt pins, with the original's own
/// ProjectSettings (gravity, 50 Hz fixed step, solver iterations, bounce threshold) applied
/// exactly like BodyDefaultsProbe does.
///
/// The script under measurement is MotorWheel.FixedUpdate (MotorWheel.cs:264-330), transcribed
/// verbatim into DriveStep below, on a REAL PhysX rig made of the part's own prefab geometry
/// (Part_MotorWheel_01_SET.prefab: m_mass 1, m_force 50, SphereCollider radius 0.45 centre
/// (0,-0.075,0), SupportCollider box 0.6x0.2 offset (-0.05,0.4), WheelPivot at (0,-0.075,0)):
///
///   RaycastHit hitInfo;
///   bool num = Physics.Raycast(m_wheelPivot.transform.position, m_lastContactDirection,
///                              out hitInfo, m_radius + 0.1f);
///   if (num &amp;&amp; hitInfo.collider != m_supportCollider) {
///       float num2 = SpeedInDirection(base.transform.right);      // colliderRigidbody null on static ground
///       Vector3 vector = m_lastForceDirection = Vector3.Cross(hitInfo.normal, Vector3.forward);
///       m_hasContact = true;
///       if (m_enabled &amp;&amp; m_maximumSpeed > 0f &amp;&amp; num2 &lt; m_maximumSpeed &amp;&amp; num2 &gt; 0f - m_maximumSpeed) {
///           float f = Mathf.Pow(1f - Mathf.Abs(num2) / m_maximumSpeed, 0.5f);
///           float num3 = m_thrust * m_maximumForce * f;
///           base.rigidbody.AddForceAtPosition(num3 * vector, hitInfo.point, ForceMode.Force);
///       }
///       return;                                                   // grounded
///   }
///   m_hasContact = false;                                         // airborne: no drive at all
///
/// plus the per-FixedUpdate thrust ramp above it (MotorWheel.cs:266-271):
///   m_thrustTimer += Time.deltaTime; m_thrustTimer = Mathf.Min(m_thrustTimer, 1f);
///   m_thrust = Mathf.Pow(m_thrustTimer, 0.4f);
/// and InitializeEngine (MotorWheel.cs:99-104): m_maximumForce = m_force * enginePowerFactor,
/// m_maximumSpeed = 15f * enginePowerFactor. The probe isolates the drive from the power system
/// by holding enginePowerFactor at 1, so m_maximumForce = 50 and m_maximumSpeed = 15.
///
/// Substitution, stated because it is the probe's only departure from the original: the original
/// derives m_lastContactDirection in OnCollisionStay (MotorWheel.cs:112-123) from the first
/// contact that is not the SupportCollider, then re-raycasts along it. The probe knows its ground
/// is static and casts along -normal. At rest that is the same direction: the collision contact
/// IS the surface point, so the direction from the wheel to it converges to the surface normal.
/// The force the original applies does not depend on the ray direction anyway -- the tangent comes
/// from hitInfo.normal and the gate from the part's own right axis -- the ray direction only picks
/// the application point.
///
/// What each cell answers:
///   * slope cells (0/15/30/45/-30 degrees): is the drive the ground tangent
///     Cross(hitInfo.normal, Vector3.forward) or the wheel's own axis? (The wheel's yaw is held at
///     0 in those cells, so its right axis is world +X on every slope.)
///   * wheel90: the wheel part is rotated, so its right axis is no longer world X. This is
///     the gate-axis question: the original gates on SpeedInDirection(transform.right) and the
///     direction formula ignores the part's rotation entirely.
///   * centre cells: AddForceAtPosition at the body centre instead of hitInfo.point. It shows what
///     the application point buys on a WELDED wheel (the original's topology) and on a HINGED wheel
///     (PigForge's topology, ADR-008/009). A rigid body's linear response to an impulse does not
///     depend on the application point, so the difference is the torque.
///   * the slope and gate cells additionally freeze the Z rotation (constraints 120 instead of the
///     original's 56) so the bare wheel-plus-box rig cannot pitch itself over: the original's own
///     roll freedom has nothing to do with which direction the drive points, and letting the rig
///     tip would smear the gate reading it is supposed to isolate. The topology cells keep the
///     original's 56, because there the pitch IS part of what is being measured.
///   * flat_weld_wheel180 is not measured: flipping the part puts its SupportCollider below the
///     tire (the mount is 0.325 above the origin), so it rests on the ground, the raycast hits the
///     support and the original drives nothing at all. That is the original's own geometry, not a
///     probe choice, so the cell is left out rather than faked.
///
/// Every cell cross-checks the recorded force against an independently written expectation
/// (ExpectedTangent uses the hand-derived cross(n, z) = (n.y, -n.x, 0); the taper is written out
/// again) and reports the largest disagreement, so a transcription slip inside DriveStep shows up
/// as this probe's own two computations disagreeing.
///
/// Output: replays/motor-wheel-probe.json.
/// Driven headlessly:
///   unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1800 \
///     -- -executeMethod PigForge.WeldProbe.Probe.MotorWheelProbe.Run -logFile -
/// </summary>
public static class MotorWheelProbe
{
    // The original's project settings, resolved from this probe's own location
    // (<repo>/unity/PigForge.WeldProbe/Assets -> <repo>/../BPLE 2022.1.9/ProjectSettings).
    private const string OriginalSettingsFallback = @"C:\tmp\BAD_PIGGIES\BPLE 2022.1.9\ProjectSettings";

    // Part_MotorWheel_01_SET.prefab, read field by field (never guessed).
    private const float WheelMass = 1f;                     // m_mass: 1
    private const float WheelForce = 50f;                   // MotorWheel.m_force
    private const float EnginePowerFactor = 1f;             // isolate the drive from the power system
    private const float MaximumSpeedBase = 15f;             // InitializeEngine: 15 * enginePowerFactor
    private const float WheelRadius = 0.45f;                // SphereCollider m_Radius
    private const float WheelPivotOffsetY = -0.075f;        // WheelPivot localPosition / SphereCollider m_Center
    private const float SupportSizeX = 0.6f;
    private const float SupportSizeY = 0.2f;
    private const float SupportCenterX = -0.05f;
    private const float SupportCenterY = 0.4f;
    private const float RaycastPad = 0.1f;                  // m_radius + 0.1f
    private const float LinearDrag = 0.2f;                  // BasePart.EnsureRigidbody
    private const float AngularDrag = 0.05f;
    private const int Constraints25D = 56;                  // freeze Z position + freeze X/Y rotation
    private const float ChassisMass = 1f;
    private const float ChassisSize = 0.8f;

    public static void Run()
    {
        OriginalSettings original = ReadOriginalSettings();
        ApplyOriginalSettings(original);

        List<CellSpec> specs = new List<CellSpec>
        {
            new CellSpec("flat_weld_contact", 0f, 0f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("slope15_weld_contact", 15f, 0f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("slope30_weld_contact", 30f, 0f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("slope45_weld_contact", 45f, 0f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("slope-30_weld_contact", -30f, 0f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("flat_weld_wheel90_contact", 0f, 90f, hinged: false, applyAtContact: true, freezePitch: true, steps: 100),
            new CellSpec("flat_weld_centre", 0f, 0f, hinged: false, applyAtContact: false, freezePitch: false, steps: 100),
            new CellSpec("flat_hinge_contact", 0f, 0f, hinged: true, applyAtContact: true, freezePitch: false, steps: 100),
            new CellSpec("flat_hinge_centre", 0f, 0f, hinged: true, applyAtContact: false, freezePitch: false, steps: 100),
        };

        List<Cell> cells = new List<Cell>();
        foreach (CellSpec spec in specs)
        {
            Cell cell = RunCell(original, spec);
            cells.Add(cell);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[motor-wheel] {0}: groundedSteps={1}/{2} tangentFirst=({3:0.######},{4:0.######},{5:0.######}) tangentLast=({6:0.######},{7:0.######},{8:0.######}) gateTotal={9:0.######} gatePerpendicular={10:0.######} forceFirst={11:0.######} forceLast={12:0.######} maxForceError={13:0.########} wheelSpeedFinal={14:0.######} wheelOmegaFinal={15:0.######} chassisSpeedFinal={16:0.######} travel=({17:0.######},{18:0.######},{19:0.######}) thrustStep1={20:0.######} thrustStep25={21:0.######}",
                cell.Spec.Id, cell.GroundedSteps, cell.Spec.Steps, cell.TangentFirst.x, cell.TangentFirst.y, cell.TangentFirst.z,
                cell.TangentLast.x, cell.TangentLast.y, cell.TangentLast.z, cell.GateAlongPartRightFinal, cell.GateAlongWorldXFinal,
                cell.ForceFirst, cell.ForceLast, cell.MaxForceError, cell.WheelSpeedFinal, cell.WheelOmegaFinal,
                cell.ChassisSpeedFinal, cell.Travel.x, cell.Travel.y, cell.Travel.z,
                cell.ThrustAt(0), cell.ThrustAt(24)));
        }

        string outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "replays", "motor-wheel-probe.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, WriteJson(original, cells), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log("[motor-wheel] wrote " + outputPath);
    }

    // ---------------------------------------------------------------------------------------
    // The drive step, transcribed from MotorWheel.FixedUpdate (MotorWheel.cs:264-330).
    // ---------------------------------------------------------------------------------------

    private static Vector3 DriveStep(
        Rigidbody wheelBody,
        Transform wheelRoot,
        Vector3 wheelPivotPosition,
        Vector3 contactDirection,
        Collider supportCollider,
        float thrust,
        float maximumForce,
        float maximumSpeed,
        bool applyAtContact,
        out float gateReading,
        out float hitDistance,
        out Vector3 tangent,
        out Vector3 appliedPoint,
        out Vector3 hitNormal,
        out string hitCollider)
    {
        gateReading = 0f;
        hitDistance = 0f;
        tangent = Vector3.zero;
        appliedPoint = Vector3.zero;
        hitNormal = Vector3.zero;
        hitCollider = "none";

        RaycastHit hitInfo;
        bool hit = Physics.Raycast(wheelPivotPosition, contactDirection, out hitInfo, WheelRadius + RaycastPad);
        if (!hit || hitInfo.collider == supportCollider)
        {
            return Vector3.zero;                       // airborne: MotorWheel.FixedUpdate returns without driving
        }

        // SpeedInDirection(base.transform.right); colliderRigidbody is null because the ground is static.
        gateReading = Vector3.Dot(wheelBody.velocity, wheelRoot.right);
        hitDistance = hitInfo.distance;
        hitNormal = hitInfo.normal;
        hitCollider = hitInfo.collider.name;
        tangent = Vector3.Cross(hitInfo.normal, Vector3.forward);

        if (gateReading >= maximumSpeed || gateReading <= -maximumSpeed)
        {
            return Vector3.zero;                       // the original's `num2 < m_maximumSpeed && num2 > -m_maximumSpeed`
        }

        float taper = Mathf.Pow(1f - Mathf.Abs(gateReading) / maximumSpeed, 0.5f);
        float magnitude = thrust * maximumForce * taper;
        appliedPoint = applyAtContact ? hitInfo.point : wheelBody.position;
        Vector3 force = magnitude * tangent;
        wheelBody.AddForceAtPosition(force, appliedPoint, ForceMode.Force);
        return force;
    }

    /// <summary>Hand-derived cross(normal, Vector3.forward) = (normal.y, -normal.x, 0), written out
    /// again so the probe's own expectation does not call the same engine function the transcription
    /// calls.</summary>
    private static Vector3 ExpectedTangent(Vector3 normal) => new Vector3(normal.y, -normal.x, 0f).normalized;

    /// <summary>The original's 2.5D constraints (freeze Z position + X/Y rotation), plus the frozen
    /// Z rotation of the isolating cells.</summary>
    private static int Constraints(CellSpec spec) => spec.FreezePitch ? Constraints25D | 64 : Constraints25D;

    /// <summary>The expected applied force and its direction, computed from the normal the raycast
    /// reported and the transcribed formula's taper, independently of DriveStep.</summary>
    private static Vector3 ExpectedForce(Vector3 normal, float thrust, float maximumForce, float maximumSpeed, float gateReading)
    {
        if (gateReading >= maximumSpeed || gateReading <= -maximumSpeed)
        {
            return Vector3.zero;
        }

        float taper = Mathf.Pow(1f - Mathf.Abs(gateReading) / maximumSpeed, 0.5f);
        return thrust * maximumForce * taper * ExpectedTangent(normal);
    }

    // ---------------------------------------------------------------------------------------
    // Cells.
    // ---------------------------------------------------------------------------------------

    private sealed class CellSpec
    {
        public CellSpec(string id, float slopeDegrees, float wheelYawDegrees, bool hinged, bool applyAtContact, bool freezePitch, int steps)
        {
            Id = id;
            SlopeDegrees = slopeDegrees;
            WheelYawDegrees = wheelYawDegrees;
            Hinged = hinged;
            ApplyAtContact = applyAtContact;
            FreezePitch = freezePitch;
            Steps = steps;
        }

        public string Id { get; }
        public float SlopeDegrees { get; }
        public float WheelYawDegrees { get; }
        public bool Hinged { get; }
        public bool ApplyAtContact { get; }
        public bool FreezePitch { get; }
        public int Steps { get; }
    }

    private sealed class Cell
    {
        public CellSpec Spec;
        public Vector3 GroundNormal;
        public float MaximumForce;
        public float MaximumSpeed;
        public int GroundedSteps;
        public readonly List<float> Thrust = new List<float>();
        public readonly List<float> GateAlongPartRight = new List<float>();
        public readonly List<float> GateAlongWorldXSameStep = new List<float>();
        public readonly List<Vector3> WheelRight = new List<Vector3>();
        public readonly List<float> HitDistance = new List<float>();
        public readonly List<Vector3> HitNormal = new List<Vector3>();
        public readonly List<string> HitCollider = new List<string>();
        public readonly List<Vector3> Tangent = new List<Vector3>();
        public readonly List<Vector3> AppliedForce = new List<Vector3>();
        public readonly List<Vector3> AppliedPoint = new List<Vector3>();
        public readonly List<Vector3> WheelSpeed = new List<Vector3>();
        public readonly List<Vector3> ChassisSpeed = new List<Vector3>();
        public readonly List<Vector3> WheelOmega = new List<Vector3>();
        public float MaxForceError;
        public Vector3 TangentFirst;
        public Vector3 TangentLast;
        public float GateAlongPartRightFinal;
        public float GateAlongWorldXFinal;
        public float ForceFirst;
        public float ForceLast;
        public float WheelSpeedFinal;
        public float WheelOmegaFinal;
        public float MaxWheelOmega;
        public float ChassisSpeedFinal;
        public Vector3 Travel;

        public float ThrustAt(int index) => index < Thrust.Count ? Thrust[index] : 0f;
    }

    private static Cell RunCell(OriginalSettings settings, CellSpec spec)
    {
        ClearScene();

        float dt = settings.FixedTimeStep;
        Cell cell = new Cell
        {
            Spec = spec,
            MaximumForce = WheelForce * EnginePowerFactor,
            MaximumSpeed = MaximumSpeedBase * EnginePowerFactor,
        };

        float slope = spec.SlopeDegrees * Mathf.Deg2Rad;
        // Rises to the right for positive angles: the surface normal tilts towards -X.
        Vector3 normal = new Vector3(-Mathf.Sin(slope), Mathf.Cos(slope), 0f);
        cell.GroundNormal = normal;

        // Static ground: a 60 x 1 x 4 box whose top face contains the origin.
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.GetComponent<MeshRenderer>().enabled = false;
        ground.transform.localScale = new Vector3(60f, 1f, 4f);
        ground.transform.rotation = Quaternion.Euler(0f, 0f, spec.SlopeDegrees);
        ground.transform.position = -normal * 0.5f;

        // The wheel part root. Its SphereCollider sits at local (0, -0.075, 0) with radius 0.45,
        // so the root goes where the sphere centre lands on the surface.
        Vector3 sphereCentre = normal * WheelRadius;
        Vector3 localPivot = new Vector3(0f, WheelPivotOffsetY, 0f);
        Quaternion wheelRotation = Quaternion.Euler(0f, 0f, spec.WheelYawDegrees);

        GameObject wheel = new GameObject("MotorWheel");
        wheel.transform.rotation = wheelRotation;
        wheel.transform.position = sphereCentre - wheelRotation * localPivot;

        SphereCollider tire = wheel.AddComponent<SphereCollider>();
        tire.radius = WheelRadius;
        tire.center = localPivot;

        GameObject support = new GameObject("SupportCollider");
        support.transform.SetParent(wheel.transform, worldPositionStays: false);
        support.transform.localPosition = localPivot;
        BoxCollider supportCollider = support.AddComponent<BoxCollider>();
        supportCollider.size = new Vector3(SupportSizeX, SupportSizeY, 1f);
        supportCollider.center = new Vector3(SupportCenterX, SupportCenterY, 0f);

        GameObject pivot = new GameObject("WheelPivot");
        pivot.transform.SetParent(wheel.transform, worldPositionStays: false);
        pivot.transform.localPosition = localPivot;

        Rigidbody wheelBody = wheel.AddComponent<Rigidbody>();
        wheelBody.mass = WheelMass;
        wheelBody.drag = LinearDrag;
        wheelBody.angularDrag = AngularDrag;
        wheelBody.useGravity = true;
        wheelBody.interpolation = RigidbodyInterpolation.None;
        wheelBody.constraints = (RigidbodyConstraints)Constraints(spec);

        GameObject chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chassis.name = "Chassis";
        chassis.GetComponent<MeshRenderer>().enabled = false;
        chassis.transform.localScale = new Vector3(ChassisSize, ChassisSize, ChassisSize);
        chassis.transform.position = wheel.transform.position + new Vector3(0f, 0.6f, 0f);
        Rigidbody chassisBody = chassis.AddComponent<Rigidbody>();
        chassisBody.mass = ChassisMass;
        chassisBody.drag = LinearDrag;
        chassisBody.angularDrag = AngularDrag;
        chassisBody.useGravity = true;
        chassisBody.interpolation = RigidbodyInterpolation.None;
        chassisBody.constraints = (RigidbodyConstraints)Constraints(spec);

        if (spec.Hinged)
        {
            // PigForge's topology (ADR-008/009): the wheel rides its own body on a revolute joint
            // anchored at the tire centre.
            HingeJoint hinge = wheel.AddComponent<HingeJoint>();
            hinge.connectedBody = chassisBody;
            hinge.axis = Vector3.forward;
            hinge.anchor = localPivot;
            hinge.autoConfigureConnectedAnchor = true;
            hinge.useLimits = false;
            hinge.useSpring = false;
        }
        else
        {
            // The original's topology: every adjacent pair is one joint, and a wheel that does not
            // override CustomConnectToPart is welded rigidly.
            FixedJoint weld = wheel.AddComponent<FixedJoint>();
            weld.connectedBody = chassisBody;
            weld.autoConfigureConnectedAnchor = true;
        }

        Vector3 start = wheel.transform.position;
        float thrustTimer = 0f;

        for (int step = 0; step < spec.Steps; step++)
        {
            // MotorWheel.FixedUpdate's ramp, above the raycast.
            thrustTimer = Mathf.Min(thrustTimer + dt, 1f);
            float thrust = Mathf.Pow(thrustTimer, 0.4f);
            cell.Thrust.Add(thrust);

            float gateReading;
            float hitDistance;
            Vector3 tangent;
            Vector3 appliedPoint;
            Vector3 hitNormal;
            string hitCollider;
            Vector3 appliedForce = DriveStep(
                wheelBody,
                wheel.transform,
                pivot.transform.position,
                -normal,
                supportCollider,
                thrust,
                cell.MaximumForce,
                cell.MaximumSpeed,
                spec.ApplyAtContact,
                out gateReading,
                out hitDistance,
                out tangent,
                out appliedPoint,
                out hitNormal,
                out hitCollider);

            float worldGate = Vector3.Dot(wheelBody.velocity, Vector3.right);
            bool grounded = tangent != Vector3.zero;
            if (grounded)
            {
                cell.GroundedSteps++;
                cell.Tangent.Add(tangent);
                cell.HitDistance.Add(hitDistance);
                cell.HitNormal.Add(hitNormal);
                cell.HitCollider.Add(hitCollider);
                cell.GateAlongPartRight.Add(gateReading);
                cell.GateAlongWorldXSameStep.Add(worldGate);
                cell.WheelRight.Add(wheel.transform.right);
                cell.AppliedForce.Add(appliedForce);
                cell.AppliedPoint.Add(appliedPoint);
                Vector3 expected = ExpectedForce(hitNormal, thrust, cell.MaximumForce, cell.MaximumSpeed, gateReading);
                cell.MaxForceError = Mathf.Max(cell.MaxForceError, (appliedForce - expected).magnitude);
            }

            Physics.Simulate(dt);

            cell.WheelSpeed.Add(wheelBody.velocity);
            cell.WheelOmega.Add(wheelBody.angularVelocity);
            cell.ChassisSpeed.Add(chassisBody.velocity);
            cell.MaxWheelOmega = Mathf.Max(cell.MaxWheelOmega, wheelBody.angularVelocity.magnitude);
        }

        cell.Travel = wheel.transform.position - start;
        if (cell.Tangent.Count > 0)
        {
            cell.TangentFirst = cell.Tangent[0];
            cell.TangentLast = cell.Tangent[cell.Tangent.Count - 1];
            cell.GateAlongPartRightFinal = cell.GateAlongPartRight[cell.GateAlongPartRight.Count - 1];
            cell.GateAlongWorldXFinal = cell.GateAlongWorldXSameStep[cell.GateAlongWorldXSameStep.Count - 1];
            cell.ForceFirst = cell.AppliedForce[0].magnitude;
            cell.ForceLast = cell.AppliedForce[cell.AppliedForce.Count - 1].magnitude;
        }

        cell.WheelSpeedFinal = wheelBody.velocity.magnitude;
        cell.WheelOmegaFinal = wheelBody.angularVelocity.magnitude;
        cell.ChassisSpeedFinal = chassisBody.velocity.magnitude;

        return cell;
    }

    private static void ClearScene()
    {
        foreach (GameObject stray in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            UnityEngine.Object.DestroyImmediate(stray);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The original's own project settings, parsed from the original's files (same reader as
    // BodyDefaultsProbe: no value is guessed).
    // ---------------------------------------------------------------------------------------

    private sealed class OriginalSettings
    {
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
        string relative = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "BPLE 2022.1.9", "ProjectSettings"));
        if (Directory.Exists(relative))
        {
            return relative;
        }
        if (Directory.Exists(OriginalSettingsFallback))
        {
            return OriginalSettingsFallback;
        }
        throw new DirectoryNotFoundException("MotorWheelProbe could not find the original's ProjectSettings (tried " + relative + " and " + OriginalSettingsFallback + ").");
    }

    private static string ReadAssetOrThrow(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MotorWheelProbe reads the original's physics settings from the original's own files; missing: " + path);
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
                throw new InvalidDataException("MotorWheelProbe cannot parse " + key + " = '" + value + "' in " + path);
            }
            return parsed;
        }
        throw new InvalidDataException("MotorWheelProbe found no " + key + " in " + path);
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
        throw new InvalidDataException("MotorWheelProbe found no " + key + " in " + path);
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
    // Output.
    // ---------------------------------------------------------------------------------------

    private static string WriteJson(OriginalSettings settings, List<Cell> cells)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"probe\": \"motor-wheel\",\n");
        json.Append("  \"editor\": \"Unity 2021.3.45f2 (BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt)\",\n");
        json.Append("  \"source\": \"MotorWheel.cs:99-104 (InitializeEngine), :112-123 (OnCollisionStay), :264-330 (FixedUpdate)\",\n");
        json.Append("  \"prefab\": \"Part_MotorWheel_01_SET.prefab (m_mass 1, m_force 50, SphereCollider r 0.45 centre (0,-0.075,0), SupportCollider box 0.6x0.2 centre (-0.05,0.4), WheelPivot (0,-0.075,0))\",\n");
        json.Append("  \"isolation\": \"enginePowerFactor is held at 1, so m_maximumForce = m_force * 1 = 50 and m_maximumSpeed = 15 * 1 = 15; the power system itself is out of scope.\",\n");
        json.Append("  \"substitution\": \"m_lastContactDirection is derived by the probe as -groundNormal instead of from OnCollisionStay; the ground is static, so that is the direction the original's own collision contact converges to (the contact IS the surface point).\",\n");
        json.Append("  \"settings\": {\n");
        json.Append("    \"dt\": ").Append(N(settings.FixedTimeStep)).Append(",\n");
        json.Append("    \"gravity\": [").Append(N(settings.Gravity.x)).Append(", ").Append(N(settings.Gravity.y)).Append(", ").Append(N(settings.Gravity.z)).Append("],\n");
        json.Append("    \"bounceThreshold\": ").Append(N(settings.BounceThreshold)).Append(",\n");
        json.Append("    \"solverIterations\": ").Append(settings.SolverIterations).Append(",\n");
        json.Append("    \"solverVelocityIterations\": ").Append(settings.SolverVelocityIterations).Append(",\n");
        json.Append("    \"defaultMaxAngularSpeed\": ").Append(N(settings.MaxAngularSpeed)).Append("\n");
        json.Append("  },\n");
        json.Append("  \"cells\": [\n");
        for (int index = 0; index < cells.Count; index++)
        {
            AppendCell(json, cells[index]);
            json.Append(index == cells.Count - 1 ? "\n" : ",\n");
        }
        json.Append("  ],\n");

        json.Append("  \"summary\": {\n");
        json.Append("    \"forceDirectionIsGroundTangent\": [\n");
        foreach (Cell cell in cells)
        {
            json.Append("      {\"id\": \"").Append(cell.Spec.Id).Append("\", \"slopeDegrees\": ").Append(N(cell.Spec.SlopeDegrees))
                .Append(", \"groundNormal\": [").Append(N(cell.GroundNormal.x)).Append(", ").Append(N(cell.GroundNormal.y)).Append(", ").Append(N(cell.GroundNormal.z)).Append("]")
                .Append(", \"tangent\": [").Append(N(cell.TangentFirst.x)).Append(", ").Append(N(cell.TangentFirst.y)).Append(", ").Append(N(cell.TangentFirst.z)).Append("]")
                .Append(", \"forceFirst\": ").Append(N(cell.ForceFirst)).Append("}");
            json.Append(cell == cells[cells.Count - 1] ? "\n" : ",\n");
        }
        json.Append("    ],\n");
        json.Append("    \"thrustRamp\": ").Append(Series(cells[0].Thrust, 1)).Append(",\n");
        json.Append("    \"thrustRampIndex\": \"index i is thrust at step i+1 (0-based); the ramp is m_thrustTimer += dt capped at 1, m_thrust = pow(timer, 0.4)\",\n");
        json.Append("    \"maxForceErrorAcrossCells\": ").Append(NPrecise(MaxForceError(cells))).Append(",\n");
        json.Append("    \"gateAxisIsPartRight\": \"flat_weld_wheel90_contact rotates the wheel 90 degrees about Z, so transform.right is world +Y while world X still points along the travel; comparing gateAlongPartRight with gateAlongWorldXSameStep shows which axis the original read\",\n");
        json.Append("    \"directionIgnoresPartRotation\": \"flat_weld_contact and flat_weld_wheel90_contact share the same ground and the same tangent, although the wheel's own right axis differs by 90 degrees\",\n");
        json.Append("    \"applicationPoint\": \"flat_weld_contact (hitInfo.point) vs flat_weld_centre (body centre): a rigid body's linear response is the same, the difference is the torque\",\n");
        json.Append("    \"topology\": \"flat_hinge_* repeat the drive on PigForge's topology (revolute joint at the tire centre, ADR-008/009)\"\n");
        json.Append("  }\n}\n");
        return json.ToString();
    }

    private static void AppendCell(StringBuilder json, Cell cell)
    {
        json.Append("    {\n");
        json.Append("      \"id\": \"").Append(cell.Spec.Id).Append("\",\n");
        json.Append("      \"slopeDegrees\": ").Append(N(cell.Spec.SlopeDegrees)).Append(",\n");
        json.Append("      \"wheelYawDegrees\": ").Append(N(cell.Spec.WheelYawDegrees)).Append(",\n");
        json.Append("      \"topology\": \"").Append(cell.Spec.Hinged ? "hinge" : "weld").Append("\",\n");
        json.Append("      \"applicationPoint\": \"").Append(cell.Spec.ApplyAtContact ? "contact" : "bodyCentre").Append("\",\n");
        json.Append("      \"frozenPitch\": ").Append(cell.Spec.FreezePitch ? "true" : "false").Append(",\n");
        json.Append("      \"steps\": ").Append(cell.Spec.Steps).Append(",\n");
        json.Append("      \"groundNormal\": [").Append(N(cell.GroundNormal.x)).Append(", ").Append(N(cell.GroundNormal.y)).Append(", ").Append(N(cell.GroundNormal.z)).Append("],\n");
        json.Append("      \"maximumForce\": ").Append(N(cell.MaximumForce)).Append(",\n");
        json.Append("      \"maximumSpeed\": ").Append(N(cell.MaximumSpeed)).Append(",\n");
        json.Append("      \"groundedSteps\": ").Append(cell.GroundedSteps).Append(",\n");
        json.Append("      \"maxForceError\": ").Append(NPrecise(cell.MaxForceError)).Append(",\n");
        json.Append("      \"tangentFirst\": ").Append(V(cell.TangentFirst)).Append(",\n");
        json.Append("      \"tangentLast\": ").Append(V(cell.TangentLast)).Append(",\n");
        json.Append("      \"forceFirst\": ").Append(N(cell.ForceFirst)).Append(",\n");
        json.Append("      \"forceLast\": ").Append(N(cell.ForceLast)).Append(",\n");
        json.Append("      \"gateAlongPartRightFinal\": ").Append(N(cell.GateAlongPartRightFinal)).Append(",\n");
        json.Append("      \"gateAlongWorldXFinal\": ").Append(N(cell.GateAlongWorldXFinal)).Append(",\n");
        json.Append("      \"wheelSpeedFinal\": ").Append(N(cell.WheelSpeedFinal)).Append(",\n");
        json.Append("      \"wheelOmegaFinal\": ").Append(N(cell.WheelOmegaFinal)).Append(",\n");
        json.Append("      \"maxWheelOmega\": ").Append(N(cell.MaxWheelOmega)).Append(",\n");
        json.Append("      \"chassisSpeedFinal\": ").Append(N(cell.ChassisSpeedFinal)).Append(",\n");
        json.Append("      \"travel\": ").Append(V(cell.Travel)).Append(",\n");
        json.Append("      \"thrust\": ").Append(Series(cell.Thrust, 1)).Append(",\n");
        json.Append("      \"gateAlongPartRight\": ").Append(Series(cell.GateAlongPartRight, 1)).Append(",\n");
        json.Append("      \"hitDistance\": ").Append(Series(cell.HitDistance, 1)).Append(",\n");
        json.Append("      \"hitNormal\": ").Append(VectorSeries(cell.HitNormal, 1)).Append(",\n");
        json.Append("      \"hitCollider\": [").Append(string.Join(", ", cell.HitCollider.ConvertAll(name => "\"" + name + "\""))).Append("],\n");
        json.Append("      \"wheelRightAxis\": ").Append(VectorSeries(cell.WheelRight, 1)).Append(",\n");
        json.Append("      \"gateAlongWorldXSameStep\": ").Append(Series(cell.GateAlongWorldXSameStep, 1)).Append(",\n");
        json.Append("      \"appliedForce\": ").Append(VectorSeries(cell.AppliedForce, 1)).Append(",\n");
        json.Append("      \"appliedPoint\": ").Append(VectorSeries(cell.AppliedPoint, 1)).Append(",\n");
        json.Append("      \"wheelVelocity\": ").Append(VectorSeries(cell.WheelSpeed, 1)).Append(",\n");
        json.Append("      \"wheelAngularVelocity\": ").Append(VectorSeries(cell.WheelOmega, 1)).Append(",\n");
        json.Append("      \"chassisVelocity\": ").Append(VectorSeries(cell.ChassisSpeed, 1)).Append("\n");
        json.Append("    }");
    }

    private static float MaxForceError(List<Cell> cells)
    {
        float worst = 0f;
        foreach (Cell cell in cells)
        {
            worst = Mathf.Max(worst, cell.MaxForceError);
        }
        return worst;
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

    private static string VectorSeries(List<Vector3> values, int stride)
    {
        StringBuilder builder = new StringBuilder("[");
        for (int index = 0; index < values.Count; index += stride)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }
            builder.Append(V(values[index]));
        }
        return builder.Append(']').ToString();
    }

    private static string V(Vector3 value) =>
        "[" + N(value.x) + ", " + N(value.y) + ", " + N(value.z) + "]";

    private static string N(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NPrecise(float value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
}
