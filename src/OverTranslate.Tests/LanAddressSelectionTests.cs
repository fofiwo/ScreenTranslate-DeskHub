using System.Net;
using System.Net.NetworkInformation;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanAddressSelectionTests
{
    private static LanNode.LocalAddress Address(string address, bool gateway = false, bool virtualAdapter = false,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet) => new(IPAddress.Parse(address), type, gateway, virtualAdapter);

    [Fact]
    public void Ethernet_with_gateway_precedes_wsl_vmware_and_vpn()
    {
        var ordered = LanNode.OrderLocalAddresses([
            Address("172.22.224.1", virtualAdapter: true),
            Address("169.254.37.162", virtualAdapter: true),
            Address("10.8.0.2", gateway: true, virtualAdapter: true, type: NetworkInterfaceType.Ppp),
            Address("192.168.31.231", gateway: true)]);

        Assert.Equal(IPAddress.Parse("192.168.31.231"), ordered[0]);
        Assert.Equal(4, ordered.Count);
    }

    [Fact]
    public void Wifi_with_gateway_precedes_virtual_ethernet_even_with_a_gateway()
    {
        var ordered = LanNode.OrderLocalAddresses([
            Address("172.22.224.1", gateway: true, virtualAdapter: true),
            Address("192.168.31.196", gateway: true, type: NetworkInterfaceType.Wireless80211)]);

        Assert.Equal(IPAddress.Parse("192.168.31.196"), ordered[0]);
    }

    [Fact]
    public void Isolated_lan_still_precedes_virtual_adapters_and_link_local_fallbacks()
    {
        var ordered = LanNode.OrderLocalAddresses([
            Address("169.254.1.2"), Address("172.22.224.1", virtualAdapter: true), Address("192.168.5.2")]);

        Assert.Equal(new[] { "192.168.5.2", "172.22.224.1", "169.254.1.2" }, ordered.Select(a => a.ToString()));
    }

    [Fact]
    public void Filters_unsupported_addresses_deduplicates_and_keeps_virtual_fallback()
    {
        var ordered = LanNode.OrderLocalAddresses([
            Address("127.0.0.1"), Address("::1"), Address("8.8.8.8"), Address("100.66.193.92"),
            Address("172.22.224.1", virtualAdapter: true), Address("172.22.224.1", virtualAdapter: true)]);

        Assert.Equal(IPAddress.Parse("172.22.224.1"), Assert.Single(ordered));
        Assert.Empty(LanNode.OrderLocalAddresses([]));
    }

    [Theory]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", true)]
    [InlineData("VMware Network Adapter VMnet8", true)]
    [InlineData("VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("VPN Tunnel", true)]
    [InlineData("Realtek Gaming 2.5GbE Family Controller", false)]
    [InlineData("Intel(R) Wi-Fi 6E AX211", false)]
    public void Recognizes_common_virtual_adapters_without_excluding_them(string name, bool expected)
        => Assert.Equal(expected, LanNode.IsVirtualAdapter(name));
}
