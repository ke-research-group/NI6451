using System.Diagnostics;

namespace Ni6451.Core;

/// <summary>An immutable read of <see cref="AcquisitionStats"/>, safe to hand to the UI.</summary>
public readonly record struct AcquisitionSnapshot(
    long ChunksAcquired,
    long SamplesPerChannel,
    long BytesSpooled,
    int ChannelCount,
    int QueueDepth,
    int PeakQueueDepth,
    int QueueCapacity,
    long DroppedChunks,
    long Overruns,
    TimeSpan Elapsed)
{
    /// <summary>Sustained spool throughput since the run started.</summary>
    public double MegabytesPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : BytesSpooled / (1024.0 * 1024.0) / Elapsed.TotalSeconds;

    /// <summary>How full the writer queue is, 0..1. A number that stays near 1 means the disk is the bottleneck.</summary>
    public double QueuePressure => QueueCapacity <= 0 ? 0 : (double)QueueDepth / QueueCapacity;

    /// <summary>True if anything was lost: a driver overrun, or a chunk the writer could not take.</summary>
    public bool HasDataLoss => DroppedChunks > 0 || Overruns > 0;
}

/// <summary>
/// Live counters for one acquisition run, written from the DAQmx callback thread and the
/// spool writer thread, read from the UI thread. Every field is updated with interlocked
/// operations so no lock is ever taken on the acquisition hot path.
///
/// This is the visibility the Python version could not afford: under the GIL, instrumenting
/// the callback would have cost the very headroom it was meant to measure.
/// </summary>
public sealed class AcquisitionStats
{
    private long _chunksAcquired;
    private long _samplesPerChannel;
    private long _bytesSpooled;
    private long _droppedChunks;
    private long _overruns;
    private int _queueDepth;
    private int _peakQueueDepth;

    private readonly Stopwatch _clock = new();

    public int ChannelCount { get; private set; }

    public int QueueCapacity { get; private set; }

    public void Reset(int channelCount, int queueCapacity)
    {
        Interlocked.Exchange(ref _chunksAcquired, 0);
        Interlocked.Exchange(ref _samplesPerChannel, 0);
        Interlocked.Exchange(ref _bytesSpooled, 0);
        Interlocked.Exchange(ref _droppedChunks, 0);
        Interlocked.Exchange(ref _overruns, 0);
        Interlocked.Exchange(ref _queueDepth, 0);
        Interlocked.Exchange(ref _peakQueueDepth, 0);

        ChannelCount = channelCount;
        QueueCapacity = queueCapacity;
        _clock.Restart();
    }

    public void Stop() => _clock.Stop();

    /// <summary>Called on the DAQmx callback thread once a chunk has been queued.</summary>
    public void OnChunkQueued(int samplesPerChannel)
    {
        Interlocked.Increment(ref _chunksAcquired);
        Interlocked.Add(ref _samplesPerChannel, samplesPerChannel);

        int depth = Interlocked.Increment(ref _queueDepth);

        // Lock-free running maximum: retry only while another thread raised it under us.
        int peak = Volatile.Read(ref _peakQueueDepth);
        while (depth > peak)
        {
            int seen = Interlocked.CompareExchange(ref _peakQueueDepth, depth, peak);
            if (seen == peak) break;
            peak = seen;
        }
    }

    /// <summary>
    /// Monitor-only mode: a chunk was acquired and shown, but deliberately never queued for
    /// disk. Counts the samples without touching the queue depth, which stays at zero because
    /// there is no writer to fall behind.
    /// </summary>
    public void OnChunkMonitored(int samplesPerChannel)
    {
        Interlocked.Increment(ref _chunksAcquired);
        Interlocked.Add(ref _samplesPerChannel, samplesPerChannel);
    }

    /// <summary>Called on the spool writer thread once a chunk has reached the file streams.</summary>
    public void OnChunkWritten(long bytes)
    {
        Interlocked.Add(ref _bytesSpooled, bytes);
        Interlocked.Decrement(ref _queueDepth);
    }

    /// <summary>A chunk that never made it to disk, because the writer had already failed.</summary>
    public void OnChunkDropped()
    {
        Interlocked.Increment(ref _droppedChunks);
        Interlocked.Decrement(ref _queueDepth);
    }

    /// <summary>The driver reported that its own buffer overflowed before we read it.</summary>
    public void OnOverrun() => Interlocked.Increment(ref _overruns);

    public AcquisitionSnapshot Snapshot() => new(
        ChunksAcquired: Interlocked.Read(ref _chunksAcquired),
        SamplesPerChannel: Interlocked.Read(ref _samplesPerChannel),
        BytesSpooled: Interlocked.Read(ref _bytesSpooled),
        ChannelCount: ChannelCount,
        QueueDepth: Volatile.Read(ref _queueDepth),
        PeakQueueDepth: Volatile.Read(ref _peakQueueDepth),
        QueueCapacity: QueueCapacity,
        DroppedChunks: Interlocked.Read(ref _droppedChunks),
        Overruns: Interlocked.Read(ref _overruns),
        Elapsed: _clock.Elapsed);
}
