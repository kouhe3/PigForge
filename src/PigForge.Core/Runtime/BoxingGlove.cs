using PigForge.Core.Content;

namespace PigForge.Core;

/// <summary>
/// The three states of the original's <c>SpringBoxingGlove</c> (docs/specs/boxing-glove.md §4):
/// at rest wound up, thrown, and winding back. The pure half of the machine lives here so a test
/// can drive the transitions without a physics world; the room applies the drive the state asks
/// for to the glove's joint and body.
/// </summary>
public enum BoxingGlovePhase
{
    /// <summary>Wound up: the glove is held against the part at its zero offset by the yDrive.</summary>
    WindedUp,

    /// <summary>Thrown: the drive's target is the skin's own distance along the part's local -Y.</summary>
    Shoot,

    /// <summary>Winding back: target zero, a softer drive, the skin's winding mass, no collider.</summary>
    Winding,
}

/// <summary>
/// What one state asks of the glove's joint and body — the §4 table of
/// docs/specs/boxing-glove.md as data. The room converts the spring numbers with the shared
/// <c>GameRoom.TrySpringResponse</c> and rebuilds the joint with them.
/// </summary>
/// <param name="TargetOffset">Target of the driven (y) axis in metres. The room's axis points
/// along the part's local <c>-Y</c>, so a positive offset throws the glove away from the part;
/// the original's <c>targetPosition.y</c> counts the other way round (probe §1.1).</param>
/// <param name="LateralOffset">Target of the lateral (x) axis in metres.</param>
/// <param name="DriveSpring">Driven-axis spring in N/m (the original's yDrive).</param>
/// <param name="DriveDamper">Driven-axis damper in N·s/m.</param>
/// <param name="Mass">The glove rigidbody's mass in kg.</param>
/// <param name="ColliderEnabled">False while the limp glove is brought home.</param>
/// <param name="LimitSpring">The driven axis's linear-limit spring in N/m; zero is a hard limit.</param>
public readonly record struct BoxingGloveDrive(
    float TargetOffset,
    float LateralOffset,
    float DriveSpring,
    float DriveDamper,
    float Mass,
    bool ColliderEnabled,
    float LimitSpring);

/// <summary>Per-glove runtime state: the phase and how long it has been in it.</summary>
public struct BoxingGloveState
{
    public BoxingGlovePhase Phase;

    /// <summary>Seconds in <see cref="Phase"/>. Advanced by the fixed step, so a replay is exact.</summary>
    public float Age;
}

/// <summary>
/// The original's <c>SpringBoxingGlove</c> moves, without a physics world behind them
/// (docs/specs/boxing-glove.md §4/§5). Every number the machine uses is content data extracted
/// from the part prefab; the two IN values it needs are constants of the shipped settings
/// (<c>BoxingGloveLength = 1</c>, <c>SwitchableBoxingGlove = true</c>), never authored here.
/// </summary>
public static class BoxingGloveRules
{
    /// <summary>
    /// IN <c>BoxingGloveLength</c> (<c>INDeclarationSettingsExp.json:1172</c>, not overridden):
    /// the multiplier the skin's <c>shoot.distanceY</c> is scaled by, i.e. 1 — the extracted
    /// distance is already the throw distance.
    /// </summary>
    public const float Length = 1f;

    /// <summary>
    /// The original's home threshold: the wind-back ends as soon as the glove is back within
    /// 0.1 m of its rest offset, whichever comes first with <c>wind.time</c>
    /// (<c>SpringBoxingGlove.cs:280-330</c>; the probe measures 0.22-0.34 s, not the declared 1 s).
    /// </summary>
    public const float HomeOffset = 0.1f;

    /// <summary>
    /// The original's <c>m_CanBeEnabled</c>: a glove whose neighbour in the effect direction
    /// carries SuperGlue cannot be switched on — the glue is what holds the contraption together
    /// and the punch is meant to break it. TNT is the exception (the original's own check names
    /// it), so a glued timebomb stays fair game.
    /// </summary>
    public static bool CanBeEnabled(bool neighbourGlued, bool neighbourIsTnt) =>
        !(neighbourGlued && !neighbourIsTnt);

    /// <summary>The distance one throw reaches, in metres (the original's
    /// <c>m_targetDistanceY * BoxingGloveLength</c>).</summary>
    public static float ThrowDistance(in PartGlove glove) => glove.Shoot.DistanceY * Length;

