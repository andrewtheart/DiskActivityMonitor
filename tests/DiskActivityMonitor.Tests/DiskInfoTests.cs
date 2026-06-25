using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

public class DiskInfoTests
{
    [Fact]
    public void IsSsd_SsdMediaType_ReturnsTrue()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd };
        Assert.True(disk.IsSsd);
    }

    [Fact]
    public void IsSsd_ScmMediaType_ReturnsTrue()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Scm };
        Assert.True(disk.IsSsd);
    }

    [Fact]
    public void IsSsd_HddMediaType_ReturnsFalse()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Hdd };
        Assert.False(disk.IsSsd);
    }

    [Fact]
    public void IsSsd_UnknownMediaType_ReturnsFalse()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Unknown };
        Assert.False(disk.IsSsd);
    }

    [Fact]
    public void DisplayName_WithVolumesAndFriendlyName_ShowsBoth()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", FriendlyName = "Samsung 990 PRO", Volumes = "C:" };
        Assert.Equal("C:  (Samsung 990 PRO)", disk.DisplayName);
    }

    [Fact]
    public void DisplayName_NoVolumes_ShowsFriendlyName()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C:", FriendlyName = "Samsung 990 PRO", Volumes = "" };
        Assert.Equal("Samsung 990 PRO", disk.DisplayName);
    }

    [Fact]
    public void DisplayName_NoFriendlyName_NoVolumes_ShowsDiskId()
    {
        var disk = new DiskInfo { DiskId = "2", InstanceName = "2", FriendlyName = "", Volumes = "" };
        Assert.Equal("Disk 2", disk.DisplayName);
    }

    [Fact]
    public void DisplayName_WithVolumes_NoFriendlyName_ShowsVolumesAndDiskId()
    {
        var disk = new DiskInfo { DiskId = "0", InstanceName = "0 C: D:", FriendlyName = "", Volumes = "C: D:" };
        Assert.Equal("C: D:  (Disk 0)", disk.DisplayName);
    }
}
