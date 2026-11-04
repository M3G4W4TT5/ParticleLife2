using BenchmarkDotNet.Running;
using ParticleLife.Benchmarks;

try
{
    if (args.Length > 0 && args[0] == "profile-m41")
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("Use profile-m41 input-experiment.json output-profile.json.");
        }

        TraversalProfile.Run(args[1], args[2]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "prepare-m41")
    {
        if (args.Length > 3)
        {
            throw new ArgumentException("Use prepare-m41 [input-directory] [retained-M4-directory].");
        }

        M41Inputs.Prepare(args.Length > 1 ? args[1] : "artifacts/benchmarks/m4-1/inputs",
            args.Length > 2 ? args[2] : "artifacts/benchmarks/m4");
        return 0;
    }

    if (args.Length > 0 && args[0] == "compare-m41")
    {
        if (args.Length is < 4 or > 7)
        {
            throw new ArgumentException("Use compare-m41 output-directory input-directory selection [samples] [repetitions] [evolving-steps].");
        }

        M41Runner.Run(args[1], args[2], args[3],
            args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 15,
            args.Length > 5 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 3,
            args.Length > 6 ? int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 0);
        return 0;
    }

    if (args.Length > 0 && args[0] == "compare")
    {
        string outputDirectory = args.Length > 1 ? args[1] : "artifacts/benchmarks/m4";
        int samples = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 31;
        string selection = args.Length > 3 ? args[3] : "all";
        int repetitions = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 3;
        if (args.Length > 5)
        {
            throw new ArgumentException("Too many compare arguments.");
        }

        ComparisonRunner.Run(outputDirectory, samples, selection, repetitions);
        return 0;
    }

    if (args.Length > 0 && args[0] == "latency")
    {
        string outputDirectory = args.Length > 1 ? args[1] : "artifacts/benchmarks/reference";
        int samples = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 31;
        string selection = args.Length > 3 ? args[3] : "quick";
        if (args.Length > 4)
        {
            throw new ArgumentException("Too many latency arguments.");
        }

        LatencyRunner.Run(outputDirectory, samples, selection);
        return 0;
    }

    if (args.Length > 1 && args[0] == "bdn")
    {
        string inputDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("PARTICLELIFE_BENCHMARK_INPUTS")
            ?? Path.Combine("artifacts", "benchmarks", "bdn-inputs"));
        Directory.CreateDirectory(inputDirectory);
        Environment.SetEnvironmentVariable("PARTICLELIFE_BENCHMARK_INPUTS", inputDirectory);
        File.WriteAllText(Path.Combine(inputDirectory, "session.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            environment = LatencyRunner.CaptureEnvironment("direct and uniform-grid; serial double precision"),
            arguments = args
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        BenchmarkSwitcher.FromAssembly(typeof(DirectStepBenchmarks).Assembly).Run(args[1..]);
        return 0;
    }

    Console.WriteLine("Particle Life CPU benchmarks (run Release without a debugger)");
    Console.WriteLine("  prepare-m41 [input-directory] [retained-M4-directory]");
    Console.WriteLine("  profile-m41 input-experiment.json output-profile.json");
    Console.WriteLine("  compare-m41 output-directory input-directory all|static|cutoff|evolved|representative|case|file [samples] [repetitions] [evolving-steps]");
    Console.WriteLine("  compare [output-directory] [samples >= 5] [quick|all|case-name|experiment.json] [repetitions >= 1]");
    Console.WriteLine("  latency [output-directory] [samples >= 5] [quick|all|case-name|experiment.json]");
    Console.WriteLine("  bdn --filter '*uniform-fixed-500*' --job short");
    Console.WriteLine("  bdn --filter '*' --job short");
    Console.WriteLine("Cases: " + string.Join(", ", BenchmarkWorkload.AllCases));
    return args.Length == 0 || args[0] is "--help" or "-h" ? 0 : 2;
}
catch (Exception error) when (error is ArgumentException or FormatException or ArithmeticException or IOException
    or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
