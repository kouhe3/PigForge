using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The boxing glove's own machine (docs/specs/boxing-glove.md §4/§5) without a physics world: the
/// three states, what each one asks of the joint and the body, and the guard the original puts in
/// front of a throw. The numbers are the extracted prefab overrides — never authored here — and
/// the two IN constants the machine needs are pinned to the shipped settings.
/// </summary>
public sealed class BoxingGloveTests
{
    private const uint PartBoxingGlove = 28;
    private const uint PartBoxingGloveLong = 245;

    private const float Step = 1f / 60f;

    [Fact]
    public void TheContentCarriesTheExtractedPrefabOverrides()
    {
        // The prefab's per-skin overrides, not the class defaults: drive 380/3.5 (class 60/3),
        // shoot time 0.4 (class 1), deviationX 0 (class 0.01), distanceY 2.5 for the base skin.
        PartGlove glove = Glove(PartBoxingGlove);
        Assert.Equal(0.5f, glove.Mass);
        Assert.Equal(380f, glove.YDrive.Spring);
        Assert.Equal(3.5f, glove.YDrive.Damper);
        Assert.Equal(1000f, glove.XDrive.Spring);
        Assert.Equal(5f, glove.XDrive.Damper);
        Assert.Equal(1f, glove.Limit);
        Assert.Equal(0.1f, glove.ProjectionDistance);
        Assert.Equal(2.5f, glove.Shoot.DistanceY);
        Assert.Equal(0f, glove.Shoot.DeviationX);
        Assert.Equal(0.4f, glove.Shoot.Time);
        Assert.Equal(0.1f, glove.Shoot.LimitSpring);
        Assert.Equal(1f, glove.Wind.Time);
        Assert.Equal(0.01f, glove.Wind.Mass);
        Assert.Equal(25f, glove.Wind.DriveSpring);
        Assert.Equal(2.5f, glove.Wind.DriveDamper);
        Assert.Equal(1.6f, glove.SolverIterationScale);

        // The glove rigidbody's own collider: the BoxingGlove prefab's 0.3 m sphere.
        PartShapeDefinition shape = Assert.Single(glove.Shapes);
        Assert.Equal(PhysicsShapeKind.Sphere, shape.Kind);
        Assert.Equal(0.3f, shape.Radius!.Value, precision: 5);

        // IN BoxingGloveLength = 1 (INDeclarationSettingsExp.json:1172, unoverridden), so the throw
        // distance is the skin's own distanceY; the home threshold is the original's 0.1 m.
        Assert.Equal(1f, BoxingGloveRules.Length);
        Assert.Equal(0.1f, BoxingGloveRules.HomeOffset);
    }

    [Fact]
    public void TheLongSkinThrowsTwiceAsFarWithItsOwnDrive()
    {
        // Part_SpringBoxingGlove_05_SET is the skin that punches further: 5 m, and its wind-back
        // drive follows (10 x 5 x 1 = 50).
        PartGlove long_ = Glove(PartBoxingGloveLong);
        Assert.Equal(5f, BoxingGloveRules.ThrowDistance(long_));
        Assert.Equal(50f, long_.Wind.DriveSpring);
        Assert.Equal(2.5f, BoxingGloveRules.ThrowDistance(Glove(PartBoxingGlove)));
    }

    [Fact]
    public void TheWoundUpStateHoldsTheGloveHomeWithTheSkinsOwnDrive()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveDrive drive = BoxingGloveRules.DriveFor(BoxingGlovePhase.WindedUp, glove);

