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
    /// <summary>Lifetime totals reported by a drive. <paramref name="PercentUsed"/> is the SMART wear indicator (0-100) when available.</summary>
    public readonly record struct SmartLifetime(long BytesWritten, long BytesRead, int? PercentUsed);

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

    /// <summary>Reads lifetime totals for <c>\\.\PhysicalDrive{driveNumber}</c>, or null if the drive does not expose them.</summary>
    public static SmartLifetime? Read(int driveNumber)
    {
        string path = $"\\\\.\\PhysicalDrive{driveNumber}";
        try
        {
            var nvme = ReadNvme(path);
            if (nvme is not null) return nvme;
            return ReadAta(path);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- NVMe

    private static SmartLifetime? ReadNvme(string path)
    {
        // The query property works without elevation; try a zero-access handle first.
        IntPtr h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == InvalidHandle)
            h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
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

                ulong duWritten = (ulong)Marshal.ReadInt64(buf, logStart + 48);
                ulong duRead = (ulong)Marshal.ReadInt64(buf, logStart + 32);
                int pctUsed = Marshal.ReadByte(buf, logStart + 5);
                if (duWritten == 0 && duRead == 0) return null; // not a populated NVMe log

                // Each NVMe data unit is 1000 * 512 = 512,000 bytes.
                long bytesWritten = checked((long)(duWritten * 512000UL));
                long bytesRead = checked((long)(duRead * 512000UL));
                return new SmartLifetime(bytesWritten, bytesRead, pctUsed is >= 0 and <= 100 ? pctUsed : null);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

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
