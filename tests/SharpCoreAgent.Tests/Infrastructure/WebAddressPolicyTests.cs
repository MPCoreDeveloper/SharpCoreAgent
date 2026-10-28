using System.Net;
using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The address rule is the whole safety story of <c>fetch_url</c>, so every category it refuses is asserted here, one
/// row per address: the suite proves the rule without a socket and without a page.
/// </summary>
public sealed class WebAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.1.2.3", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("10.1.2.3", "private")]
    [InlineData("172.16.0.1", "private")]
    [InlineData("172.31.255.255", "private")]
    [InlineData("192.168.1.1", "private")]
    [InlineData("169.254.1.1", "link-local")]
    [InlineData("169.254.169.254", "cloud metadata")]
    [InlineData("fd00:ec2::254", "cloud metadata")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("fc00::1", "unique-local")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("::", "unspecified")]
    [InlineData("100.64.0.1", "carrier-grade NAT")]
    [InlineData("100.127.255.254", "carrier-grade NAT")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("255.255.255.255", "reserved")]
    [InlineData("240.0.0.1", "reserved")]
    public void RefusesTheAddressesThatAreNotThePublicInternet(string address, string category) =>
        Assert.Equal(category, WebAddressPolicy.Classify(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AllowsAPublicAddress(string address) =>
        Assert.Null(WebAddressPolicy.Classify(IPAddress.Parse(address)));

    [Fact]
    public void AnAddressIsExemptWhenTheHostIsNamed()
    {
        var loopback = new[] { IPAddress.Loopback };

        Assert.True(WebAddressPolicy.IsExempt("localhost", 8080, loopback, ["localhost"]));
        Assert.True(WebAddressPolicy.IsExempt("localhost", 8080, loopback, ["LOCALHOST"]));
        Assert.False(WebAddressPolicy.IsExempt("localhost", 8080, loopback, []));
        Assert.False(WebAddressPolicy.IsExempt("localhost", 8080, loopback, ["example.com"]));
    }

    [Fact]
    public void AnAddressIsExemptWhenTheAddressIsNamed()
    {
        var loopback = new[] { IPAddress.Parse("127.0.0.5") };

        Assert.True(WebAddressPolicy.IsExempt("api.internal", 80, loopback, ["127.0.0.5"]));
        Assert.False(WebAddressPolicy.IsExempt("api.internal", 80, loopback, ["127.0.0.6"]));
    }

    [Fact]
    public void AHostAndPortEntryCoversThatPortOnly()
    {
        var loopback = new[] { IPAddress.Loopback };

        Assert.True(WebAddressPolicy.IsExempt("localhost", 5000, loopback, ["localhost:5000"]));
        Assert.False(WebAddressPolicy.IsExempt("localhost", 5001, loopback, ["localhost:5000"]));
        Assert.True(WebAddressPolicy.IsExempt("localhost", 5000, loopback, ["[::1]:5000", "localhost:5000"]));
    }

    [Fact]
    public void ThereAreNoWildcardsAndNoSuffixMatching()
    {
        var privateAddress = new[] { IPAddress.Parse("10.0.0.5") };

        Assert.False(WebAddressPolicy.IsExempt("api.corp", 443, privateAddress, ["*.corp"]));
        Assert.False(WebAddressPolicy.IsExempt("api.corp", 443, privateAddress, ["corp"]));
    }
}
