using PigForge.Physics.Abstractions;

namespace PigForge.Physics.Tests;

/// <summary>
/// Unity's <c>PhysicMaterialCombine</c> pair rule (see <see cref="PhysicsMaterial.FrictionWith"/>):
/// the pair uses the higher-priority mode of the two surfaces and applies only that mode. The
/// wheel tyres are Multiply (0.025–0.05) while the ground is Average, which is why a tire slides
/// instead of gripping like its hub's 0.7.
/// </summary>
public sealed class PhysicsMaterialTests
{
    [Theory]
    [InlineData(FrictionCombine.Average, 0.7f, FrictionCombine.Average, 0.9f, 0.8f)]
    [InlineData(FrictionCombine.Multiply, 0.025f, FrictionCombine.Average, 0.7f, 0.0175f)]
    [InlineData(FrictionCombine.Average, 0.7f, FrictionCombine.Multiply, 0.025f, 0.0175f)]
    [InlineData(FrictionCombine.Minimum, 0.7f, FrictionCombine.Average, 0.3f, 0.3f)]
    [InlineData(FrictionCombine.Average, 0.3f, FrictionCombine.Maximum, 0.7f, 0.7f)]
    public void FrictionCombinesByTheHigherPriorityMode(
        FrictionCombine left,
        float leftFriction,
        FrictionCombine right,
        float rightFriction,
        float expected)
    {
        Assert.Equal(
            expected,
            new PhysicsMaterial(0f, leftFriction, left).FrictionWith(new PhysicsMaterial(0f, rightFriction, right)),
            precision: 4);
    }

    [Fact]
    public void AMaterialDefaultsToAverageAndUnitysBuiltInFriction()
    {
        Assert.Equal(FrictionCombine.Average, PhysicsMaterial.Default.FrictionCombine);
        Assert.Equal(0.8f, PhysicsMaterial.Default.Friction, precision: 4);
    }
}
