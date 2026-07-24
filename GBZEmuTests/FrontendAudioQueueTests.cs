using GBZEmuFrontend;

namespace GBZEmuTests;

public sealed class FrontendAudioQueueTests
{
    [Fact]
    public void QueueRequiresConfiguredStartupPreroll()
    {
        var queue = new FrontendAudioQueue(capacityFrames: 12, startupFrames: 8);
        var destination = new float[8];

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
    public void QueuePreservesNormalizedFloatPrecisionAndClampsOutput()
    {
        var queue = new FrontendAudioQueue(capacityFrames: 2, startupFrames: 2);
        queue.Enqueue(new float[] { 1f, -1f, 100f, -100f }, frameCount: 2);
        var destination = new float[4];

        Assert.True(queue.TryDequeue(destination, frameCount: 2));
        Assert.Equal(1f / 64f, destination[0], precision: 6);
        Assert.Equal(-1f / 64f, destination[1], precision: 6);
        Assert.Equal(1f, destination[2]);
        Assert.Equal(-1f, destination[3]);
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
    public void Underflow_OutputsSilenceAndRequiresPreroll()
    {
        var queue = new FrontendAudioQueue(capacityFrames: 12, startupFrames: 8);
        queue.Enqueue(new float[16], frameCount: 8);
        Assert.True(queue.TryDequeue(new float[8], frameCount: 4));
        var destination = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

        Assert.False(queue.TryDequeue(destination, frameCount: 5));
        Assert.All(destination, sample => Assert.Equal(0f, sample));
        Assert.False(queue.IsPrimed);
        Assert.Equal(4, queue.QueuedFrames);
    }

    [Fact]
    public async Task QueueSupportsConcurrentProducerAndConsumer()
    {
        const int iterations = 500;
        var queue = new FrontendAudioQueue(capacityFrames: 2, startupFrames: 1);
        using var sampleReady = new AutoResetEvent(initialState: false);
        using var sampleConsumed = new AutoResetEvent(initialState: true);
        var destination = new float[2];

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                Assert.True(sampleConsumed.WaitOne(TimeSpan.FromSeconds(5)));
                queue.Enqueue(new float[] { i, -i }, frameCount: 1);
                sampleReady.Set();
            }
        }, TestContext.Current.CancellationToken);
        var consumer = Task.Run(() =>
        {
            const float feedback = 0.9960133f;
            var leftInput = 0f;
            var leftOutput = 0f;
            var rightInput = 0f;
            var rightOutput = 0f;
            for (var i = 0; i < iterations; i++)
            {
                Assert.True(sampleReady.WaitOne(TimeSpan.FromSeconds(5)));
                Assert.True(queue.TryDequeue(destination, frameCount: 1));

                leftOutput = i - leftInput + (feedback * leftOutput);
                leftInput = i;
                rightOutput = -i - rightInput + (feedback * rightOutput);
                rightInput = -i;
                Assert.Equal(Math.Clamp(leftOutput / 64f, -1f, 1f), destination[0]);
                Assert.Equal(Math.Clamp(rightOutput / 64f, -1f, 1f), destination[1]);
                sampleConsumed.Set();
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(producer, consumer);
        Assert.Equal(0, queue.QueuedFrames);
    }

    private static float[] FilterTwoFrames(float[] source, bool cgbHardware)
    {
        var queue = new FrontendAudioQueue(capacityFrames: 2, startupFrames: 2);
        queue.SetHardwareModel(cgbHardware);
        queue.Enqueue(source, frameCount: 2);
        var destination = new float[4];
        Assert.True(queue.TryDequeue(destination, frameCount: 2));
        return destination;
    }
}
