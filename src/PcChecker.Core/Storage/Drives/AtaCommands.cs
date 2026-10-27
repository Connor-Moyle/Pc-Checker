using System.Buffers.Binary;

namespace PcChecker.Core.Storage;

/// <summary>Facts from the 512-byte ATA IDENTIFY DEVICE data.</summary>
public sealed record AtaIdentity(
    string Model,
    string Serial,
    string Firmware,
    int? RotationRate,
    int? SataCurrentGeneration,
    int? SataMaxGeneration,
    bool SmartSupported,
    bool SmartEnabled);

/// <summary>
/// ATA command building and parsing for SMART over the different Windows paths: SMART_RCV_DRIVE_DATA,
/// IOCTL_ATA_PASS_THROUGH and SCSI/ATA Translation (SAT) ATA PASS-THROUGH (12) and (16) for USB bridges. Pure.
/// </summary>
public static class AtaCommands
{
    public const byte SmartCommand = 0xB0;
    public const byte IdentifyCommand = 0xEC;
    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;
    public const byte SmartLbaMid = 0x4F;
    public const byte SmartLbaHigh = 0xC2;

    /// <summary>One ATA register set (task file).</summary>
    public readonly record struct TaskFile(byte Features, byte SectorCount, byte LbaLow, byte LbaMid, byte LbaHigh, byte Device, byte Command);

    public static TaskFile SmartRead(byte feature) => new(feature, 1, 1, SmartLbaMid, SmartLbaHigh, 0xA0, SmartCommand);

    public static TaskFile Identify() => new(0, 1, 0, 0, 0, 0xA0, IdentifyCommand);

    /// <summary>ATA CHECK POWER MODE (E5h): reports standby / idle / active in the count register without spinning the drive up.</summary>
    public const byte CheckPowerModeCommand = 0xE5;

    public static TaskFile CheckPowerMode() => new(0, 0, 0, 0, 0, 0xA0, CheckPowerModeCommand);

    /// <summary>
    /// SAT ATA PASS-THROUGH (16) for a non-data command (protocol 3) with CK_COND set, so the bridge returns the ATA
    /// registers in the sense data.
    /// </summary>
    public static byte[] SatCdb16NonData(TaskFile tf) =>
    [
        0x85,
        3 << 1,       // protocol: non-data
        0x20,         // CK_COND: return the registers, no transfer
        0, tf.Features,
        0, tf.SectorCount,
        0, tf.LbaLow,
        0, tf.LbaMid,
        0, tf.LbaHigh,
        tf.Device,
        tf.Command,
        0,
    ];

    /// <summary>
    /// The ATA count register from SAT sense data ("ATA pass-through information available", ASC/ASCQ 00h/1Dh): the
    /// ATA Status Return descriptor (09h) in descriptor format, or the INFORMATION field in fixed format. Null when the
    /// bridge did not return the registers.
    /// </summary>
    public static byte? CountFromSatSense(ReadOnlySpan<byte> sense)
    {
        if (sense.Length < 8)
        {
            return null;
        }

        int code = sense[0] & 0x7F;
        if (code is 0x72 or 0x73)
        {
            if (sense[2] != 0x00 || sense[3] != 0x1D)
            {
                return null;
            }

            int end = Math.Min(sense.Length, 8 + sense[7]);
            for (int i = 8; i + 1 < end;)
            {
                int length = sense[i + 1];
                if (sense[i] == 0x09 && length >= 12 && i + 13 < sense.Length)
                {
                    return sense[i + 5];
                }

                i += 2 + length;
            }

            return null;
        }

        if (code is 0x70 or 0x71 && sense.Length >= 14 && sense[12] == 0x00 && sense[13] == 0x1D)
        {
            return sense[6]; // INFORMATION: ERROR, STATUS, DEVICE, COUNT
        }

        return null;
    }

    /// <summary>CHECK POWER MODE count: 00h/01h standby (spun down), 40h/41h/80h–83h/FFh idle or active.</summary>
    public static DrivePowerState PowerFromCheckPowerMode(byte? count) => count switch
    {
        0x00 or 0x01 => DrivePowerState.Asleep,
        0x40 or 0x41 or 0x80 or 0x81 or 0x82 or 0x83 or 0xFF => DrivePowerState.Active,
        _ => DrivePowerState.Unknown,
    };

    /// <summary>
    /// SAT ATA PASS-THROUGH (12), opcode A1h: PIO data-in (protocol 4), transfer direction from the device, length in
    /// sectors taken from the sector count field.
    /// </summary>
    public static byte[] SatCdb12(TaskFile tf) =>
    [
        0xA1,
        4 << 1,       // protocol: PIO data-in
        0x0E,         // T_DIR = in, BYT_BLOK = blocks, T_LENGTH = sector count field
        tf.Features,
        tf.SectorCount,
        tf.LbaLow,
        tf.LbaMid,
        tf.LbaHigh,
        tf.Device,
        tf.Command,
        0,
        0,
    ];

    /// <summary>SAT ATA PASS-THROUGH (16), opcode 85h, with the same protocol and the 28-bit registers in the low bytes.</summary>
    public static byte[] SatCdb16(TaskFile tf) =>
    [
        0x85,
        4 << 1,
        0x0E,
        0, tf.Features,
        0, tf.SectorCount,
        0, tf.LbaLow,
        0, tf.LbaMid,
        0, tf.LbaHigh,
        tf.Device,
        tf.Command,
        0,
    ];

    /// <summary>Reads model, serial, firmware, rotation rate (word 217), SATA speeds (words 76 and 77) and SMART flags (words 82, 85).</summary>
    public static AtaIdentity? ParseIdentify(ReadOnlySpan<byte> data)
    {
        if (data.Length < 512)
        {
            return null;
        }

        var bytes = data.ToArray();
        int Word(int index) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index * 2, 2));

        string serial = DriveSerial.AtaString(data, 10, 10);
        string firmware = DriveSerial.AtaString(data, 23, 4);
        string model = DriveSerial.AtaString(data, 27, 20);

        int rotation = Word(217);
        int? rotationRate = rotation is 0 or 0xFFFF ? null : rotation;

        int capabilities = Word(76);
        int? maxGen = null;
        if (capabilities is not 0 and not 0xFFFF)
        {
            maxGen = (capabilities & 0x0008) != 0 ? 3 : (capabilities & 0x0004) != 0 ? 2 : (capabilities & 0x0002) != 0 ? 1 : (int?)null;
        }

        int additional = Word(77);
        int gen = (additional >> 1) & 0x7;
        int? currentGen = additional is not 0 and not 0xFFFF && gen is >= 1 and <= 3 ? gen : null;

        int commandSets = Word(82);
        int enabledSets = Word(85);
        bool supported = commandSets is not 0xFFFF && (commandSets & 0x0001) != 0;
        bool enabled = enabledSets is not 0xFFFF && (enabledSets & 0x0001) != 0;
        return new AtaIdentity(model, serial, firmware, rotationRate, currentGen, maxGen, supported, enabled);
    }

    /// <summary>True when a SMART data sector looks real (not all zeros or all 0xFF, as a bridge that drops the command returns).</summary>
    public static bool LooksLikeSmartData(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 362)
        {
            return false;
        }

        bool anyId = false;
        for (int i = 0; i < 30; i++)
        {
            byte id = sector[2 + i * 12];
            if (id is not 0 and not 0xFF)
            {
                anyId = true;
                break;
            }
        }

        return anyId;
    }
}
