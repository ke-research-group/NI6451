using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ni6451.Core;

/// <summary>
/// Metadata written alongside the raw spool files, so an interrupted run is recoverable.
///
/// Without this, a crash or power cut during acquisition left a <c>_tmp_*</c> directory of
/// anonymous <c>ai{n}.raw</c> files with no record of the sample rate, the run numbering or
/// the trigger index -- the samples survived but nothing could turn them back into a
/// recording. The manifest is rewritten atomically on every flush, so it is never observed
/// half-written.
/// </summary>
public sealed class SpoolManifest
{
    public const string FileName = "manifest.json";

    public int Version { get; set; } = 1;

    public string Device { get; set; } = string.Empty;

    public int[] Channels { get; set; } = [];

    public int SampleRate { get; set; } = AppConfig.DefaultRate;

    public string StartedUtc { get; set; } = string.Empty;

    /// <summary>Experiment serial, four digits zero-padded; the <c>T{serial}</c> prefix of the recording's file name.</summary>
    public string ExperimentSerial { get; set; } = "0000";

    public int Rn { get; set; }

    public bool CaptureTrigger { get; set; }

    /// <summary>Best-known sample count; refreshed on each flush. Recovery prefers the file lengths.</summary>
    public long SamplesPerChannel { get; set; }

    /// <summary>-1 when trigger capture was off or no edge was seen.</summary>
    public long TriggerSampleIndex { get; set; } = -1;

    /// <summary>Set once the recording has been merged into its <c>.npz</c>; an unset flag marks an orphan.</summary>
    public bool Completed { get; set; }

    public static string PathIn(string spoolDir) => Path.Combine(spoolDir, FileName);

    /// <summary>
    /// Write via a temporary file and an atomic replace, so a crash mid-write cannot destroy
    /// the previous good manifest.
    /// </summary>
    public void Save(string spoolDir)
    {
        string finalPath = PathIn(spoolDir);
        string tempPath = finalPath + ".tmp";

        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, SpoolManifestJsonContext.Default.SpoolManifest));
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public static SpoolManifest? TryLoad(string spoolDir)
    {
        string path = PathIn(spoolDir);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), SpoolManifestJsonContext.Default.SpoolManifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Compile-time JSON (de)serialiser for <see cref="SpoolManifest"/>. Reflection-based
/// System.Text.Json is trimmed away under Native AOT; the source generator emits the
/// equivalent code at build time instead.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SpoolManifest))]
internal partial class SpoolManifestJsonContext : JsonSerializerContext
{
}

/// <summary>An interrupted run found on disk, with everything needed to finish it.</summary>
/// <param name="SpoolDir">The orphaned <c>_tmp_*</c> directory.</param>
/// <param name="Manifest">Its manifest.</param>
/// <param name="RecoverableSamplesPerChannel">
/// Derived from the raw file lengths rather than the manifest, so it is exact even though the
/// manifest is only refreshed every flush interval.
/// </param>
public sealed record OrphanedSpool(string SpoolDir, SpoolManifest Manifest, long RecoverableSamplesPerChannel)
{
    public DateTime StartedUtc =>
        DateTime.TryParse(Manifest.StartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d)
            ? d
            : DateTime.MinValue;

    public double DurationSeconds =>
        Manifest.SampleRate <= 0 ? 0 : (double)RecoverableSamplesPerChannel / Manifest.SampleRate;
}

/// <summary>Finds and completes acquisitions that were interrupted before they were merged.</summary>
public static class SpoolRecovery
{
    /// <summary>
    /// Scan <paramref name="saveDir"/> for spool directories left behind by an interrupted run.
    /// Directories without a manifest are ignored: they predate this format, and guessing at a
    /// channel list would risk mislabelling the data.
    /// </summary>
    public static IReadOnlyList<OrphanedSpool> FindOrphans(string saveDir)
    {
        if (!Directory.Exists(saveDir)) return [];

        var found = new List<OrphanedSpool>();
        foreach (string dir in SafeEnumerateSpoolDirs(saveDir))
        {
            SpoolManifest? manifest = SpoolManifest.TryLoad(dir);
            if (manifest is null || manifest.Completed || manifest.Channels.Length == 0) continue;

            long samples = RecoverableSamples(dir, manifest.Channels);
            if (samples <= 0) continue;

            found.Add(new OrphanedSpool(dir, manifest, samples));
        }

        return found.OrderBy(o => o.StartedUtc).ToArray();
    }

    /// <summary>
    /// The number of complete samples present in every channel file. Taking the minimum means a
    /// run cut off mid-chunk yields an aligned, consistent recording across all channels rather
    /// than one ragged channel.
    /// </summary>
    public static long RecoverableSamples(string spoolDir, IReadOnlyList<int> channels)
    {
        long min = long.MaxValue;
        foreach (int ch in channels)
        {
            var file = new FileInfo(Path.Combine(spoolDir, $"ai{ch}.raw"));
            if (!file.Exists) return 0;
            min = Math.Min(min, file.Length / sizeof(double));
        }

        return min == long.MaxValue ? 0 : min;
    }

    /// <summary>Merge an orphaned spool into a normal <c>.npz</c>, reusing its original run numbering.</summary>
    public static string Recover(OrphanedSpool orphan, string saveDir, IProgress<FinalizeProgress>? progress = null)
    {
        var request = new FinalizeRequest(
            orphan.SpoolDir,
            orphan.RecoverableSamplesPerChannel,
            saveDir,
            orphan.Manifest.Channels,
            orphan.Manifest.TriggerSampleIndex >= 0 ? orphan.Manifest.TriggerSampleIndex : null,
            orphan.Manifest.ExperimentSerial,
            orphan.Manifest.Rn,
            orphan.Manifest.SampleRate);

        return FinalizeJob.Run(request, progress);
    }

    private static IEnumerable<string> SafeEnumerateSpoolDirs(string saveDir)
    {
        try
        {
            return Directory.EnumerateDirectories(saveDir, "_tmp_*").ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
