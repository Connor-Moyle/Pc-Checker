using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Vendor-neutral GPU load through Direct3D 11 compute (NVIDIA, AMD and Intel, integrated graphics included).
/// Picks a hardware DXGI adapter (default: the one with the most dedicated memory), compiles
/// <see cref="GpuLoadShader"/>, calibrates the work so one dispatch takes 50–100 ms, then dispatches back to back
/// (Heavy keeps two in flight). Every couple of seconds a window of the result buffer is read back and compared with
/// the first read of that window; differences are counted as errors. A driver reset (TDR) ends the run with
/// <see cref="GpuLoadResult.DriverReset"/> set. All DirectX calls are guarded: failures come back as results.
/// </summary>
public sealed class D3D11ComputeLoadGenerator : IGpuLoadGenerator
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;

    public D3D11ComputeLoadGenerator(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<GpuAdapterChoice> ListAdapters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return GpuAdapterRanking.Rank(EnumerateAdapters());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graphics adapters could not be listed for the GPU stress test.");
            return [];
        }
    }

    public Task<GpuLoadResult> RunAsync(GpuLoadOptions options, IProgress<GpuLoadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Factory.StartNew(() =>
        {
            // The feeding thread must keep up even while the CPU burn occupies every core in a combined test.
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.AboveNormal;
                return Run(options, progress, cancellationToken);
            }
            finally
            {
                thread.Priority = previous;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static List<GpuAdapterInfo> EnumerateAdapters()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var list = new List<GpuAdapterInfo>();
        for (uint i = 0; i < 16; i++)
        {
            if (factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                break;
            }

            using (adapter)
            {
                var d = adapter.Description1;
                list.Add(new GpuAdapterInfo((int)i, (d.Description ?? string.Empty).Trim(), d.VendorId, d.DeviceId,
                    (long)(ulong)d.DedicatedVideoMemory, (long)(ulong)d.SharedSystemMemory, (d.Flags & AdapterFlags.Software) != 0));
            }
        }

        return list;
    }

    private GpuLoadResult Run(GpuLoadOptions options, IProgress<GpuLoadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return GpuLoadResult.CouldNotStart("GPU", "The built-in GPU load needs Windows with DirectX 11.");
        }

        GpuAdapterChoice? choice;
        try
        {
            choice = GpuAdapterRanking.Pick(GpuAdapterRanking.Rank(EnumerateAdapters()), options.AdapterIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DXGI adapter enumeration failed.");
            return GpuLoadResult.CouldNotStart("GPU", "Windows did not list any graphics adapters (" + ex.Message + ").");
        }

        if (choice is null)
        {
            return GpuLoadResult.CouldNotStart("GPU", "No DirectX 11 graphics card was found (only the Microsoft software renderer is available).");
        }

        var plan = GpuLoadPlanner.Create(options.Intensity, choice.DedicatedVideoMemoryBytes);
        D3D11ComputeSession? session = null;
        var watch = Stopwatch.StartNew();
        long dispatches = 0, errors = 0;
        int iterations = 0;
        try
        {
            using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
            {
                factory.EnumAdapters1((uint)choice.Index, out IDXGIAdapter1? adapter).CheckError();
                using (adapter)
                {
                    session = D3D11ComputeSession.Create(adapter!, plan);
                }
            }

            _logger.LogInformation("GPU load started on {Adapter} ({Level}): {Intensity}, buffer {Bytes} bytes, {Elements} elements.",
                choice.Name, session.FeatureLevel, options.Intensity, plan.BufferBytes, plan.Elements);

            var calibrator = new DispatchCalibrator(plan.InitialIterations, plan.TargetDispatchMs);
            var references = new float[]?[plan.VerifyWindows];
            var rate = new RateMeter();
            var lastReport = TimeSpan.Zero;
            int verifications = 0, sinceVerify = 0;
            iterations = calibrator.Iterations;
            session.SetIterations(iterations);

            while (watch.Elapsed < options.Duration && !cancellationToken.IsCancellationRequested)
            {
                session.Dispatch();
                if (session.PendingCount < plan.MaxInFlight && calibrator.IsCalibrated)
                {
                    continue;
                }

                double ms = session.WaitOldest(cancellationToken);
                dispatches++;
                sinceVerify++;
                rate.Add(watch.Elapsed.TotalSeconds, dispatches);

                // Calibrate on single in-flight dispatches, and keep watching for a dangerously slow one afterwards.
                int next = calibrator.Observe(ms);
                if (next != iterations)
                {
                    dispatches += session.Drain(cancellationToken);
                    iterations = next;
                    session.SetIterations(iterations);
                    Array.Clear(references);
                    sinceVerify = 0;
                }

                if (options.VerifyResults && calibrator.IsCalibrated && sinceVerify >= plan.VerifyEveryDispatches)
                {
                    dispatches += session.Drain(cancellationToken);
                    int offset = GpuLoadPlanner.WindowOffset(verifications, plan.Elements, plan.VerifyWindowElements, plan.VerifyWindows);
                    var values = session.ReadBack(offset);
                    long found = GpuResultComparer.Check(references, GpuLoadPlanner.WindowSlot(verifications, plan.VerifyWindows), values);
                    if (found > 0)
                    {
                        _logger.LogWarning("GPU load verification found {Count} wrong values in the window at element {Offset}.", found, offset);
                    }

                    errors += found;
                    verifications++;
                    sinceVerify = 0;
                }

                var idle = GpuLoadPlanner.IdleAfterDispatch(ms, plan.DutyCycle);
                if (idle > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(idle))
                {
                    break;
                }

                if (watch.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = watch.Elapsed;
                    progress?.Report(new GpuLoadProgress(watch.Elapsed, rate.Rate, errors));
                }
            }

            dispatches += session.Drain(cancellationToken);
            session.ThrowIfDeviceLost();
            progress?.Report(new GpuLoadProgress(watch.Elapsed, rate.Rate, errors));
            _logger.LogInformation("GPU load finished: {Dispatches} dispatches, {Errors} errors, {Iterations} iterations per element.", dispatches, errors, iterations);
            return new GpuLoadResult(true, dispatches, errors, choice.Name, null, watch.Elapsed) { Iterations = iterations, BufferBytes = plan.BufferBytes };
        }
        catch (Exception ex) when (DeviceLostCode(ex, session) is { } code)
        {
            _logger.LogError(ex, "The graphics driver reset during the GPU load (0x{Code:X8}).", code);
            return new GpuLoadResult(false, dispatches, errors, choice.Name, GpuDeviceLoss.Describe(code), watch.Elapsed)
            {
                DriverReset = true,
                Iterations = iterations,
                BufferBytes = plan.BufferBytes,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The GPU load stopped with an error.");
            bool started = session is not null;
            string reason = started ? "The GPU test stopped with an error: " + ex.Message : ex.Message;
            return new GpuLoadResult(false, dispatches, errors, choice.Name, reason, watch.Elapsed)
            {
                NotStarted = !started,
                Iterations = iterations,
                BufferBytes = plan.BufferBytes,
            };
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>The DXGI code when <paramref name="ex"/> (or the device's removed reason) says the driver reset.</summary>
    private static int? DeviceLostCode(Exception ex, D3D11ComputeSession? session)
    {
        if (ex is GpuDeviceLostException lost)
        {
            return lost.HResult;
        }

        if (ex is SharpGenException sg && GpuDeviceLoss.IsDeviceLost(sg.ResultCode.Code))
        {
            return sg.ResultCode.Code;
        }

        if (session is not null && ex is not OperationCanceledException)
        {
            try
            {
                session.ThrowIfDeviceLost();
            }
            catch (GpuDeviceLostException inner)
            {
                return inner.HResult;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }
}
