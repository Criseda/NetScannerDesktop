using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetScannerDesktop.Services;

/// <summary>
/// One IPv4 network attached to this machine, shown as a suggestion so
/// users rarely have to type a subnet by hand.
/// </summary>
public sealed record LocalSubnet(string Cidr, string AdapterName);

/// <summary>
/// One entry in the subnet box dropdown: a local network (labelled with its
/// adapter) or a recent scan (labelled "Recent").
/// </summary>
public sealed record SubnetSuggestion(string Cidr, string Label)
{
    // AutoSuggestBox shows ToString() in the text box after a pick.
    public override string ToString() => Cidr;
}

/// <summary>
/// Finds the machine's own subnets via the OS network interfaces.
/// Used to suggest scan targets and to pre-fill the discovery form.
/// </summary>
public static class LocalNetwork
{
    public static List<LocalSubnet> GetLocalSubnets()
    {
        var found = new List<LocalSubnet>();

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                // Skip link-local (169.254.x.x): rarely worth scanning.
                byte[] bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                int prefix = CountMaskBits(address.IPv4Mask);
                string network = NetworkAddress(address.Address, address.IPv4Mask);
                found.Add(new LocalSubnet($"{network}/{prefix}", adapter.Name));
            }
        }

        return found
            .GroupBy(s => s.Cidr)
            .Select(group => group.First())
            .OrderBy(s => s.Cidr)
            .ToList();
    }

    private static int CountMaskBits(IPAddress? mask)
    {
        if (mask is null)
        {
            return 24;
        }

        int bits = 0;
        foreach (byte octet in mask.GetAddressBytes())
        {
            bits += System.Numerics.BitOperations.PopCount(octet);
        }

        return bits;
    }

    private static string NetworkAddress(IPAddress address, IPAddress? mask)
    {
        if (mask is null)
        {
            return address.ToString();
        }

        byte[] ip = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] network = new byte[ip.Length];

        for (int i = 0; i < ip.Length; i++)
        {
            network[i] = (byte)(ip[i] & maskBytes[i]);
        }

        return new IPAddress(network).ToString();
    }
}
