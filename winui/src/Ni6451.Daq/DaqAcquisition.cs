using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Ni6451.Core;

namespace Ni6451.Daq;

/// <summary>What an acquisition run does with the samples it reads.</summary>
public enum AcquisitionMode
{
    /// <summary>Spool every sample to disk for later merging into a recording.</summary>
    Record,

    /// <summary>
    /// Show the live traces and the sensor readout, and write nothing. The spool files, the
    /// writer thread and the bounded queue are never created, so a monitoring session cannot
    /// fill a drive and cannot be slowed down by one.
    /// </summary>
    Monitor,
}

/// <summary>What a finished acquisition leaves behind for <see cref="FinalizeJob"/> to merge.</summary>
public sealed record AcquisitionResult(
    string TempDir,
    long SamplesPerChannel,
    IReadOnlyList<int> Channels,
    long? TriggerSampleIndex,
    int SampleRate,
    AcquisitionSnapshot Stats);

/// <summary>
/// Creates and controls the DAQmx task(s), using only the channels the user selected in
/// the UI. Optionally also opens a second DI task, sample-clock-synced to the AI task, to
/// record which AI sample index lines up with the first rising edge of an external TTL
/// trigger (e.g. another DAQ's Trigger Out).
///
/// This is the C# port of the Python <c>daq_worker.py</c>, restructured into a three-stage
/// pipeline whose stages genuinely run at the same time -- which is the part a Python
/// implementation could not have, because the GIL serialises them no matter how many
/// threads are involved:
///
///   1. <b>The DAQmx callback thread</b> does the minimum that must happen there: read the
///      AI buffer, read the DI buffer, take one strided copy for the monitor, and hand both
///      off. It never touches the UI's rolling buffer and never waits on disk.
///   2. <b>The spool writer thread</b> drains a bounded queue and writes the full-rate data
///      to the per-channel files, fanning the per-channel writes out across the thread pool.
///      The queue is the backpressure: if the disk cannot keep up, the queue fills and the
///      callback waits, which is visible in <see cref="Stats"/> long before anything is lost.
///   3. <b>The monitor thread</b> drains a separate, small, lossy queue of decimated data and
///      raises <see cref="MonitorChunkReady"/>. Because this queue drops its oldest entry when
///      full, a stalled or slow UI can never apply backpressure to the acquisition -- the
///      live plot degrades instead, and the recording is untouched.
///
/// Notes:
///   - AI channels use RSE (single-ended) mode with a +/-10 V range.
///   - The device name should match what NI MAX reports for your hardware.
///   - Only channels the user actually connected should be selected -- leaving unused
///     channels in the scan list exposes them to multiplexer ghosting from neighbouring
///     active channels.
///   - Trigger capture: the DI task's sample clock and start trigger are both locked to
///     the AI task's ("/{device}/ai/SampleClock", "/{device}/ai/StartTrigger"), so DI
///     sample N and AI sample N were taken at the same instant. Because many external
///     trigger sources latch high after firing rather than pulsing per event, only the
///     first rising edge is recorded.
/// </summary>
public sealed class DaqAcquisition : IDisposable
{
    /// <summary>DAQmx: "the application is not able to keep up with the hardware acquisition".</summary>
    private const int ErrorSamplesNoLongerAvailable = -200279;

    /// <summary>DAQmx: onboard device memory overflowed before the samples could be transferred.</summary>
    private const int ErrorOnboardMemoryOverflow = -200361;

    /// <summary>Frames of decimated monitor data buffered for the UI before the oldest is dropped.</summary>
    private const int MonitorQueueCapacity = 64;

    private readonly object _latestLock = new();

    private nint _aiTask;
    private nint _diTask;

    // The driver calls the static EveryNSamplesThunk below and hands back this handle as its
    // callbackData, which is how the thunk finds the instance. A normal (non-pinned) handle
    // keeps the instance alive for as long as the task exists.
    private GCHandle _selfHandle;

    private ChannelSpool? _spool;
    private int[] _channels = [];

