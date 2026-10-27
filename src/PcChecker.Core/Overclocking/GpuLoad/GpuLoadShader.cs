namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// The HLSL compute shader behind the GPU load. Each thread derives a starting value from its index, runs a
/// deterministic loop of multiply-add, sine and cosine (the iteration count is calibrated at run time) and writes
/// one float4. The math is bounded (values stay within ±3) and has no data races, so an unchanged card must produce
/// bit-identical output on every dispatch; anything else is a hardware or stability error.
/// </summary>
public static class GpuLoadShader
{
    public const string EntryPoint = "main";
    public const string Profile = "cs_5_0";
    public const string SourceName = "PcCheckerGpuLoad.hlsl";

    /// <summary>Fixed seed; outputs depend only on the element index and the iteration count.</summary>
    public const uint Seed = 0x5EED_2024;

    public const string Source = """
        // PC Checker GPU load: vendor-neutral Direct3D 11 compute work with checkable results.
        cbuffer LoadParams : register(b0)
        {
            uint Iterations;
            uint ElementCount;
            uint GroupsX;
            uint Seed;
        };

        RWStructuredBuffer<float4> Output : register(u0);

        float4 StartValue(uint index)
        {
            uint h = index * 747796405u + 2891336453u + Seed;
            h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
            h = (h >> 22u) ^ h;
            float a = (h & 0xFFFFu) / 65535.0;
            float b = (h >> 16u) / 65535.0;
            return float4(a, b, 1.0 - a, 0.5 * (a + b));
        }

        [numthreads(256, 1, 1)]
        void main(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
        {
            uint index = (group.y * GroupsX + group.x) * 256u + local.x;
            if (index >= ElementCount)
            {
                return;
            }

            float4 v = StartValue(index);
            float4 acc = float4(0.0, 0.0, 0.0, 0.0);
            [loop]
            for (uint n = 0u; n < Iterations; n++)
            {
                v = mad(v, float4(0.9997, 1.0003, 0.9991, 1.0007), float4(0.0013, -0.0007, 0.0021, -0.0011));
                float4 s = sin(v * 6.2831853);
                float4 c = cos(v.wzyx * 6.2831853);
                acc = mad(s, c, acc * 0.5);
                v = frac(v + acc * 0.25);
            }

            Output[index] = acc + v;
        }
        """;
}
