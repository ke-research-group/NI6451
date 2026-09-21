using Ni6451.Core;

namespace Ni6451.Daq;

/// <summary>A rising edge seen on the monitored TTL line.</summary>
/// <param name="Index">Sample index since the monitor started.</param>
/// <param name="Seconds">Time of the edge relative to the start, in seconds.</param>
public readonly record struct TriggerEdge(long Index, double Seconds);

/// <summary>
/// Hardware-clock-synced TTL trigger test. Instead of software-polling the line over USB
/// (which is limited by USB round-trip latency and can miss closely-spaced triggers), this
/// opens a DI task whose sample clock and start trigger are both locked to an AI task's
/// internal clock. The trigger line is therefore sampled at exactly <c>rate</c> samples per
/// second by the device's own hardware timing, so edges only need to be farther apart than
/// 1/rate to be caught -- at 500 kS/s that is 2 us, plenty for a window-mode trigger firing
/// repeatedly.
///
/// Port of the Python <c>tests/trigger_tester.py</c>. The AI channel just needs to exist on
/// the device to generate the shared sample clock; it does not need to be connected to
/// anything meaningful and its readings are discarded.
/// </summary>
public static class TriggerMonitor
{
    /// <summary>
    /// Watch <paramref name="triggerLine"/> until <paramref name="cancellationToken"/> is
    /// signalled, invoking <paramref name="onEdge"/> for every rising edge.
    /// </summary>
    public static void Watch(
        string device,
        string triggerLine,
        string aiChannel,
        int rate,
        int chunk,
        Action<TriggerEdge> onEdge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEdge);

        nint aiTask = 0;
        nint diTask = 0;
        try
        {
            NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out aiTask));
            NiDaqmx.Check(NiDaqmx.DAQmxCreateAIVoltageChan(
                aiTask, $"{device}/{aiChannel}", null,
                NiDaqmx.Val_Cfg_Default, -10.0, 10.0, NiDaqmx.Val_Volts, null));
            NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                aiTask, null, rate, NiDaqmx.Val_Rising, NiDaqmx.Val_ContSamps, (ulong)rate * 5));

            NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out diTask));
            NiDaqmx.Check(NiDaqmx.DAQmxCreateDIChan(
                diTask, $"{device}/{triggerLine}", null, NiDaqmx.Val_ChanPerLine));

            // Lock the DI task's sample clock to the AI task's internal clock, and its start
            // to the AI task's start, so sample 0 lines up on both.
            NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                diTask, $"/{device}/ai/SampleClock", rate, NiDaqmx.Val_Rising,
                NiDaqmx.Val_ContSamps, (ulong)rate * 5));
            NiDaqmx.Check(NiDaqmx.DAQmxCfgDigEdgeStartTrig(
                diTask, $"/{device}/ai/StartTrigger", NiDaqmx.Val_Rising));

            NiDaqmx.Check(NiDaqmx.DAQmxStartTask(diTask));   // arms, waits for the AI start trigger
            NiDaqmx.Check(NiDaqmx.DAQmxStartTask(aiTask));   // fires the shared start trigger

            var diBuffer = new byte[chunk];
            var aiBuffer = new double[chunk];
            long sampleIndex = 0;
            bool last = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                NiDaqmx.Check(NiDaqmx.DAQmxReadDigitalLines(
                    diTask, chunk, 10.0, NiDaqmx.Val_GroupByChannel,
                    diBuffer, (uint)diBuffer.Length, out int read, out _, 0));

                if (read <= 0) continue;

                // Detect rising edges, including one that straddles the previous chunk boundary.
                bool previous = last;
                for (int i = 0; i < read; i++)
                {
                    bool current = diBuffer[i] != 0;
                    if (!previous && current)
                    {
                        long globalIndex = sampleIndex + i;
                        onEdge(new TriggerEdge(globalIndex, globalIndex / (double)rate));
                    }

                    previous = current;
                }

                last = diBuffer[read - 1] != 0;
                sampleIndex += read;

                // The AI side must be drained too, or its internal buffer will overflow.
                NiDaqmx.Check(NiDaqmx.DAQmxReadAnalogF64(
                    aiTask, chunk, 10.0, NiDaqmx.Val_GroupByChannel,
                    aiBuffer, (uint)aiBuffer.Length, out _, 0));
            }
        }
        finally
        {
            foreach (nint task in new[] { aiTask, diTask })
            {
                if (task == 0) continue;
                try { NiDaqmx.DAQmxStopTask(task); } catch (DllNotFoundException) { }
                try { NiDaqmx.DAQmxClearTask(task); } catch (DllNotFoundException) { }
            }
        }
    }

    /// <summary>Convenience overload using the application's configured rate and chunk size.</summary>
    public static void Watch(
        string device, string triggerLine, string aiChannel, Action<TriggerEdge> onEdge, CancellationToken cancellationToken)
        => Watch(device, triggerLine, aiChannel, AppConfig.DefaultRate, AppConfig.ChunkFor(AppConfig.DefaultRate), onEdge, cancellationToken);
}
