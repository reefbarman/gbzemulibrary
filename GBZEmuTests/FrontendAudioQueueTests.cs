using GBZEmuFrontend;

namespace GBZEmuTests;

public sealed class FrontendAudioQueueTests
{
    [Fact]
    public void QueueRequiresConfiguredStartupPreroll()
    {
        var queue = new FrontendAudioQueue(capacityFrames: 12, startupFrames: 8);
        var destination = new short[8];

        queue.Enqueue(new float[14], frameCount: 7);

        Assert.False(queue.IsPrimed);
        Assert.False(queue.TryDequeue(destination, frameCount: 4));

        queue.Enqueue(new float[2], frameCount: 1);

        Assert.True(queue.IsPrimed);
        Assert.True(queue.TryDequeue(destination, frameCount: 4));
    }

    [Fact]
    public void QueueHoldsMaximumCatchUpBurstWithoutDroppingStartupAudio()
    {
        const int framesPerRaylibBuffer = 735;
        var queue = new FrontendAudioQueue(
            capacityFrames: framesPerRaylibBuffer * 6,
            startupFrames: framesPerRaylibBuffer * 2);
        var source = new float[739 * 2];

        for (var frame = 0; frame < 5; frame++)
        {
            queue.Enqueue(source, frameCount: 739);
        }

        Assert.True(queue.IsPrimed);
        Assert.Equal(739 * 5, queue.QueuedFrames);
        Assert.Equal(0, queue.DroppedFrames);
    }

    [Fact]
    public void QueueUsesModelSpecificDcBlockerFeedback()
    {
        var source = new float[] { 10, 10, 0, 0 };
        var dmg = FilterTwoFrames(source, cgbHardware: false);
        var cgb = FilterTwoFrames(source, cgbHardware: true);

        Assert.Equal(dmg[0], cgb[0]);
        Assert.True(Math.Abs(cgb[2]) > Math.Abs(dmg[2]) * 10);
    }

    [Fact]
    public void RequirePreroll_PreservesQueuedAudioButClosesPlaybackGate()
    {
        var queue = new FrontendAudioQueue(capacityFrames: 12, startupFrames: 8);
        queue.Enqueue(new float[16], frameCount: 8);

        queue.RequirePreroll();

        Assert.False(queue.IsPrimed);
        Assert.Equal(8, queue.QueuedFrames);
        queue.Enqueue(new float[2], frameCount: 1);
        Assert.True(queue.IsPrimed);
    }

    private static short[] FilterTwoFrames(float[] source, bool cgbHardware)
    {
        var queue = new FrontendAudioQueue(capacityFrames: 2, startupFrames: 2);
        queue.SetHardwareModel(cgbHardware);
        queue.Enqueue(source, frameCount: 2);
        var destination = new short[4];
        Assert.True(queue.TryDequeue(destination, frameCount: 2));
        return destination;
    }
}
