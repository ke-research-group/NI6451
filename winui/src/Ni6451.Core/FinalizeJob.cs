using System.Globalization;

namespace Ni6451.Core;

/// <summary>Everything <see cref="FinalizeJob"/> needs to turn a spool directory into a recording.</summary>
/// <param name="TempDir">Directory holding the per-channel <c>ai{n}.raw</c> spool files.</param>
/// <param name="SamplesPerChannel">Sample count written to each spool file.</param>
/// <param name="SaveDir">Folder the final <c>.npz</c> is written to.</param>
/// <param name="Channels">Active AI channel numbers, in the spool file order.</param>
/// <param name="TriggerSampleIndex">AI sample index of the first trigger edge, or null if none.</param>
/// <param name="ExperimentSerial">Four-digit, zero-padded experiment serial used in the file name (the <c>T{serial}</c> prefix).</param>
/// <param name="Rn">Run number used in the file name.</param>
/// <param name="SampleRate">Samples/s per channel the data was acquired at; written to the archive as <c>sample_rate</c>.</param>
public sealed record FinalizeRequest(
    string TempDir,
    long SamplesPerChannel,
    string SaveDir,
    IReadOnlyList<int> Channels,
    long? TriggerSampleIndex,
    string ExperimentSerial,
    int Rn,
    int SampleRate);

/// <summary>How far along a merge is, for a progress bar.</summary>
public readonly record struct FinalizeProgress(int ChannelsDone, int ChannelCount, long BytesWritten, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesWritten / TotalBytes, 0, 1);
}

/// <summary>
/// Merges the temporary per-channel raw files into a single <c>.npz</c>. This step is
/// I/O-bound and can take a long time for large recordings, so callers must run it off
/// the UI thread (see <c>MainWindow.FinalizeAsync</c>) or the application will appear to
/// hang -- the same reason the Python version used a background QThread.
/// </summary>
public static class FinalizeJob
{
    /// <summary>
    /// Write the archive and delete the spool files on success. Returns the output path.
    /// On failure the partially written <c>.npz</c> is removed but the raw spool files are
    /// deliberately kept: they are the only copy of the captured data, so they are left in
    /// place -- manifest included -- for recovery.
    /// </summary>
    public static string Run(
        FinalizeRequest request,
        IProgress<FinalizeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string fileName = $"T{request.ExperimentSerial}-raw-run{request.Rn.ToString(CultureInfo.InvariantCulture)}-{timestamp}.npz";
        string outPath = Path.Combine(request.SaveDir, fileName);

        long totalBytes = request.SamplesPerChannel * sizeof(double) * request.Channels.Count;
        long bytesWritten = 0;

        try
        {
            using (var npz = new NpzWriter(outPath))
            {
                for (int i = 0; i < request.Channels.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int channelsDone = i;
                    npz.AddFloat64FromRawFile(
                        $"ai{request.Channels[i]}",
                        Path.Combine(request.TempDir, $"ai{request.Channels[i]}.raw"),
                        request.SamplesPerChannel,
                        n =>
                        {
                            bytesWritten += n;
                            progress?.Report(new FinalizeProgress(channelsDone, request.Channels.Count, bytesWritten, totalBytes));
                        });
                }

                npz.AddInt64Scalar("sample_rate", request.SampleRate);
                npz.AddInt64Array("channels", request.Channels.Select(c => (long)c).ToArray());

                // -1 means trigger capture was off or no trigger edge was seen during the recording.
                npz.AddInt64Scalar("trigger_sample_index", request.TriggerSampleIndex ?? -1);
            }

            progress?.Report(new FinalizeProgress(request.Channels.Count, request.Channels.Count, totalBytes, totalBytes));
            MarkCompleteAndRemoveSpool(request);
            return outPath;
        }
        catch
        {
            // Don't leave a broken/empty .npz behind if the write failed partway through.
            TryDelete(outPath);
            throw;
        }
    }

    private static void MarkCompleteAndRemoveSpool(FinalizeRequest request)
    {
        // Flag the manifest first: if the directory delete then fails for any reason, recovery
        // will skip the leftovers instead of offering to merge an already-merged recording.
        try
        {
            SpoolManifest? manifest = SpoolManifest.TryLoad(request.TempDir);
            if (manifest is not null)
            {
                manifest.Completed = true;
                manifest.Save(request.TempDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (int ch in request.Channels)
                TryDelete(Path.Combine(request.TempDir, $"ai{ch}.raw"));

            TryDelete(SpoolManifest.PathIn(request.TempDir));
            if (Directory.Exists(request.TempDir)) Directory.Delete(request.TempDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // leftover temp files are harmless; can be deleted manually
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
