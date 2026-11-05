using BenchmarkDotNet.Running;
using ParticleLife.Benchmarks;

try
{
    if (args.Length > 0 && args[0] == "prepare-p2")
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("Use prepare-p2 fresh-output-directory saved-input.json.");
        }
        P2Candidates.Prepare(args[1], args[2]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "measure-p2")
    {
        if (args.Length != 6)
        {
            throw new ArgumentException("Use measure-p2 output.json input.json workers static|evolving samples.");
        }
        P2Runner.Run(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
            args[4], int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

    if (args.Length > 0 && args[0] == "profile-p2")
    {
        if (args.Length != 4)
        {
            throw new ArgumentException("Use profile-p2 output.json input.json samples.");
        }
        P2Candidates.Run(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

    if (args.Length > 0 && args[0] == "prepare-p1")
    {
        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException("Use prepare-p1 input-directory [screen|density|ladder|stress|stretch|all|case].");
        }

        P1Inputs.Prepare(args[1], args.Length > 2 ? args[2] : "screen");
        return 0;
    }

    if (args.Length > 0 && args[0] == "measure-p1")
    {
        if (args.Length is < 3 or > 8)
        {
            throw new ArgumentException("Use measure-p1 output-directory input-directory [selection] [samples] [timeout-seconds] [workers] [evolving|static].");
        }

        P1Runner.Run(args[1], args[2], args.Length > 3 ? args[3] : "screen",
            args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 5,
            args.Length > 5 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 30,
            args.Length > 6 ? int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 1,
            args.Length > 7 ? args[7] : "evolving");
        return 0;
    }

    if (args.Length > 0 && args[0] == "_measure-p1-case")
    {
        if (args.Length != 7)
        {
            throw new ArgumentException("Internal P1 child invocation requires six arguments.");
        }

        P1Runner.MeasureCase(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), args[5], args[6]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "editor")
    {
        if (args.Length is < 3 or > 4)
        {
            throw new ArgumentException("Use editor output-directory input-experiment.json [samples].");
        }

        EditorRunner.Run(args[1], args[2], args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 101);
        return 0;
    }

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
    Console.WriteLine("  prepare-p2 fresh-output-directory saved-input.json");
    Console.WriteLine("  measure-p2 output.json input.json workers static|evolving samples");
    Console.WriteLine("  profile-p2 output.json input.json samples");
    Console.WriteLine("  prepare-p1 input-directory [screen|density|ladder|stress|stretch|all|case]");
    Console.WriteLine("  measure-p1 output-directory input-directory [screen|density|ladder|stress|stretch|all|case] [samples >= 5] [timeout-seconds] [workers] [evolving|static]");
    Console.WriteLine("  editor output-directory input-experiment.json [samples >= 20]");
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