        Assert.Equal(0f, drive.TargetOffset);
        Assert.Equal(0f, drive.LateralOffset);
        Assert.Equal(380f, drive.DriveSpring);
        Assert.Equal(3.5f, drive.DriveDamper);
        Assert.Equal(0.5f, drive.Mass);
        Assert.True(drive.ColliderEnabled);
        // The original's linearLimitSpring is 0/0 at rest: a hard limit.
        Assert.Equal(0f, drive.LimitSpring);
    }

    [Fact]
    public void TheThrowAimsTheSkinsDistanceAndKeepsTheGloveSolid()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveDrive drive = BoxingGloveRules.DriveFor(BoxingGlovePhase.Shoot, glove);

        // The machine's offset is measured along the part's local -Y, which is the direction the
        // probe found the original's glove travelling (tasks/boxing-glove-probe.json §1.1).
        Assert.Equal(2.5f, drive.TargetOffset);
        Assert.Equal(0f, drive.LateralOffset);
        Assert.Equal(380f, drive.DriveSpring);
        Assert.Equal(3.5f, drive.DriveDamper);
        Assert.Equal(0.5f, drive.Mass);
        Assert.True(drive.ColliderEnabled);
        // m_Shoot rewrites the limit spring to the skin's nearly slack one.
        Assert.Equal(0.1f, drive.LimitSpring);
    }

    [Fact]
    public void TheWindBackGoesLimpAndTargetsHome()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveDrive drive = BoxingGloveRules.DriveFor(BoxingGlovePhase.Winding, glove);

        Assert.Equal(0f, drive.TargetOffset);
        Assert.Equal(25f, drive.DriveSpring);
        Assert.Equal(2.5f, drive.DriveDamper);
        Assert.Equal(0.01f, drive.Mass);
        Assert.False(drive.ColliderEnabled);
        Assert.Equal(0.1f, drive.LimitSpring);
    }

    [Fact]
    public void TheThrowEndsOnTheShootTime()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveState state = new() { Phase = BoxingGlovePhase.Shoot, Age = 0f };

        for (int tick = 0; tick < 23; tick++)
        {
            Assert.False(BoxingGloveRules.Tick(ref state, glove, Step, 2.5f));
        }

        Assert.Equal(BoxingGlovePhase.Shoot, state.Phase);

        // 0.4 s of 1/60 steps: the 24th tick crosses the skin's shoot time.
        Assert.True(BoxingGloveRules.Tick(ref state, glove, Step, 2.5f));
        Assert.Equal(BoxingGlovePhase.Winding, state.Phase);
        Assert.Equal(0f, state.Age);
    }

    [Fact]
    public void TheWindBackEndsOnTheTimerAtTheLatest()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveState state = new() { Phase = BoxingGlovePhase.Winding, Age = 0f };

        // The glove is still far out, so only the skin's 1 s matters (the original's "or home
        // within 0.1 m" cannot fire).
        int ticks = 0;
        while (state.Phase == BoxingGlovePhase.Winding && ticks < 65)
        {
            BoxingGloveRules.Tick(ref state, glove, Step, 1.5f);
            ticks++;
        }

        Assert.Equal(BoxingGlovePhase.WindedUp, state.Phase);
        Assert.Equal(0f, state.Age);
        // 1 s of 1/60 steps, plus the float accumulation's own rounding.
        Assert.InRange(ticks, 59, 62);
    }

    [Fact]
    public void TheWindBackEndsEarlyOnceTheGloveIsHome()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveState state = new() { Phase = BoxingGlovePhase.Winding, Age = 0f };

        // The probe measures the wind-back home in 0.22-0.34 s, far inside the declared 1 s: the
        // home test is what ends it, and it ends the moment the offset is inside 0.1 m.
        Assert.True(BoxingGloveRules.Tick(ref state, glove, Step, 0.09f));
        Assert.Equal(BoxingGlovePhase.WindedUp, state.Phase);
    }

    [Fact]
    public void TheTriggerOnlyStartsFromRest()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveState resting = new() { Phase = BoxingGlovePhase.WindedUp, Age = 0.7f };

        BoxingGloveState thrown = BoxingGloveRules.Trigger(resting);
        Assert.Equal(BoxingGlovePhase.Shoot, thrown.Phase);
        Assert.Equal(0f, thrown.Age);

        // A glove that is already out keeps its state: the original only throws from rest.
        BoxingGloveState flying = new() { Phase = BoxingGlovePhase.Winding, Age = 0.3f };
        Assert.Equal(flying, BoxingGloveRules.Trigger(flying));
        Assert.Equal(flying, BoxingGloveRules.Trigger(BoxingGloveRules.Trigger(flying)));
    }

    [Fact]
    public void TheSwitchGoingOffInterruptsTheThrowAndTheWindBack()
    {
        // The toggle's off edge is the original's InitilizeBoxingGlove: the glove goes home now.
        Assert.Equal(BoxingGlovePhase.WindedUp, BoxingGloveRules.Abort(new BoxingGloveState { Phase = BoxingGlovePhase.Shoot, Age = 0.2f }).Phase);
        BoxingGloveState aborted = BoxingGloveRules.Abort(new BoxingGloveState { Phase = BoxingGlovePhase.Winding, Age = 0.5f });
        Assert.Equal(BoxingGlovePhase.WindedUp, aborted.Phase);
        Assert.Equal(0f, aborted.Age);

        BoxingGloveState resting = new() { Phase = BoxingGlovePhase.WindedUp, Age = 0.4f };
        Assert.Equal(resting, BoxingGloveRules.Abort(resting));
    }

    [Fact]
    public void CanBeEnabledRefusesAGluedNeighbourUnlessItIsTnt()
    {
        // The original's m_CanBeEnabled: glue on the part behind the glove blocks the throw, but a
        // glued timebomb is the one exception its own check names.
        Assert.False(BoxingGloveRules.CanBeEnabled(neighbourGlued: true, neighbourIsTnt: false));
        Assert.True(BoxingGloveRules.CanBeEnabled(neighbourGlued: true, neighbourIsTnt: true));
        Assert.True(BoxingGloveRules.CanBeEnabled(neighbourGlued: false, neighbourIsTnt: false));
        Assert.True(BoxingGloveRules.CanBeEnabled(neighbourGlued: false, neighbourIsTnt: true));
    }

    [Fact]
    public void TheGloveMachineIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunMachine(), RunMachine());
    }

    /// <summary>One full cycle: trigger, throw, wind back on the home test, then a second throw.</summary>
    private static string RunMachine()
    {
        PartGlove glove = Glove(PartBoxingGlove);
        BoxingGloveState state = default;
        List<string> trace = new();
        for (int tick = 0; tick < 80; tick++)
        {
            if (tick == 2)
            {
                state = BoxingGloveRules.Trigger(state);
            }

            // A crude throw-and-return profile: out for the shoot time, then home over 30 ticks.
            float offset = state.Phase switch
            {
                BoxingGlovePhase.Shoot => 2.5f,
                BoxingGlovePhase.Winding => 2.5f * (1f - (state.Age / 0.5f)),
                _ => 0f,
            };
            bool changed = BoxingGloveRules.Tick(ref state, glove, Step, offset);
            BoxingGloveDrive drive = BoxingGloveRules.DriveFor(state.Phase, glove);
            trace.Add($"{tick}:{state.Phase}:{drive.TargetOffset}:{drive.Mass}:{changed}");
            if (state.Phase == BoxingGlovePhase.WindedUp && tick > 40)
            {
                state = BoxingGloveRules.Trigger(state);
            }
        }

        return string.Join("|", trace);
    }

    private static PartGlove Glove(uint partTypeId)
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        PartGlove? glove = content.GetPart(partTypeId).Capabilities?.Glove;
        Assert.NotNull(glove);
        return glove!;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
