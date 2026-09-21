using System.Globalization;
using System.IO.Compression;
using System.Text;
using Ni6451.Core;

namespace Ni6451.Tools;

/// <summary>
/// Platform-independent checks over the parts of the rewrite that do not touch hardware:
/// the rolling buffer semantics, the sensor conversions, and -- most importantly -- the
/// exact on-disk layout of the <c>.npz</c> recordings, which the Python tooling on the
/// <c>main</c> branch has to keep being able to read.
/// </summary>
internal static class SelfTestCommand
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        TestNpyHeaderLayout();
        TestNpzRoundTrip();
        TestFinalizeJobProducesReadableArchive();
        TestCrashRecovery();
        TestAcquisitionStats();
        TestNamingState();
        TestRateDerivedParameters();
        TestRollingBuffer();
        TestUnitConversion();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- .npy / .npz container ----------

    private static void TestNpyHeaderLayout()
    {
        using var ms = new MemoryStream();
        NpyFormat.WriteHeader(ms, NpyFormat.Float64Descr, 3);
        byte[] bytes = ms.ToArray();

        Check("npy: magic", bytes[0] == 0x93 && Encoding.ASCII.GetString(bytes, 1, 5) == "NUMPY");
        Check("npy: version 1.0", bytes[6] == 1 && bytes[7] == 0);
        Check("npy: data starts on a 64-byte boundary", bytes.Length % 64 == 0);

        string header = Encoding.ASCII.GetString(bytes, 10, bytes.Length - 10);
        Check("npy: header dict matches numpy's repr",
            header.StartsWith("{'descr': '<f8', 'fortran_order': False, 'shape': (3,), }", StringComparison.Ordinal));
        Check("npy: header is newline-terminated", header.EndsWith('\n'));

        using var scalar = new MemoryStream();
        NpyFormat.WriteHeader(scalar, NpyFormat.Int64Descr);
        string scalarHeader = Encoding.ASCII.GetString(scalar.ToArray(), 10, (int)scalar.Length - 10);
        Check("npy: 0-D shape is ()",
            scalarHeader.StartsWith("{'descr': '<i8', 'fortran_order': False, 'shape': (), }", StringComparison.Ordinal));
        Check("npy: 0-D data starts on a 64-byte boundary", scalar.Length % 64 == 0);
    }

    private static void TestNpzRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}.npz");
        try
        {
            double[] ai0 = [0.0, 1.5, -2.25, 3.125];
            using (var w = new NpzWriter(path))
            {
                w.AddFloat64Array("ai0", ai0);
                w.AddInt64Array("channels", [0L]);
                w.AddInt64Scalar("sample_rate", AppConfig.DefaultRate);
                w.AddInt64Scalar("trigger_sample_index", -1);
            }

            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                Check("npz: members are stored uncompressed",
                    zip.Entries.All(e => e.CompressedLength == e.Length));
                Check("npz: member names carry the .npy suffix",
                    zip.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
                       .SequenceEqual(["ai0.npy", "channels.npy", "sample_rate.npy", "trigger_sample_index.npy"]));
            }

            using var r = new NpzReader(path);
            Check("npz: float64 array round-trips", r.ReadFloat64Array("ai0").SequenceEqual(ai0));
            Check("npz: int64 array round-trips", r.ReadInt64Array("channels").SequenceEqual([0L]));
            Check("npz: scalar round-trips", r.ReadInt64Scalar("sample_rate") == AppConfig.DefaultRate);
            Check("npz: missing trigger is stored as -1", r.ReadInt64Scalar("trigger_sample_index") == -1);
            Check("npz: scalar has 0-D shape", r.GetHeader("sample_rate").Shape.Length == 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestFinalizeJobProducesReadableArchive()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            int[] channels = [0, 3, 7];
            const int samples = 1000;

            var spool = new ChannelSpool(workDir, channels, 20_000, new SpoolManifest { ExperimentSerial = "0207", Rn = 5 });
            var chunk = new double[channels.Length * samples];
            for (int c = 0; c < channels.Length; c++)
                for (int i = 0; i < samples; i++)
                    chunk[c * samples + i] = channels[c] + i * 0.001;

            spool.Write(chunk, samples);
            spool.Flush();
            spool.CloseFiles();

            string outPath = FinalizeJob.Run(new FinalizeRequest(
                spool.TempDir, spool.TotalSamplesWritten, workDir, channels, 12_345L, "0207", 5, spool.SampleRate));

            Check("finalize: file name follows the T{SH}-raw-run{RN}-{timestamp}.npz pattern",
                Path.GetFileName(outPath).StartsWith("T0207-raw-run5-", StringComparison.Ordinal)
                && outPath.EndsWith(".npz", StringComparison.Ordinal));
            Check("finalize: spool directory is removed on success", !Directory.Exists(spool.TempDir));

            using var r = new NpzReader(outPath);
            Check("finalize: every selected channel is present",
                channels.All(c => r.Names.Contains($"ai{c}")));
            Check("finalize: channels array matches the selection",
                r.ReadInt64Array("channels").SequenceEqual(channels.Select(c => (long)c)));
            Check("finalize: trigger index is preserved", r.ReadInt64Scalar("trigger_sample_index") == 12_345L);
            Check("finalize: sample_rate is the rate the spool was recorded at", r.ReadInt64Scalar("sample_rate") == 20_000);

            double[] ai3 = r.ReadFloat64Array("ai3");
            Check("finalize: channel data survives the spool round-trip",
                ai3.Length == samples && Math.Abs(ai3[999] - (3 + 0.999)) < 1e-12);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---------- crash recovery ----------

    private static void TestCrashRecovery()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            int[] channels = [0, 1];
            const int samples = 800;

            // Simulate a run that was killed: data spooled, manifest present, never finalized.
            var spool = new ChannelSpool(workDir, channels, 10_000, new SpoolManifest { ExperimentSerial = "0311", Rn = 9 });
            spool.TriggerIndexSource = () => 42L;

            var chunk = new double[channels.Length * samples];
            for (int c = 0; c < channels.Length; c++)
                for (int i = 0; i < samples; i++)
                    chunk[c * samples + i] = c * 1000 + i;

            spool.Write(chunk, samples);
            spool.SaveManifest();
            spool.CloseFiles();   // no FinalizeJob.Run: this is the crash

            IReadOnlyList<OrphanedSpool> orphans = SpoolRecovery.FindOrphans(workDir);
            Check("recovery: the interrupted run is found", orphans.Count == 1);
            if (orphans.Count != 1) return;

            OrphanedSpool orphan = orphans[0];
            Check("recovery: sample count is re-derived from the file lengths",
                orphan.RecoverableSamplesPerChannel == samples);
            Check("recovery: run numbering survives", orphan.Manifest.ExperimentSerial == "0311" && orphan.Manifest.Rn == 9);
            Check("recovery: trigger index survives", orphan.Manifest.TriggerSampleIndex == 42L);
            Check("recovery: sample rate survives", orphan.Manifest.SampleRate == 10_000);

            string outPath = SpoolRecovery.Recover(orphan, workDir);
            Check("recovery: reuses the original run numbering in the file name",
                Path.GetFileName(outPath).StartsWith("T0311-raw-run9-", StringComparison.Ordinal));

            using (var r = new NpzReader(outPath))
            {
                double[] ai1 = r.ReadFloat64Array("ai1");
                Check("recovery: the rescued samples are intact",
                    ai1.Length == samples && Math.Abs(ai1[799] - 1799) < 1e-12);
                Check("recovery: the trigger index is carried into the archive",
                    r.ReadInt64Scalar("trigger_sample_index") == 42L);
                Check("recovery: the archive carries the run's own sample rate, not the default",
                    r.ReadInt64Scalar("sample_rate") == 10_000);
            }

            Check("recovery: a recovered run is not offered again",
                SpoolRecovery.FindOrphans(workDir).Count == 0);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---------- stats ----------

    private static void TestAcquisitionStats()
    {
        var stats = new AcquisitionStats();
        stats.Reset(channelCount: 4, queueCapacity: 10);

        stats.OnChunkQueued(5000);
        stats.OnChunkQueued(5000);
        stats.OnChunkQueued(5000);
        Check("stats: queue depth tracks outstanding chunks", stats.Snapshot().QueueDepth == 3);

        stats.OnChunkWritten(160_000);
        Check("stats: a written chunk leaves the queue", stats.Snapshot().QueueDepth == 2);

        AcquisitionSnapshot snap = stats.Snapshot();
        Check("stats: peak depth is retained after draining", snap.PeakQueueDepth == 3);
        Check("stats: samples accumulate", snap.SamplesPerChannel == 15_000);
        Check("stats: no loss reported on a healthy run", !snap.HasDataLoss);

        stats.OnOverrun();
        Check("stats: an overrun is reported as data loss", stats.Snapshot().HasDataLoss);

        // Concurrent updates must not lose counts -- this is the hot path from four threads.
        var parallel = new AcquisitionStats();
        parallel.Reset(16, 200);
        Parallel.For(0, 1000, _ => parallel.OnChunkQueued(10));
        Parallel.For(0, 1000, _ => parallel.OnChunkWritten(80));
        AcquisitionSnapshot p = parallel.Snapshot();
        Check("stats: interlocked updates survive concurrency",
            p.ChunksAcquired == 1000 && p.SamplesPerChannel == 10_000 && p.QueueDepth == 0 && p.BytesSpooled == 80_000);
    }

    // ---------- output naming ----------

    private static void TestNamingState()
    {
        var today = new DateOnly(2026, 9, 17);
        var yesterday = new DateOnly(2026, 9, 16);

        var fresh = new NamingState();
        Check("naming: first launch uses the defaults",
            fresh.NextFor(today) == (NamingState.DefaultExperimentSerial, NamingState.DefaultRunNumber));

        var state = new NamingState();
        state.RecordRun("0207", 5, today);
        Check("naming: same day keeps the serial and advances the run", state.NextFor(today) == ("0207", 6));
        Check("naming: a new day advances the serial and resets the run",
            state.NextFor(today.AddDays(1)) == ("0208", 1));

        state.RecordRun("0207", 5, yesterday);
        Check("naming: 'yesterday' is a new day from today's point of view", state.NextFor(today) == ("0208", 1));

        Check("naming: serial is zero-padded", NamingState.Pad("7") == "0007" && NamingState.Increment("0999") == "1000");
        Check("naming: a non-numeric serial is left alone", NamingState.Increment("AB12") == "AB12");
        Check("naming: 9999 rolls to 10000 rather than wrapping", NamingState.Increment("9999") == "10000");

        string path = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}", NamingState.FileName);
        try
        {
            state.RecordRun("0311", 9, today);
            state.Save(path);
            NamingState loaded = NamingState.Load(path);
            Check("naming: round-trips through the settings file",
                loaded.ExperimentSerial == "0311" && loaded.RunNumber == 9 && loaded.LastRunDate == "2026-09-17");

            File.WriteAllText(path, "{ this is not json");
            Check("naming: a corrupt settings file falls back to the defaults",
                NamingState.Load(path).NextFor(today) == (NamingState.DefaultExperimentSerial, NamingState.DefaultRunNumber));
        }
        finally
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- sample-rate derived parameters ----------

    private static void TestRateDerivedParameters()
    {
        Check("rates: the menu offers 500k/100k/20k/10k/2k, highest first",
            AppConfig.SupportedRates.SequenceEqual([500_000, 100_000, 20_000, 10_000, 2_000]));
        Check("rates: the default is in the menu", AppConfig.SupportedRates.Contains(AppConfig.DefaultRate));

        // A chunk is 10 ms at every rate, so the callback cadence and plot latency do not change.
        Check("rates: chunk is 5 000 samples at 500 kS/s (unchanged from the fixed-rate design)",
            AppConfig.ChunkFor(500_000) == 5_000);
        Check("rates: chunk is 20 samples at 2 kS/s, not one plot update every 2.5 s",
            AppConfig.ChunkFor(2_000) == 20);
        Check("rates: chunk never drops below 20 samples", AppConfig.ChunkFor(100) == 20);

        // Every supported rate decimates to exactly DisplayRateHz for the plot.
        Check("rates: every rate is an exact multiple of the display rate",
            AppConfig.SupportedRates.All(r => r % AppConfig.DisplayRateHz == 0));
        Check("rates: decimation stride is 250 at 500 kS/s and 1 at 2 kS/s",
            AppConfig.DecimationStrideFor(500_000) == 250 && AppConfig.DecimationStrideFor(2_000) == 1);

        Check("rates: flush thresholds scale with the rate (10 s and 30 s)",
            AppConfig.FlushSamplesFor(20_000) == 200_000 && AppConfig.DurableFlushSamplesFor(20_000) == 600_000);
        Check("rates: driver buffer is 5 s at any rate", AppConfig.DriverBufferSamplesFor(10_000) == 50_000);
        Check("rates: menu labels", AppConfig.FormatRate(500_000) == "500 kS/s" && AppConfig.FormatRate(2_000) == "2 kS/s");
    }

    // ---------- rolling buffer ----------

    private static void TestRollingBuffer()
    {
        var buf = new RollingBuffer(2, 5);
        var dest = new double[2 * 5];

        Check("buffer: starts empty", buf.GetLast(5, dest) == 0);

        // channel-major: [ch0 samples..., ch1 samples...]
        buf.Push([1, 2, 3, 11, 12, 13], 3);
        int n = buf.GetLast(5, dest);
        Check("buffer: returns only what it holds", n == 3);
        Check("buffer: keeps channels separate",
            dest[0] == 1 && dest[1] == 2 && dest[2] == 3 && dest[3] == 11 && dest[4] == 12 && dest[5] == 13);

        // Wrap around: capacity 5, 3 already written, 4 more.
        buf.Push([4, 5, 6, 7, 14, 15, 16, 17], 4);
        n = buf.GetLast(5, dest);
        Check("buffer: saturates at capacity", n == 5);
        Check("buffer: oldest samples are overwritten across the wrap",
            dest[0] == 3 && dest[1] == 4 && dest[2] == 5 && dest[3] == 6 && dest[4] == 7);
        Check("buffer: second channel wraps identically",
            dest[5] == 13 && dest[6] == 14 && dest[7] == 15 && dest[8] == 16 && dest[9] == 17);

        // A push larger than the whole buffer keeps only the tail.
        buf.Push([1, 2, 3, 4, 5, 6, 7, 21, 22, 23, 24, 25, 26, 27], 7);
        n = buf.GetLast(5, dest);
        Check("buffer: oversized push keeps the tail",
            n == 5 && dest[0] == 3 && dest[4] == 7 && dest[5] == 23 && dest[9] == 27);

        buf.Reset();
        Check("buffer: reset empties it", buf.GetLast(5, dest) == 0 && buf.Filled == 0);
    }

    // ---------- unit conversion ----------

    private static void TestUnitConversion()
    {
        // Reference values computed from the Python unit_conversion.py on the main branch.
        Check("units: oil pressure", Close(UnitConversion.GetOilPressure(1.0), 5_498_500.0));
        Check("units: 1D normal stress",
            Close(UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.05), 4_097_482.2));
        Check("units: 2D shear stress ignores the thickness argument",
            Close(UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.05), 835_991.94)
            && Close(UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.10),
                     UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.05)));
        Check("units: 1D normal stress scales inversely with thickness",
            Close(UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.10) * 2,
                  UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.05)));
        Check("units: LVDT displacement", Close(UnitConversion.GetLvdtDisplacement(2.0), 0.09954));
    }

    private static bool Close(double a, double b, double relativeTolerance = 1e-9)
        => Math.Abs(a - b) <= relativeTolerance * Math.Max(1.0, Math.Abs(b));

    // ---------- harness ----------

    private static void Check(string name, bool ok)
    {
        if (ok) _passed++;
        else _failed++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{(ok ? "PASS" : "FAIL")}] {name}"));
    }
}
