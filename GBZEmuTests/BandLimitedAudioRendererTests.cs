using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class BandLimitedAudioRendererTests
{
    [Fact]
    public void RendererProducesHardwareFrameCadenceAtOutputRate()
    {
        var renderer = new BandLimitedAudioRenderer();

        renderer.Update(15, 9, Display.CLOCK_CYCLES_PER_FRAME);
        var samples = renderer.GetSamples(out var frameCount);

        Assert.Equal(738, frameCount);
        Assert.All(samples.Take(frameCount * 2), sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void RendererSettlesToConstantLevelWithoutGainError()
    {
        var renderer = new BandLimitedAudioRenderer();

        renderer.Update(12, 6, Display.CLOCK_CYCLES_PER_FRAME);
        var samples = renderer.GetSamples(out var frameCount);

        Assert.InRange(samples[(frameCount - 1) * 2], 11.99f, 12.01f);
        Assert.InRange(samples[((frameCount - 1) * 2) + 1], 5.99f, 6.01f);
    }

    [Fact]
    public void RendererSuppressesTransitionsFarAboveOutputNyquist()
    {
        var renderer = new BandLimitedAudioRenderer();
        for (var cycle = 0; cycle < Display.CLOCK_CYCLES_PER_FRAME; cycle++)
        {
            var level = (cycle & 1) == 0 ? 0 : 15;
            renderer.Update(level, level, 1);
        }

        var samples = renderer.GetSamples(out var frameCount);
        var settled = samples
            .Take(frameCount * 2)
            .Where((_, index) => index % 2 == 0)
            .Skip(frameCount - 100)
            .ToArray();

        Assert.InRange(settled.Max() - settled.Min(), 0f, 0.15f);
        Assert.InRange(settled.Average(), 7.45f, 7.55f);
    }
}