    /// <summary>The joint and body settings one phase asks for.</summary>
    public static BoxingGloveDrive DriveFor(BoxingGlovePhase phase, in PartGlove glove) => phase switch
    {
        // The original's InitilizeBoxingGlove: home, full mass, collider on, the yDrive holding
        // it, and the linear limit back to its hard 0/0 spring.
        BoxingGlovePhase.WindedUp => new BoxingGloveDrive(
            TargetOffset: 0f,
            LateralOffset: 0f,
            DriveSpring: glove.YDrive.Spring,
            DriveDamper: glove.YDrive.Damper,
            Mass: glove.Mass,
            ColliderEnabled: true,
            LimitSpring: 0f),
        // OnTouch -> Shoot: the drive's target goes out along the part's local -Y, the linear
        // limit's spring becomes the skin's (nearly slack) one, and the collider stays on so the
        // glove can hit whatever it reaches.
        BoxingGlovePhase.Shoot => new BoxingGloveDrive(
            TargetOffset: ThrowDistance(glove),
            // The original rolls +/-deviationX when it throws; every extracted skin has
            // deviationX = 0, so there is no roll to model (a non-zero skin would need a
            // deterministic sign source — recorded as a deviation).
            LateralOffset: glove.Shoot.DeviationX,
            DriveSpring: glove.YDrive.Spring,
            DriveDamper: glove.YDrive.Damper,
            Mass: glove.Mass,
            ColliderEnabled: true,
            LimitSpring: glove.Shoot.LimitSpring),
        // The wind-back: target home, the soft drive, the limp mass and no collider, so the
        // glove slides home through the part it belongs to. The limit keeps the throw's spring
        // (the original's wind-back only rewrites the drive, the mass and the collider).
        _ => new BoxingGloveDrive(
            TargetOffset: 0f,
            LateralOffset: 0f,
            DriveSpring: glove.Wind.DriveSpring,
            DriveDamper: glove.Wind.DriveDamper,
            Mass: glove.Wind.Mass,
            ColliderEnabled: false,
            LimitSpring: glove.Shoot.LimitSpring),
    };

    /// <summary>
    /// The trigger (an IN-off glove's touch, or the switch going on): a wound-up glove is thrown.
    /// A glove that is already out keeps its state — the original only starts a throw from rest.
    /// </summary>
    public static BoxingGloveState Trigger(in BoxingGloveState state) => state.Phase == BoxingGlovePhase.WindedUp
        ? new BoxingGloveState { Phase = BoxingGlovePhase.Shoot, Age = 0f }
        : state;

    /// <summary>
    /// The switch going off: the wind-back is interrupted and the glove returns to rest at once
    /// (the original's <c>InitilizeBoxingGlove</c> from its OnTouch; docs/specs/boxing-glove.md §5).
    /// </summary>
    public static BoxingGloveState Abort(in BoxingGloveState state) =>
        state.Phase == BoxingGlovePhase.WindedUp
            ? state
            : new BoxingGloveState { Phase = BoxingGlovePhase.WindedUp, Age = 0f };

    /// <summary>
    /// Advances one glove by one fixed step and returns whether the phase changed.
    /// <paramref name="offset"/> is the glove's current offset along the driven axis in metres
    /// (positive away from the part), measured from the tick's own snapshots — the wind-back ends
    /// on it, so the room has to look at the physics rather than at a timer alone.
    /// </summary>
    public static bool Tick(
        ref BoxingGloveState state,
        in PartGlove glove,
        float deltaSeconds,
        float offset)
    {
        state.Age += deltaSeconds;
        switch (state.Phase)
        {
            case BoxingGlovePhase.Shoot:
                // m_ShootTime seconds out, then the wind-back.
                if (state.Age >= glove.Shoot.Time)
                {
                    state.Phase = BoxingGlovePhase.Winding;
                    state.Age = 0f;
                    return true;
                }

                return false;
            case BoxingGlovePhase.Winding:
                // Time out or home, whichever comes first. The original's third test
                // (localPosition.y > 0, a glove that overshot past home) is covered by the
                // absolute one: passing the origin means |offset| crosses 0.
                if (state.Age >= glove.Wind.Time || MathF.Abs(offset) < HomeOffset)
                {
                    state.Phase = BoxingGlovePhase.WindedUp;
                    state.Age = 0f;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}
