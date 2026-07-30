using System.Buffers.Binary;
using DiskActivityMonitor.Core.Collection;

namespace DiskActivityMonitor.Tests;

public class SmartLifetimeReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(167)]
    public void ParseNvmeHealthLog_ShortLog_ReturnsNull(int length)
        => Assert.Null(SmartLifetimeReader.ParseNvmeHealthLog(new byte[length]));

    [Fact]
    public void ParseNvmeHealthLog_UnpopulatedLog_ReturnsNull()
        => Assert.Null(SmartLifetimeReader.ParseNvmeHealthLog(new byte[168]));

    [Fact]
    public void ParseNvmeHealthLog_DecodesAllFields()
    {
        var log = Log(unitsRead: 2, unitsWritten: 3, kelvin: 300, used: 7, spare: 95,
            warning: 3, powerHours: 9, shutdowns: 4, mediaErrors: 2);

        var value = SmartLifetimeReader.ParseNvmeHealthLog(log)!.Value;

        Assert.Equal(1_536_000, value.BytesWritten);
        Assert.Equal(1_024_000, value.BytesRead);
        Assert.Equal(7, value.PercentUsed);
        Assert.Equal(27, value.TemperatureC);
        Assert.Equal(95, value.AvailableSparePercent);
        Assert.Equal(3, value.CriticalWarning);
        Assert.Equal(9, value.PowerOnHours);
        Assert.Equal(4, value.UnsafeShutdowns);
        Assert.Equal(2, value.MediaErrors);
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(201, -72)]
    [InlineData(499, 226)]
    [InlineData(500, null)]
    public void ParseNvmeHealthLog_TemperatureBoundaries(int kelvin, int? expected)
    {
        var value = SmartLifetimeReader.ParseNvmeHealthLog(Log(kelvin: kelvin))!.Value;
        Assert.Equal(expected, value.TemperatureC);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    public void ParseNvmeHealthLog_PercentBoundaries(byte value, int? expected)
    {
        var parsed = SmartLifetimeReader.ParseNvmeHealthLog(Log(used: value, spare: value))!.Value;
        Assert.Equal(expected, parsed.PercentUsed);
        Assert.Equal(expected, parsed.AvailableSparePercent);
    }

    [Fact]
    public void ParseNvmeHealthLog_SaturatesLargeCounters()
    {
        var log = Log(unitsRead: ulong.MaxValue, unitsWritten: ulong.MaxValue,
            powerHours: ulong.MaxValue, shutdowns: ulong.MaxValue, mediaErrors: ulong.MaxValue);
        var value = SmartLifetimeReader.ParseNvmeHealthLog(log)!.Value;
        Assert.Equal(long.MaxValue, value.BytesWritten);
        Assert.Equal(long.MaxValue, value.BytesRead);
        Assert.Equal(long.MaxValue, value.PowerOnHours);
        Assert.Equal(long.MaxValue, value.UnsafeShutdowns);
        Assert.Equal(long.MaxValue, value.MediaErrors);
    }

    [Fact]
    public void Read_InvalidDriveAndLiveNvme_AreSafe()
    {
        Assert.Null(SmartLifetimeReader.Read(int.MaxValue));
        _ = SmartLifetimeReader.Read(0);
    }

    private static byte[] Log(
        ulong unitsRead = 1,
        ulong unitsWritten = 1,
        int kelvin = 300,
        byte used = 1,
        byte spare = 99,
        byte warning = 0,
        ulong powerHours = 1,
        ulong shutdowns = 1,
        ulong mediaErrors = 0)
    {
        var log = new byte[512];
        log[0] = warning;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1, 2), (ushort)kelvin);
        log[3] = spare;
        log[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(32, 8), unitsRead);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48, 8), unitsWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128, 8), powerHours);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144, 8), shutdowns);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160, 8), mediaErrors);
        return log;
    }
}
