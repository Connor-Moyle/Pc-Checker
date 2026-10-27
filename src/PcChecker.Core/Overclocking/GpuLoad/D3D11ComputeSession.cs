using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// The Direct3D 11 resources for one GPU load run on one adapter: device, compiled compute shader, the work buffer
/// and its view, a constant buffer, event queries for in-flight dispatches and a small staging buffer for read-back.
/// Only used from the generator's own thread (the immediate context is not thread-safe).
/// </summary>
internal sealed class D3D11ComputeSession : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    /// <summary>A dispatch that has not finished after this long is treated as a hang.</summary>
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(10);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _output;
    private readonly ID3D11UnorderedAccessView _view;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11Buffer _staging;
    private readonly ID3D11Query[] _queries;
    private readonly Queue<int> _pending = new();
    private readonly int _groupsX;
    private readonly int _groupsY;
    private int _nextQuery;

    [StructLayout(LayoutKind.Sequential)]
    private struct LoadParams
    {
        public uint Iterations;
        public uint ElementCount;
        public uint GroupsX;
        public uint Seed;
    }

    private D3D11ComputeSession(ID3D11Device device, ID3D11DeviceContext context, ID3D11ComputeShader shader, ID3D11Buffer output,
        ID3D11UnorderedAccessView view, ID3D11Buffer constants, ID3D11Buffer staging, ID3D11Query[] queries, GpuLoadPlan plan)
    {
        _device = device;
        _context = context;
        _shader = shader;
        _output = output;
        _view = view;
        _constants = constants;
        _staging = staging;
        _queries = queries;
        Plan = plan;
        (_groupsX, _groupsY) = GpuLoadPlanner.GroupCounts(plan.Elements);
    }

    public GpuLoadPlan Plan { get; }

    public FeatureLevel FeatureLevel { get; private init; }

    public int PendingCount => _pending.Count;

    /// <summary>Creates the device on <paramref name="adapter"/>, compiles the shader and allocates the buffers.</summary>
    public static D3D11ComputeSession Create(IDXGIAdapter1 adapter, GpuLoadPlan plan)
    {
        var result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FeatureLevels, out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
        {
            throw new InvalidOperationException($"Direct3D 11 could not start on this graphics card (0x{result.Code:X8}). It needs a DirectX 11 (feature level 11_0) driver.");
        }

        var created = new List<IDisposable> { context, device };
        try
        {
            ReadOnlyMemory<byte> bytecode;
            try
            {
                bytecode = Compiler.Compile(GpuLoadShader.Source, GpuLoadShader.EntryPoint, GpuLoadShader.SourceName, GpuLoadShader.Profile, ShaderFlags.OptimizationLevel3, EffectFlags.None);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The GPU test shader could not be compiled (d3dcompiler_47.dll is missing or failed): " + ex.Message, ex);
            }

            var shader = device.CreateComputeShader(bytecode.Span, null);
            created.Add(shader);

            var output = device.CreateBuffer(new BufferDescription((uint)plan.BufferBytes, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, GpuLoadPlanner.ElementBytes));
            created.Add(output);
            var view = device.CreateUnorderedAccessView(output, new UnorderedAccessViewDescription(output, Format.Unknown, 0, (uint)plan.Elements, BufferUnorderedAccessViewFlags.None));
            created.Add(view);
            var constants = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
            created.Add(constants);
            var staging = device.CreateBuffer(new BufferDescription((uint)(plan.VerifyWindowElements * GpuLoadPlanner.ElementBytes), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
            created.Add(staging);

            var queries = new ID3D11Query[Math.Max(1, plan.MaxInFlight)];
            for (int i = 0; i < queries.Length; i++)
            {
                queries[i] = device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
                created.Add(queries[i]);
            }

            return new D3D11ComputeSession(device, context, shader, output, view, constants, staging, queries, plan) { FeatureLevel = level };
        }
        catch
        {
            for (int i = created.Count - 1; i >= 0; i--)
            {
                created[i].Dispose();
            }

            throw;
        }
    }

    /// <summary>Sets the iteration count for the following dispatches.</summary>
    public void SetIterations(int iterations)
    {
        var p = new LoadParams
        {
            Iterations = (uint)Math.Max(1, iterations),
            ElementCount = (uint)Plan.Elements,
            GroupsX = (uint)_groupsX,
            Seed = GpuLoadShader.Seed,
        };
        _context.UpdateSubresource(in p, _constants);
    }

    /// <summary>Queues one dispatch over the whole buffer and marks it with an event query.</summary>
    public void Dispatch()
    {
        _context.CSSetShader(_shader);
        _context.CSSetConstantBuffer(0, _constants);
        _context.CSSetUnorderedAccessView(0, _view, uint.MaxValue);
        _context.Dispatch((uint)_groupsX, (uint)_groupsY, 1);
        int slot = _nextQuery;
        _nextQuery = (_nextQuery + 1) % _queries.Length;
        _context.End(_queries[slot]);
        _context.Flush();
        _pending.Enqueue(slot);
    }

    /// <summary>
    /// Waits for the oldest queued dispatch; returns how long the wait took in milliseconds, or <see cref="double.NaN"/>
    /// when cancellation gave up on a dispatch that had not finished (it is then not counted as done).
    /// </summary>
    public double WaitOldest(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return 0;
        }

        int slot = _pending.Peek();
        var watch = Stopwatch.StartNew();
        long nextRemovedCheck = 100;
        while (!_context.IsDataAvailable(_queries[slot]))
        {
            if (watch.ElapsedMilliseconds >= nextRemovedCheck)
            {
                ThrowIfDeviceLost();
                nextRemovedCheck = watch.ElapsedMilliseconds + 100;
            }

            if (watch.Elapsed > HangTimeout)
            {
                ThrowIfDeviceLost();
                throw new TimeoutException("The graphics card did not finish a test step within 10 seconds and stopped responding.");
            }

            // Cancellation is honoured between dispatches; a single dispatch is ~100 ms at most, so stopping stays well under a second.
            if (cancellationToken.IsCancellationRequested && watch.ElapsedMilliseconds > 1000)
            {
                _pending.Dequeue();
                return double.NaN;
            }

            Thread.Sleep(watch.ElapsedMilliseconds < 5 ? 0 : 1);
        }

        _pending.Dequeue();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Waits for every queued dispatch; returns how many finished.</summary>
    public int Drain(CancellationToken cancellationToken)
    {
        int drained = 0;
        while (_pending.Count > 0)
        {
            if (!double.IsNaN(WaitOldest(cancellationToken)))
            {
                drained++;
            }
        }

        return drained;
    }

    /// <summary>Copies <see cref="GpuLoadPlan.VerifyWindowElements"/> elements starting at <paramref name="firstElement"/> back to the CPU as floats.</summary>
    public float[] ReadBack(int firstElement)
    {
        int bytes = Plan.VerifyWindowElements * GpuLoadPlanner.ElementBytes;
        int left = firstElement * GpuLoadPlanner.ElementBytes;
        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _output, 0, new Box(left, 0, 0, left + bytes, 1, 1));
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var span = MemoryMarshal.Cast<byte, float>(mapped.AsSpan(bytes));
            return span.ToArray();
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    /// <summary>Throws <see cref="GpuDeviceLostException"/> when Windows reports the device as removed or reset.</summary>
    public void ThrowIfDeviceLost()
    {
        var reason = _device.DeviceRemovedReason;
        if (reason.Failure)
        {
            throw new GpuDeviceLostException(reason.Code);
        }
    }

    public void Dispose()
    {
        try
        {
            _context.ClearState();
            _context.Flush();
        }
        catch (Exception)
        {
            // The device may already be gone after a driver reset; releasing the objects below is all that matters.
        }

        foreach (var q in _queries)
        {
            q.Dispose();
        }

        _staging.Dispose();
        _constants.Dispose();
        _view.Dispose();
        _output.Dispose();
        _shader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
