using DiskActivityMonitor.Core.Collection;
using Xunit;

namespace DiskActivityMonitor.Tests;

public class ServiceHostNameResolverTests
{
    [Fact]
    public void FormatServiceHostName_SingleService_IncludesShortAndDisplayNames()
    {
        var result = ServiceHostNameResolver.FormatServiceHostName(
            "svchost",
            new[] { ("SDRSVC", "Windows Backup") });

        Assert.Equal("svchost (SDRSVC: Windows Backup)", result);
    }

    [Fact]
    public void FormatServiceHostName_MultipleServices_UsesSortedShortNames()
    {
        var result = ServiceHostNameResolver.FormatServiceHostName(
            "svchost",
            new[] { ("NlaSvc", "Network Location Awareness"), ("Dnscache", "DNS Client") });

        Assert.Equal("svchost (Dnscache, NlaSvc)", result);
    }

    [Fact]
    public void FormatServiceHostName_DeduplicatesServiceNames()
    {
        var result = ServiceHostNameResolver.FormatServiceHostName(
            "svchost",
            new[] { ("SDRSVC", "Windows Backup"), ("sdrsvc", "Windows Backup") });

        Assert.Equal("svchost (SDRSVC: Windows Backup)", result);
    }

    [Fact]
    public void FormatServiceHostName_NoServices_ReturnsOriginalName()
    {
        var result = ServiceHostNameResolver.FormatServiceHostName(
            "svchost",
            Array.Empty<(string Name, string DisplayName)>());

        Assert.Equal("svchost", result);
    }
}
