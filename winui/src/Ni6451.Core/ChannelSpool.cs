using System.Runtime.InteropServices;

namespace Ni6451.Core;

/// <summary>
/// Owns the temporary per-channel raw files that acquired data is streamed into, so
/// full-rate data is never held entirely in RAM. One <c>ai{n}.raw</c> file per active
/// channel, holding bare little-endian <c>float64</c> samples with no header --
/// byte-identical to the layout the Python implementation spooled, and to the payload
/// of the corresponding <c>.npy</c> member in the final archive.
///
/// Alongside them sits a <see cref="SpoolManifest"/> recording what the run was, refreshed
/// on every flush, so an interrupted acquisition can be recovered rather than left as a
/// directory of anonymous numbers.
///
/// Instances are used from a single writer thread. Within one <see cref="Write"/> call the
/// per-channel writes may be fanned out across the thread pool -- they target independent
/// <see cref="FileStream"/> objects, so they do not contend.
/// </summary>
public sealed class ChannelSpool : IDisposable
{
    private readonly FileStream[] _files;
    private readonly int[] _channels;
    private readonly long _flushSamples;
    private readonly long _durableFlushSamples;

    private long _samplesSinceFlush;
    private long _samplesSinceDurableFlush;
    private bool _filesClosed;

    /// <param name="saveDir">Folder the temp directory is created inside.</param>
    /// <param name="channels">Active AI channel numbers, e.g. [0, 3, 7].</param>
    /// <param name="sampleRate">Samples/s per channel; sets the flush cadences and is recorded in the manifest.</param>
    /// <param name="manifest">Run metadata to record beside the data; a default is written if null.</param>
    public ChannelSpool(string saveDir, IReadOnlyList<int> channels, int sampleRate, SpoolManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(saveDir);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0) throw new ArgumentException("At least one channel is required.", nameof(channels));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        _channels = channels.ToArray();
        _flushSamples = AppConfig.FlushSamplesFor(sampleRate);
        _durableFlushSamples = AppConfig.DurableFlushSamplesFor(sampleRate);
        TempDir = Path.Combine(saveDir, $"_tmp_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(TempDir);

        Manifest = manifest ?? new SpoolManifest();
        Manifest.Channels = _channels;
        Manifest.SampleRate = sampleRate;
        Manifest.StartedUtc = DateTime.UtcNow.ToString("O");
        Manifest.Completed = false;

        _files = new FileStream[_channels.Length];
        try
        {
            Manifest.Save(TempDir);

            for (int i = 0; i < _channels.Length; i++)
                _files[i] = new FileStream(ChannelPath(i), FileMode.Create, FileAccess.Write, FileShare.Read,
                                           bufferSize: 1 << 20);
        }
        catch
        {
            CloseFiles();
            RemoveTempDir();
            throw;
        }
    }

    public string TempDir { get; }

    public IReadOnlyList<int> Channels => _channels;

    public int SampleRate => Manifest.SampleRate;

    /// <summary>Run metadata persisted beside the data. Mutate, then call <see cref="SaveManifest"/>.</summary>
    public SpoolManifest Manifest { get; }

    /// <summary>Supplies the current trigger sample index when the manifest is refreshed.</summary>
    public Func<long?>? TriggerIndexSource { get; set; }

    /// <summary>Samples per channel handed to <see cref="Write"/> so far.</summary>
    public long TotalSamplesWritten { get; private set; }

    /// <summary>Bytes written across all channels.</summary>
    public long TotalBytesWritten => TotalSamplesWritten * _channels.Length * sizeof(double);

    /// <summary><paramref name="position"/> is the index into <see cref="Channels"/>, not the AI channel number.</summary>
    public string ChannelPath(int position) => Path.Combine(TempDir, $"ai{_channels[position]}.raw");

    /// <summary>
    /// Append <paramref name="nSamples"/> samples per channel. <paramref name="data"/> is
    /// channel-major with a row stride of <paramref name="nSamples"/>, in the same channel
    /// order as <see cref="Channels"/>.
    /// </summary>
    public void Write(double[] data, int nSamples)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (nSamples <= 0) return;
        ObjectDisposedException.ThrowIf(_filesClosed, this);
        if (data.Length < _channels.Length * nSamples)
            throw new ArgumentException("Source array is smaller than Channels.Count * nSamples.", nameof(data));

        if (_channels.Length >= AppConfig.ParallelWriteThreshold)
        {
            try
            {
                // Independent FileStream objects, so the copies into their buffers -- and any
                // buffer-full write syscalls they trigger -- genuinely overlap. This is the part
                // a Python implementation could not parallelise: the GIL would have serialised
                // the per-channel byte handling regardless of how many threads it used.
                Parallel.For(0, _channels.Length, c => WriteChannel(c, data, nSamples));
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                throw e.InnerExceptions[0];   // surface the real IOException, not the wrapper
            }
        }
        else
        {
            for (int c = 0; c < _channels.Length; c++) WriteChannel(c, data, nSamples);
        }

        TotalSamplesWritten += nSamples;
        _samplesSinceFlush += nSamples;
        _samplesSinceDurableFlush += nSamples;

        if (_samplesSinceFlush >= _flushSamples)
        {
            bool durable = _samplesSinceDurableFlush >= _durableFlushSamples;
            Flush(durable);

            _samplesSinceFlush = 0;
            if (durable) _samplesSinceDurableFlush = 0;
        }
    }

    private void WriteChannel(int position, double[] data, int nSamples)
        => _files[position].Write(MemoryMarshal.AsBytes(data.AsSpan(position * nSamples, nSamples)));

    /// <summary>
    /// Push buffered data out of the process, and refresh the manifest so a crash right after
    /// this point is recoverable. Pass <paramref name="durable"/> to additionally fsync.
    /// </summary>
    public void Flush(bool durable = false)
    {
        if (_filesClosed) return;

        foreach (FileStream f in _files) f.Flush(durable);
        SaveManifest();
    }

    /// <summary>Rewrite the manifest with the current progress. Cheap: a few hundred bytes.</summary>
    public void SaveManifest()
    {
        Manifest.SamplesPerChannel = TotalSamplesWritten;
        Manifest.TriggerSampleIndex = TriggerIndexSource?.Invoke() ?? -1;

        try
        {
            Manifest.Save(TempDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stale manifest still recovers correctly -- the sample count is re-derived from
            // the file lengths -- so this must never take the acquisition down with it.
        }
    }

    public void CloseFiles()
    {
        if (_filesClosed) return;
        _filesClosed = true;

        foreach (FileStream? f in _files)
        {
            try { f?.Dispose(); }
            catch (IOException) { /* nothing useful to do while tearing down */ }
        }
    }

    /// <summary>
    /// Delete the raw files and the temp directory. Failures are swallowed: leftover temp
    /// files are harmless and can be removed manually, and cleanup must never break the
    /// main acquisition flow.
    /// </summary>
    public void RemoveTempDir()
    {
        try
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                string p = ChannelPath(i);
                if (File.Exists(p)) File.Delete(p);
            }

            string manifestPath = SpoolManifest.PathIn(TempDir);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);

            if (Directory.Exists(TempDir)) Directory.Delete(TempDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // leave for manual cleanup
        }
    }

    public void Dispose() => CloseFiles();
}
