using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ZstdSharp;

// Minimal reproduction of the streaming compression loop used by
// GenHTTP's ZstdCompressingSink (Modules/Compression/Providers/ZstdCompressingSink.cs).
// No GenHTTP / HTTP / sockets involved - this only exercises ZstdSharp.Port's
// Compressor.WrapStream/FlushStream API.
//
// Observed symptom upstream: on the macOS Intel (x64) GitHub Actions runner,
// running under the .NET 11 SDK, a request being compressed via this code path
// occasionally never completes (the client times out waiting for the response).
// The same code never hangs on net10.0, nor on any other OS/arch, nor locally.
//
// This program runs the same compress loop many times, sequentially and
// concurrently, across all three compression levels GenHTTP exposes, and
// flags any single compression call that does not finish within a generous
// watchdog window.

const string Payload = "Payload validated via zstd compression. ";

var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Payload, 20)));

var levels = new[] { 1, 3, 22 }; // Fastest, Optimal, SmallestSize (see ZstdAlgorithm.MapLevel)

Console.WriteLine($".NET: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS:   {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine($"Proc: {RuntimeInformation.ProcessArchitecture}, CPUs: {Environment.ProcessorCount}");
Console.WriteLine();

var failed = false;

Console.WriteLine("=== Phase 1: sequential ===");
failed |= !RunSequential(levels, data, iterations: 300, watchdog: TimeSpan.FromSeconds(5));

Console.WriteLine();
Console.WriteLine("=== Phase 2: concurrent ===");
failed |= !RunConcurrent(levels, data, workers: Environment.ProcessorCount * 4, iterationsPerWorker: 300, watchdog: TimeSpan.FromSeconds(60));

Console.WriteLine();
Console.WriteLine(failed ? "REPRO: FAILED (hang or error detected)" : "REPRO: OK (no hang observed)");

return failed ? 1 : 0;

static bool RunSequential(int[] levels, byte[] data, int iterations, TimeSpan watchdog)
{
    foreach (var level in levels)
    {
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();

            Exception? error = null;
            var done = new ManualResetEventSlim(false);

            var worker = new Thread(() =>
            {
                try
                {
                    Compress(data, level);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true
            };

            worker.Start();

            if (!done.Wait(watchdog))
            {
                Console.WriteLine($"  HANG: level={level} iteration={i} did not complete within {watchdog} (elapsed {sw.Elapsed})");
                return false;
            }

            if (error != null)
            {
                Console.WriteLine($"  ERROR: level={level} iteration={i}: {error}");
                return false;
            }
        }

        Console.WriteLine($"  level={level}: {iterations} sequential iterations completed");
    }

    return true;
}

static bool RunConcurrent(int[] levels, byte[] data, int workers, int iterationsPerWorker, TimeSpan watchdog)
{
    var errors = new List<Exception>();
    var errorLock = new object();

    var task = Task.Run(() =>
    {
        Parallel.For(0, workers, w =>
        {
            var rng = new Random(w);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var level = levels[rng.Next(levels.Length)];

                try
                {
                    Compress(data, level);
                }
                catch (Exception ex)
                {
                    lock (errorLock)
                    {
                        errors.Add(ex);
                    }

                    return;
                }
            }
        });
    });

    if (!task.Wait(watchdog))
    {
        Console.WriteLine($"  HANG: {workers} concurrent workers x {iterationsPerWorker} iterations did not complete within {watchdog}");
        return false;
    }

    if (errors.Count > 0)
    {
        Console.WriteLine($"  ERROR: {errors.Count} exception(s) during concurrent run, first: {errors[0]}");
        return false;
    }

    Console.WriteLine($"  {workers} concurrent workers x {iterationsPerWorker} iterations completed");
    return true;
}

// Mirrors ZstdCompressingSink.CompressChunk + FinalFlush, but writes into a
// plain growable buffer instead of GenHTTP's IBufferWriter<byte> abstraction.
static void Compress(byte[] input, int level)
{
    using var compressor = new Compressor(level);

    var output = new ArrayBufferWriter<byte>();
    var remaining = input.AsSpan();

    OperationStatus status;

    do
    {
        var span = output.GetSpan(256);
        status = compressor.WrapStream(remaining, span, out var consumed, out var written, isFinalBlock: false);
        if (written == 0 && status == OperationStatus.DestinationTooSmall)
            throw new InvalidOperationException($"WrapStream made no progress (level={level}, remaining={remaining.Length}, spanLen={span.Length})");
        output.Advance(written);
        remaining = remaining.Slice(consumed);
    }
    while (status == OperationStatus.DestinationTooSmall);

    do
    {
        var span = output.GetSpan(256);
        status = compressor.FlushStream(span, out var written, isFinalBlock: true);
        if (written == 0 && status != OperationStatus.Done)
            throw new InvalidOperationException($"FlushStream made no progress (level={level}, spanLen={span.Length}, status={status})");
        output.Advance(written);
    }
    while (status != OperationStatus.Done);
}
