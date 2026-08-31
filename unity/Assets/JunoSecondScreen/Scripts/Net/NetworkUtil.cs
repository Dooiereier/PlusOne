namespace JunoSecondScreen.Net
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.NetworkInformation;
    using System.Net.Sockets;

    /// <summary>
    /// Helpers for telling the player which address to open on the tablet.
    /// </summary>
    internal static class NetworkUtil
    {
        /// <summary>
        /// Lists the machine's usable IPv4 addresses, most likely candidate first.
        /// </summary>
        /// <remarks>
        /// Wireless and wired adapters are preferred over virtual ones (VPNs, Hyper-V,
        /// VirtualBox), which otherwise tend to sort first and send players to an
        /// address the tablet cannot reach.
        /// </remarks>
        public static List<string> GetLocalAddresses()
        {
            var preferred = new List<string>();
            var fallback = new List<string>();

            NetworkInterface[] adapters;
            try
            {
                adapters = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return preferred;
            }

            foreach (NetworkInterface adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                bool isPhysical = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;

                UnicastIPAddressInformationCollection addresses;
                try
                {
                    addresses = adapter.GetIPProperties().UnicastAddresses;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }
                catch (PlatformNotSupportedException)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation address in addresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address.Address))
                    {
                        continue;
                    }

                    string text = address.Address.ToString();
                    if (isPhysical)
                    {
                        preferred.Add(text);
                    }
                    else
                    {
                        fallback.Add(text);
                    }
                }
            }

            preferred.AddRange(fallback);
            return preferred;
        }
    }
}
