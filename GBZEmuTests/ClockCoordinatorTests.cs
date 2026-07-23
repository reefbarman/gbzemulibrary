using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies raw CPU-clock and base-speed clock phase coordination independently of instruction execution.
/// </summary>
public sealed class ClockCoordinatorTests
{
    /// <summary>
    /// Verifies normal speed emits one base clock per T-state and realigns after four raw clocks.
    /// </summary>
    [Fact]
    public void NormalSpeedEmitsOneBaseClockPerRawClock()
    {
        var coordinator = new ClockCoordinator();

        for (var tState = 1; tState <= InstructionSchema.FOUR_CYCLES; tState++)
        {
            var advance = coordinator.AdvanceRawClock(1);
            Assert.Equal(tState, advance.TState);
            Assert.True(advance.EmitsBaseClock);
        }

        Assert.True(coordinator.IsMachineCycleAligned);
        Assert.Equal(0, coordinator.BaseClockDividerPhase);
    }

    /// <summary>
    /// Verifies double speed retains odd raw-clock phase and emits one base clock for every pair.
    /// </summary>
    [Fact]
    public void DoubleSpeedEmitsOneBaseClockForEveryTwoRawClocks()
    {
        var coordinator = new ClockCoordinator();

        Assert.False(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.Equal(1, coordinator.BaseClockDividerPhase);
        Assert.True(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.Equal(0, coordinator.BaseClockDividerPhase);
        Assert.False(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.True(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.True(coordinator.IsMachineCycleAligned);
    }

    /// <summary>
    /// Verifies a speed change preserves the pending odd double-speed raw clock instead of dropping it.
    /// </summary>
    [Fact]
    public void SpeedChangePreservesPendingBaseClockPhase()
    {
        var coordinator = new ClockCoordinator();

        Assert.False(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.True(coordinator.AdvanceRawClock(1).EmitsBaseClock);
        Assert.Equal(0, coordinator.BaseClockDividerPhase);
        Assert.False(coordinator.AdvanceRawClock(2).EmitsBaseClock);
        Assert.True(coordinator.AdvanceRawClock(2).EmitsBaseClock);
    }

    /// <summary>
    /// Verifies partial T-state and double-speed divider phase survive direct v4 machine-state serialization.
    /// </summary>
    [Fact]
    public void PartialClockPhaseRoundTripsThroughStateSerialization()
    {
        var original = new ClockCoordinator();
        var restored = new ClockCoordinator();
        original.AdvanceRawClock(2);
        original.AdvanceRawClock(2);
        original.AdvanceRawClock(2);

        var serialized = StateSerialization.Write(original);
        StateSerialization.Read(serialized, restored);

        Assert.Equal(original.RawClockInMachineCycle, restored.RawClockInMachineCycle);
        Assert.Equal(original.BaseClockDividerPhase, restored.BaseClockDividerPhase);
        var originalNext = original.AdvanceRawClock(2);
        var restoredNext = restored.AdvanceRawClock(2);
        Assert.Equal(originalNext.TState, restoredNext.TState);
        Assert.Equal(originalNext.EmitsBaseClock, restoredNext.EmitsBaseClock);
    }
}
