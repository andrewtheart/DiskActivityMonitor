using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Reads a drive's own lifetime host-write/read totals straight from the device, the same data
/// that SSD endurance (TBW) is measured against. Unlike the OS performance counters, these
/// figures are cumulative over the drive's entire life and survive reboots and OS reinstalls.
///
/// Two native, dependency-free paths are used (no smartctl required):
///  - NVMe: the SMART/Health Information log (log page 0x02) via IOCTL_STORAGE_QUERY_PROPERTY,
///    reading "Data Units Written/Read" (each unit = 512,000 bytes) and "Percentage Used".
///  - ATA/SATA: SMART READ DATA via IOCTL_ATA_PASS_THROUGH, reading attribute 241 "Total LBAs
///    Written" and 242 "Total LBAs Read" (each LBA = 512 bytes).
///
/// All failures are swallowed and surfaced as <c>null</c>; the NVMe query works unelevated, while
/// the ATA pass-through generally needs the caller (the collector service) to be elevated.
/// </summary>
public static class SmartLifetimeReader
{
    /// <summary>
    /// Health and lifetime values reported directly by a drive. The extended fields are populated
    /// by NVMe SMART/Health log page 0x02; ATA 241/242 reads expose only lifetime bytes.
    /// </summary>
    public readonly record struct SmartLifetime(
        long BytesWritten,
        long BytesRead,
        int? PercentUsed,
        int? TemperatureC = null,
        int? AvailableSparePercent = null,
        int? CriticalWarning = null,
        long? PowerOnHours = null,
        long? UnsafeShutdowns = null,
        long? MediaErrors = null);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;
    private const uint IOCTL_ATA_PASS_THROUGH = 0x0004d02c;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int PropertyStandardQuery = 0;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint ret, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// Reads lifetime totals for <c>\\.\PhysicalDrive{driveNumber}</c>, or null if the drive does
    /// not expose them. The NVMe health query uses a zero-access handle and is always safe. The ATA
    /// pass-through requires a raw read/write disk handle, which Windows Controlled Folder Access
    /// blocks (and pops a user notification for) on USB / virtual / removable disks; it is therefore
    /// only attempted when <paramref name="allowAtaPassthrough"/> is set for a genuine internal
    /// ATA/SATA drive. Those other buses do not expose the ATA 241/242 attributes anyway.
    /// </summary>
    public static SmartLifetime? Read(int driveNumber, bool allowAtaPassthrough = false)
    {
        string path = $"\\\\.\\PhysicalDrive{driveNumber}";
        try
        {
            var nvme = ReadNvme(path);
            if (nvme is not null) return nvme;
            return allowAtaPassthrough ? ReadAta(path) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- NVMe

    private static SmartLifetime? ReadNvme(string path)
    {
        // A zero-access handle is enough for the query IOCTL and, unlike a read/write handle, does
        // not trip Controlled Folder Access, so we never escalate the requested access here.
        IntPtr h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == InvalidHandle) return null;

        try
        {
            const int header = 8;   // STORAGE_PROPERTY_QUERY: PropertyId(4) + QueryType(4)
            const int pspd = 40;    // STORAGE_PROTOCOL_SPECIFIC_DATA
            const int logLen = 512; // NVMe SMART/Health log
            int total = header + pspd + logLen;
            IntPtr buf = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, StorageDeviceProtocolSpecificProperty);
                Marshal.WriteInt32(buf, 4, PropertyStandardQuery);
                int p = header;
                Marshal.WriteInt32(buf, p + 0, ProtocolTypeNvme);
                Marshal.WriteInt32(buf, p + 4, NVMeDataTypeLogPage);
                Marshal.WriteInt32(buf, p + 8, 0x02); // health/SMART log page
                Marshal.WriteInt32(buf, p + 12, 0);
                Marshal.WriteInt32(buf, p + 16, pspd); // ProtocolDataOffset
                Marshal.WriteInt32(buf, p + 20, logLen);

                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total, buf, (uint)total, out _, IntPtr.Zero))
                    return null;

                int dataOffset = Marshal.ReadInt32(buf, p + 16);
                int logStart = p + dataOffset;
                if (logStart + 64 > total) return null;