    private Channel<PendingChunk>? _spoolQueue;
    private Channel<PendingChunk>? _monitorQueue;
    private Thread? _writerThread;
    private Thread? _monitorThread;
    private volatile string? _writerError;

    private byte[] _diBuffer = [];
    private double[] _latestVoltages = [];

    // Touched only from the DAQmx callback thread.
    private long _sampleCounter;
    private bool _triggerLastValue;

    private long _triggerSampleIndex = -1;

    /// <summary>
    /// Raised on the dedicated monitor thread with decimated, channel-major data (row stride =
    /// the sample count). The buffer is only valid for the duration of the call; handlers must
    /// copy what they need. Handlers may block without affecting the recording.
    /// </summary>
    public event Action<ReadOnlyMemory<double>, int>? MonitorChunkReady;

    /// <summary>Raised when the driver or the spool writer fails. May fire on a background thread.</summary>
    public event Action<string>? Error;

    /// <summary>Raised when samples were provably lost, with a description of what happened.</summary>
    public event Action<string>? DataLoss;

    /// <summary>Live counters for the current run.</summary>
    public AcquisitionStats Stats { get; } = new();

    public bool IsRunning => _aiTask != 0;

    /// <summary>Active AI channel numbers for the current or most recent run.</summary>
    public IReadOnlyList<int> Channels => _channels;

    /// <summary>Whether trigger capture was requested for the current run.</summary>
    public bool CaptureTrigger { get; private set; }

    /// <summary>Whether the current or most recent run spools to disk or only monitors.</summary>
    public AcquisitionMode Mode { get; private set; } = AcquisitionMode.Record;

    /// <summary>Samples/s per channel of the current or most recent run.</summary>
    public int SampleRate { get; private set; } = AppConfig.DefaultRate;

    /// <summary>Samples per DAQmx callback for the current run; derived from the rate.</summary>
    public int Chunk { get; private set; } = AppConfig.ChunkFor(AppConfig.DefaultRate);

    /// <summary>
    /// Keep every Nth full-rate sample for the monitor stream, so the plot always receives
    /// <see cref="AppConfig.DisplayRateHz"/> samples/s whatever the acquisition rate. Display
    /// concern only; set from the rate on each <see cref="Start"/>.
    /// </summary>
    public int MonitorDecimationStride { get; private set; } = AppConfig.DecimationStrideFor(AppConfig.DefaultRate);

    /// <summary>
    /// AI sample index of the first trigger rising edge, or null if trigger capture is off
    /// or no edge has been seen yet.
    /// </summary>
    public long? TriggerSampleIndex
    {
        get
        {
            long v = Interlocked.Read(ref _triggerSampleIndex);
            return v < 0 ? null : v;
        }
    }

    /// <summary>Most recent sample for <paramref name="channel"/>, for the live sensor readout.</summary>
    public bool TryGetLatestVoltage(int channel, out double voltage)
    {
        lock (_latestLock)
        {
            int pos = Array.IndexOf(_channels, channel);
            if (pos < 0 || pos >= _latestVoltages.Length)
            {
                voltage = 0;
                return false;
            }

            voltage = _latestVoltages[pos];
            return true;
        }
    }

    // ---------- acquisition control ----------

