using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class WindowsClockTests(ITestOutputHelper output)
{
    [Fact]
    public void PermitClockIsMonotonicAndAdvancesInsideALegacyClockTick()
    {
        // A real clock-resolution contract, not an assertion against a second
        // call to the same Win32 implementation. No model or desktop focus.
        long previous = WindowsInterruptClock.NowMs, previousBand = -1, previousInBand = -1;
        long legacyStep = long.MaxValue, preciseStep = long.MaxValue;
        var sampling = Stopwatch.StartNew();
        bool fineStep = false;
        while (sampling.Elapsed < TimeSpan.FromMilliseconds(250) && (!fineStep || legacyStep == long.MaxValue))
        {
            long before = (long)GetTickCount64();
            long current = WindowsInterruptClock.NowMs;
            long after = (long)GetTickCount64();
            Assert.True(current >= previous, "The Windows permit clock must not move backwards.");
            if (current > previous)
                preciseStep = Math.Min(preciseStep, current - previous);
            // Compare only samples wholly inside a stable legacy clock band.
            // Preemption across a tick is ignored, not mistaken for precision.
            if (before == after)
            {
                if (before == previousBand && current > previousInBand)
                    fineStep = true;
                if (previousBand >= 0 && before > previousBand)
                    legacyStep = Math.Min(legacyStep, before - previousBand);
                previousBand = before;
                previousInBand = current;
            }
            previous = current;
        }
        output.WriteLine($"sampleMs={sampling.Elapsed.TotalMilliseconds:F2}; legacyMinimumStepMs={legacyStep}; permitMinimumStepMs={preciseStep}");
        Assert.True(fineStep, "A 10-16ms clock cannot satisfy the browser's millisecond authorization contract.");
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
