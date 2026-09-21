using System.Globalization;
using Ni6451.Core;
using Ni6451.Daq;

namespace Ni6451.Tools;

internal static class TriggerTestCommand
{
    public static int Run(string[] args)
    {
        string device = "Dev2";
        string line = AppConfig.DefaultTriggerLine;
        string aiChannel = "ai0";
        int rate = AppConfig.DefaultRate;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--line" when i + 1 < args.Length: line = args[++i]; break;
                case "--ai" when i + 1 < args.Length: aiChannel = args[++i]; break;
                case "--rate" when i + 1 < args.Length: rate = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine($"Unrecognised option '{args[i]}'.");
                    return 2;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // let the finally block clear the tasks properly
            cts.Cancel();
        };

        Console.WriteLine($"Watching {device}/{line} for TTL trigger, hardware-synced at {rate:N0} S/s... (Ctrl+C to stop)");

        int count = 0;
        try
        {
            TriggerMonitor.Watch(device, line, aiChannel, rate, AppConfig.ChunkFor(rate), edge =>
            {
                count++;
                Console.WriteLine($"Trigger #{count} detected at sample {edge.Index} (t = {edge.Seconds:F6} s)");
            }, cts.Token);
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("NI-DAQmx is not installed on this machine (nicaiu.dll not found).");
            return 3;
        }
        catch (DaqmxException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }

        Console.WriteLine("\nStopped.");
        return 0;
    }
}
