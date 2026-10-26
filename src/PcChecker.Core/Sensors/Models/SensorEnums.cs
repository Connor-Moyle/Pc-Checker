namespace PcChecker.Core.Sensors;

/// <summary>Which part of the PC a sensor belongs to.</summary>
public enum HardwareKind
{
    Cpu,
    Gpu,
    Memory,
    Motherboard,
    Storage,
    Network,
    Psu,
    Cooler,
    Battery,
    Other,
}

/// <summary>What a sensor measures.</summary>
public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Voltage,
    Fan,
    Flow,
    Control,
    Data,
    Throughput,
    Level,
    Factor,
    Other,
}