    /// <summary>
    /// Start acquiring. On failure the <see cref="Error"/> event fires, everything opened so
    /// far is torn down, and <see cref="IsRunning"/> stays false.
    /// </summary>
    /// <param name="saveDir">
    /// Where the spool directory is created. Ignored, and allowed to be null, in
    /// <see cref="AcquisitionMode.Monitor"/> — monitoring writes nothing, so it needs no folder.
    /// </param>
    public void Start(
        string device,
        string? saveDir,
        IReadOnlyList<int> channels,
        int sampleRate = AppConfig.DefaultRate,
        bool captureTrigger = false,
        string triggerLine = AppConfig.DefaultTriggerLine,
        SpoolManifest? manifest = null,
        AcquisitionMode mode = AcquisitionMode.Record)
    {
        if (_aiTask != 0)
        {
            Error?.Invoke("A previous acquisition task is still active. Stop it before starting a new one.");
            return;
        }

        if (sampleRate <= 0)
        {
            Error?.Invoke($"Sample rate must be positive (got {sampleRate}).");
            return;
        }

        if (mode == AcquisitionMode.Record && string.IsNullOrEmpty(saveDir))
        {
            Error?.Invoke("An output folder is required to record.");
            return;
        }

        Mode = mode;
        SampleRate = sampleRate;
        Chunk = AppConfig.ChunkFor(sampleRate);
        MonitorDecimationStride = AppConfig.DecimationStrideFor(sampleRate);

        _channels = channels.ToArray();
        int nActive = _channels.Length;
        if (nActive == 0)
        {
            Error?.Invoke("Select at least one channel to acquire.");
            return;
        }

        CaptureTrigger = captureTrigger;
        Interlocked.Exchange(ref _triggerSampleIndex, -1);
        _triggerLastValue = false;
        _sampleCounter = 0;
        _writerError = null;
        _diBuffer = new byte[Chunk];
        Stats.Reset(nActive, AppConfig.WriteQueueCapacity);

        lock (_latestLock)
            _latestVoltages = new double[nActive];

        try
        {
            if (mode == AcquisitionMode.Record)
            {
                manifest ??= new SpoolManifest();
                manifest.Device = device;
                manifest.CaptureTrigger = captureTrigger;

                _spool = new ChannelSpool(saveDir!, _channels, sampleRate, manifest)
                {
                    TriggerIndexSource = () => TriggerSampleIndex,
                };
            }

            StartPipelineThreads(mode);

            NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out _aiTask));
            foreach (int ch in _channels)
            {
                NiDaqmx.Check(NiDaqmx.DAQmxCreateAIVoltageChan(
                    _aiTask, $"{device}/ai{ch}", null,
                    NiDaqmx.Val_RSE, -10.0, 10.0, NiDaqmx.Val_Volts, null));
            }

            NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                _aiTask, null, sampleRate, NiDaqmx.Val_Rising,
                NiDaqmx.Val_ContSamps, AppConfig.DriverBufferSamplesFor(sampleRate)));

            if (captureTrigger)
            {
                NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out _diTask));
                NiDaqmx.Check(NiDaqmx.DAQmxCreateDIChan(
                    _diTask, $"{device}/{triggerLine}", null, NiDaqmx.Val_ChanPerLine));

                // Lock the DI sample clock and start to the AI task's, so DI sample N and
                // AI sample N are taken at the same instant.
                NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                    _diTask, $"/{device}/ai/SampleClock", sampleRate, NiDaqmx.Val_Rising,
                    NiDaqmx.Val_ContSamps, AppConfig.DriverBufferSamplesFor(sampleRate)));

                NiDaqmx.Check(NiDaqmx.DAQmxCfgDigEdgeStartTrig(
                    _diTask, $"/{device}/ai/StartTrigger", NiDaqmx.Val_Rising));
            }

            if (!_selfHandle.IsAllocated) _selfHandle = GCHandle.Alloc(this);
            unsafe
            {
                NiDaqmx.Check(NiDaqmx.DAQmxRegisterEveryNSamplesEvent(
                    _aiTask, NiDaqmx.Val_Acquired_Into_Buffer, (uint)Chunk, 0,
                    &EveryNSamplesThunk, GCHandle.ToIntPtr(_selfHandle)));
            }

            if (captureTrigger)
                NiDaqmx.Check(NiDaqmx.DAQmxStartTask(_diTask));   // arms, waits for the AI start trigger

            NiDaqmx.Check(NiDaqmx.DAQmxStartTask(_aiTask));       // fires the shared start trigger
        }
        catch (Exception e) when (e is DaqmxException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            Error?.Invoke(e.Message);
            AbortAfterFailedStart();
        }
    }

    /// <summary>
    /// Stop the DAQmx task(s) and flush any remaining buffered data to disk. This is fast
    /// (no large I/O) -- merging the spool files into a single <c>.npz</c> is handled
    /// separately by <see cref="FinalizeJob"/> so it can run in the background without
    /// blocking the UI. Returns null if nothing was recorded.
    /// </summary>
    public AcquisitionResult? StopAcquisition()
    {
        // Order matters: stop the AI task first so no further callbacks are queued, then
        // clear it (which waits for any in-flight callback to return -- the writer thread
        // is still draining at this point, so a callback blocked on a full queue can
        // always make progress), and only then shut the pipeline down.
        StopAndClear(ref _aiTask);
        ShutdownPipelineThreads();
        StopAndClear(ref _diTask);
        ReleaseSelfHandle();   // only after ClearTask: the driver may still call back until then

        Stats.Stop();
        AcquisitionSnapshot snapshot = Stats.Snapshot();

        ChannelSpool? spool = _spool;
        _spool = null;

        // Monitor runs have nothing to finalize; null here means "no recording", which is also
        // what a Record run that captured zero samples returns.
        if (spool is null) return null;

        spool.Flush(durable: true);
        spool.CloseFiles();

        string? writerError = _writerError;
        if (writerError is not null)
            Error?.Invoke($"Spool write error: {writerError}");

        if (spool.TotalSamplesWritten == 0)
        {
            spool.RemoveTempDir();
            return null;
        }

        return new AcquisitionResult(
            spool.TempDir, spool.TotalSamplesWritten, _channels.ToArray(), TriggerSampleIndex, SampleRate, snapshot);
    }

    // ---------- DAQmx callback ----------

    /// <summary>
    /// The function the driver actually calls. No exception may escape an
    /// <see cref="UnmanagedCallersOnlyAttribute"/> method -- it would tear the process down --
    /// so this is a catch-all shell around the instance method.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EveryNSamplesThunk(nint taskHandle, int eventType, uint nSamples, nint callbackData)
    {
        try
        {
            if (callbackData != 0 && GCHandle.FromIntPtr(callbackData).Target is DaqAcquisition self)
                return self.OnEveryNSamples(taskHandle, eventType, nSamples, callbackData);
        }
        catch (Exception)
        {
            // Swallowed deliberately: see the summary. The instance method reports its own errors.
        }

        return 0;
    }

    private int OnEveryNSamples(nint taskHandle, int eventType, uint nSamples, nint callbackData)
    {
        int n = (int)nSamples;
        int nActive = _channels.Length;
        double[] buffer = ArrayPool<double>.Shared.Rent(nActive * n);
        bool handedOff = false;

        try
        {
            NiDaqmx.Check(NiDaqmx.DAQmxReadAnalogF64(
                _aiTask, n, 10.0, NiDaqmx.Val_GroupByChannel,
                buffer, (uint)(nActive * n), out int samplesRead, 0));

            // The read is blocking and asks for exactly n samples/channel, so a short read
            // means the task was torn down underneath us. The row stride DAQmx used is n
            // regardless, so a partial buffer cannot be interpreted safely -- drop it.
            if (samplesRead != n) return 0;

            lock (_latestLock)
            {
                for (int c = 0; c < nActive && c < _latestVoltages.Length; c++)
                    _latestVoltages[c] = buffer[c * n + n - 1];
            }

            PostToMonitor(buffer, n, nActive);

            Channel<PendingChunk>? queue = _spoolQueue;
            if (queue is not null)
            {
                // Blocks only if the writer is more than WriteQueueCapacity chunks behind,
                // which is the intended backpressure and is visible in Stats.QueuePressure.
                queue.Writer.WriteAsync(new PendingChunk(buffer, n, nActive)).AsTask().GetAwaiter().GetResult();
                Stats.OnChunkQueued(n);
                handedOff = true;
            }
            else
            {
                Stats.OnChunkMonitored(n);   // monitoring: counted and shown, never written
            }

            if (CaptureTrigger && _diTask != 0)
                ReadTriggerChunk(n);

            _sampleCounter += n;
        }
        catch (DaqmxException e)
        {
            if (e.Status is ErrorSamplesNoLongerAvailable or ErrorOnboardMemoryOverflow)
            {
                Stats.OnOverrun();
                DataLoss?.Invoke(
                    "The acquisition outran the buffer and samples were lost. The recording up to this "
                    + "point is intact and will still be saved. Reduce the channel count, or move the "
                    + "output folder to a faster drive.");
            }

            Error?.Invoke(e.Message);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or ChannelClosedException)
        {
            // Racing a shutdown; expected during teardown and not worth surfacing.
        }
        finally
        {
            if (!handedOff) ArrayPool<double>.Shared.Return(buffer);
        }

        return 0;   // DAQmx requires the callback to return a status
    }

    /// <summary>
    /// Take a strided copy for the live plot and hand it to the monitor thread. Decimating
    /// here rather than in the UI keeps the callback thread away from the rolling buffer's
    /// lock, which the UI holds while it copies out a frame -- up to several megabytes at a
    /// 30 s window.
    /// </summary>
    private void PostToMonitor(double[] source, int nSamples, int nActive)
    {
        Channel<PendingChunk>? monitor = _monitorQueue;
        if (monitor is null) return;

        int stride = Math.Max(1, MonitorDecimationStride);
        int outCount = (nSamples + stride - 1) / stride;
        if (outCount <= 0) return;

        double[] decimated = ArrayPool<double>.Shared.Rent(nActive * outCount);
        for (int c = 0; c < nActive; c++)
        {
            int srcBase = c * nSamples;
            int dstBase = c * outCount;
            for (int i = 0, s = 0; i < outCount; i++, s += stride)
                decimated[dstBase + i] = source[srcBase + s];
        }

        // Lossy on purpose: a slow UI drops frames instead of stalling the acquisition.
        if (!monitor.Writer.TryWrite(new PendingChunk(decimated, outCount, nActive)))
            ArrayPool<double>.Shared.Return(decimated);
    }

    /// <summary>
    /// Reads (drains) the DI task every callback so its buffer never overflows. Only
    /// bothers looking for the rising edge until the first one is found -- most external
    /// trigger sources latch high after firing rather than pulsing per event, so there is
    /// nothing more to find after that.
    /// </summary>
    private void ReadTriggerChunk(int nSamples)
    {
        if (_diBuffer.Length < nSamples) _diBuffer = new byte[nSamples];

        int read;
        try
        {
            NiDaqmx.Check(NiDaqmx.DAQmxReadDigitalLines(
                _diTask, nSamples, 10.0, NiDaqmx.Val_GroupByChannel,
                _diBuffer, (uint)_diBuffer.Length, out read, out _, 0));
        }
        catch (DaqmxException e)
        {
            Error?.Invoke($"Trigger input read error: {e.Message}");
            return;
        }

        if (read <= 0) return;
        if (Interlocked.Read(ref _triggerSampleIndex) >= 0)
        {
            _triggerLastValue = _diBuffer[read - 1] != 0;
            return;
        }

        // Detect rising edges, including one that straddles the previous chunk boundary.
        bool previous = _triggerLastValue;
        for (int i = 0; i < read; i++)
        {
            bool current = _diBuffer[i] != 0;
            if (!previous && current)
            {
                Interlocked.Exchange(ref _triggerSampleIndex, _sampleCounter + i);
                break;
            }

            previous = current;
        }

        _triggerLastValue = _diBuffer[read - 1] != 0;
    }

    // ---------- pipeline threads ----------

    private void StartPipelineThreads(AcquisitionMode mode)
    {
        // In Monitor mode the spool queue and its writer are simply never created. The callback
        // sees a null queue, keeps its buffer, and returns it to the pool -- so there is no disk
        // stage at all rather than one that writes to a discard.
        if (mode == AcquisitionMode.Record)
        {
            _spoolQueue = Channel.CreateBounded<PendingChunk>(new BoundedChannelOptions(AppConfig.WriteQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        _monitorQueue = Channel.CreateBounded<PendingChunk>(new BoundedChannelOptions(MonitorQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        if (mode == AcquisitionMode.Record)
        {
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "ni6451-spool-writer",
                Priority = ThreadPriority.AboveNormal,
            };
            _writerThread.Start();
        }

        _monitorThread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "ni6451-monitor",
            Priority = ThreadPriority.BelowNormal,
        };
        _monitorThread.Start();
    }

    private void WriterLoop()
    {
        Channel<PendingChunk>? queue = _spoolQueue;
        ChannelSpool? spool = _spool;
        if (queue is null || spool is null) return;

        try
        {
            while (true)
            {
                if (!queue.Reader.TryRead(out PendingChunk chunk))
                {
                    if (!queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) break;
                    continue;
                }

                try
                {
                    if (_writerError is null)
                    {
                        spool.Write(chunk.Buffer, chunk.SampleCount);
                        Stats.OnChunkWritten((long)chunk.SampleCount * chunk.ChannelCount * sizeof(double));
                    }
                    else
                    {
                        Stats.OnChunkDropped();
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(chunk.Buffer);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Record and keep draining the queue, so the DAQ callback never deadlocks on a
            // full queue after the writer has given up.
            _writerError = e.Message;
            DataLoss?.Invoke($"Writing to disk failed: {e.Message}");
            DrainAfterFailure(queue);
        }
    }

    private void DrainAfterFailure(Channel<PendingChunk> queue)
    {
        while (queue.Reader.TryRead(out PendingChunk chunk))
        {
            ArrayPool<double>.Shared.Return(chunk.Buffer);
            Stats.OnChunkDropped();
        }
    }

    private void MonitorLoop()
    {
        Channel<PendingChunk>? queue = _monitorQueue;
        if (queue is null) return;

        while (true)
        {
            if (!queue.Reader.TryRead(out PendingChunk chunk))
            {
                if (!queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) break;
                continue;
            }

            try
            {
                MonitorChunkReady?.Invoke(
                    new ReadOnlyMemory<double>(chunk.Buffer, 0, chunk.ChannelCount * chunk.SampleCount),
                    chunk.SampleCount);
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
                // The UI is tearing down; the plot is not worth taking anything else down for.
            }
            finally
            {
                ArrayPool<double>.Shared.Return(chunk.Buffer);
            }
        }
    }

    private void ShutdownPipelineThreads()
    {
        Channel<PendingChunk>? spoolQueue = _spoolQueue;
        Channel<PendingChunk>? monitorQueue = _monitorQueue;
        Thread? writer = _writerThread;
        Thread? monitor = _monitorThread;

        _spoolQueue = null;
        _monitorQueue = null;
        _writerThread = null;
        _monitorThread = null;

        spoolQueue?.Writer.TryComplete();
        monitorQueue?.Writer.TryComplete();

        writer?.Join(TimeSpan.FromSeconds(60));
        monitor?.Join(TimeSpan.FromSeconds(5));

        // Anything still queued after the join deadline would otherwise leak pooled arrays.
        ReturnRemaining(spoolQueue);
        ReturnRemaining(monitorQueue);
    }

    private static void ReturnRemaining(Channel<PendingChunk>? queue)
    {
        if (queue is null) return;
        while (queue.Reader.TryRead(out PendingChunk leftover))
            ArrayPool<double>.Shared.Return(leftover.Buffer);
    }

    private void ReleaseSelfHandle()
    {
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private void AbortAfterFailedStart()
    {
        StopAndClear(ref _aiTask);
        StopAndClear(ref _diTask);
        ShutdownPipelineThreads();
        ReleaseSelfHandle();

        ChannelSpool? spool = _spool;
        _spool = null;
        spool?.CloseFiles();
        spool?.RemoveTempDir();
    }

    private static void StopAndClear(ref nint task)
    {
        nint handle = task;
        if (handle == 0) return;
        task = 0;

        try { NiDaqmx.DAQmxStopTask(handle); } catch (DllNotFoundException) { }
        try { NiDaqmx.DAQmxClearTask(handle); } catch (DllNotFoundException) { }
    }

    public void Dispose()
    {
        if (IsRunning) StopAcquisition();
        ReleaseSelfHandle();
        _spool?.Dispose();
    }

    private readonly record struct PendingChunk(double[] Buffer, int SampleCount, int ChannelCount);
}
