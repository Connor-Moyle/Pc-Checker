using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;

namespace PcChecker.Core.Reports;

public enum ReportFormat
{
    Html,
    Json,
    Markdown,
}

public sealed record ReportBundle(
    HardwareInventory? Hardware,
    HealthReport? Health,
    PerformanceReport? Performance,
    SoftwareInventory? Software,
    DriverScanResult? Drivers,
    SensorSnapshot? Sensors,
    DateTimeOffset Generated,
    string AppVersion);