                var log = new byte[logLen];
                Marshal.Copy(IntPtr.Add(buf, logStart), log, 0, logLen);
                return ParseNvmeHealthLog(log);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Decodes the 512-byte NVMe SMART/Health Information log page (0x02).</summary>
    public static SmartLifetime? ParseNvmeHealthLog(ReadOnlySpan<byte> log)
    {
        if (log.Length < 168) return null;

        ulong unitsRead = BinaryPrimitives.ReadUInt64LittleEndian(log[32..40]);
        ulong unitsWritten = BinaryPrimitives.ReadUInt64LittleEndian(log[48..56]);
        if (unitsRead == 0 && unitsWritten == 0) return null;

        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..3]);
        int? temperature = kelvin is > 200 and < 500 ? kelvin - 273 : null;
        int used = log[5];
        int spare = log[3];

        return new SmartLifetime(
            SaturatingBytes(unitsWritten),
            SaturatingBytes(unitsRead),
            used <= 100 ? used : null,
            temperature,
            spare <= 100 ? spare : null,
            log[0],
            SaturatingInt64(BinaryPrimitives.ReadUInt64LittleEndian(log[128..136])),
            SaturatingInt64(BinaryPrimitives.ReadUInt64LittleEndian(log[144..152])),
            SaturatingInt64(BinaryPrimitives.ReadUInt64LittleEndian(log[160..168])));
    }

    private static long SaturatingBytes(ulong units)
        => units > (ulong)long.MaxValue / 512000UL ? long.MaxValue : (long)(units * 512000UL);

    private static long SaturatingInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    // ---------------------------------------------------------------- ATA / SATA

    // ATA_PASS_THROUGH_EX (x64) field offsets.
    private const int AtaPteSize = 56;
    private const ushort ATA_FLAGS_DATA_IN = 0x02;
    private const int SMART_DATA_LEN = 512;

    private static SmartLifetime? ReadAta(string path)
    {
        // ATA pass-through needs read/write access (and typically elevation).
        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == InvalidHandle) return null;

        try
        {
            int total = AtaPteSize + SMART_DATA_LEN;
            IntPtr buf = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt16(buf, 0, (short)AtaPteSize);          // Length
                Marshal.WriteInt16(buf, 2, (short)ATA_FLAGS_DATA_IN);   // AtaFlags
                Marshal.WriteInt32(buf, 8, SMART_DATA_LEN);             // DataTransferLength
                Marshal.WriteInt64(buf, 16, 10);                       // TimeOutValue (seconds)
                Marshal.WriteInt64(buf, 32, AtaPteSize);              // DataBufferOffset

                // CurrentTaskFile (IDE registers) at offset 48: SMART READ DATA.
                int tf = 48;
                Marshal.WriteByte(buf, tf + 0, 0xD0); // Features = SMART READ DATA
                Marshal.WriteByte(buf, tf + 1, 0x01); // SectorCount
                Marshal.WriteByte(buf, tf + 3, 0x4F); // LBA Mid (Cylinder Low)
                Marshal.WriteByte(buf, tf + 4, 0xC2); // LBA High (Cylinder High)
                Marshal.WriteByte(buf, tf + 6, 0xB0); // Command = SMART

                if (!DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH, buf, (uint)total, buf, (uint)total, out _, IntPtr.Zero))
                    return null;

                int dataStart = AtaPteSize;
                long? written = null, read = null;
                // SMART attribute table: 30 entries x 12 bytes, starting 2 bytes into the data block.
                for (int i = 0; i < 30; i++)
                {
                    int e = dataStart + 2 + i * 12;
                    int id = Marshal.ReadByte(buf, e);
                    if (id == 0) continue;
                    long raw = Marshal.ReadInt64(buf, e + 5) & 0xFFFFFFFFFFFFL; // 48-bit raw value
                    if (id == 241) written = raw * 512L;       // Total LBAs Written
                    else if (id == 242) read = raw * 512L;     // Total LBAs Read
                }

                if (written is null && read is null) return null;
                return new SmartLifetime(written ?? 0, read ?? 0, null);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }
}
