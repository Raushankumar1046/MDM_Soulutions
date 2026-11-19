package com.cybershield.mdm;

import android.content.Context;
import android.net.DhcpInfo;
import android.net.wifi.WifiManager;
import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;

/**
 * Automatically discovers the Central Server on the local Wi-Fi network
 * by broadcasting to UDP port 5002 across all local network interfaces and gateways.
 */
public class UdpDiscoveryClient {
    private static final String TAG = "CyberShield_UDP";
    private static final int UDP_PORT = 5002;
    private static final String DISCOVERY_REQUEST = "CYBERSHIELD_MDM_DISCOVER";
    private static final String ANNOUNCE_PREFIX = "CYBERSHIELD_MDM_ANNOUNCE|";
    private static final String SERVER_PREFIX_ALT = "CYBERSHIELD_SERVER|";

    public interface DiscoveryCallback {
        void onServerDiscovered(String serverHubUrl);
    }

    public static void discoverServerAsync(Context context, DiscoveryCallback callback) {
        new Thread(() -> {
            DatagramSocket socket = null;
            WifiManager.MulticastLock multicastLock = null;
            try {
                // Acquire Wi-Fi Multicast Lock so Android OS allows receiving broadcast packets
                try {
                    WifiManager wifi = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                    if (wifi != null) {
                        multicastLock = wifi.createMulticastLock("cybershield_udp_discovery");
                        multicastLock.setReferenceCounted(true);
                        multicastLock.acquire();
                    }
                } catch (Exception ignored) {}

                socket = new DatagramSocket();
                socket.setBroadcast(true);
                socket.setSoTimeout(1500); // 1.5s per receive cycle

                byte[] sendData = DISCOVERY_REQUEST.getBytes();
                List<InetAddress> targets = getBroadcastTargets(context);

                // Send discovery ping to all resolved targets
                for (InetAddress target : targets) {
                    try {
                        DatagramPacket sendPacket = new DatagramPacket(sendData, sendData.length, target, UDP_PORT);
                        socket.send(sendPacket);
                        Log.i(TAG, "Sent discovery request to: " + target.getHostAddress() + ":" + UDP_PORT);
                    } catch (Exception e) {
                        Log.w(TAG, "Failed sending to " + target + ": " + e.getMessage());
                    }
                }

                // Listen for reply (up to 4.5 seconds total)
                long deadline = System.currentTimeMillis() + 4500;
                byte[] recvBuf = new byte[1024];

                while (System.currentTimeMillis() < deadline) {
                    try {
                        DatagramPacket recvPacket = new DatagramPacket(recvBuf, recvBuf.length);
                        socket.receive(recvPacket);

                        String response = new String(recvPacket.getData(), 0, recvPacket.getLength()).trim();
                        Log.i(TAG, "Received UDP response from " + recvPacket.getAddress().getHostAddress() + ": " + response);

                        String serverUrl = null;
                        if (response.startsWith(ANNOUNCE_PREFIX)) {
                            serverUrl = response.substring(ANNOUNCE_PREFIX.length()).trim();
                        } else if (response.startsWith(SERVER_PREFIX_ALT)) {
                            serverUrl = response.substring(SERVER_PREFIX_ALT.length()).trim();
                        }

                        if (serverUrl != null && !serverUrl.isEmpty()) {
                            if (callback != null) {
                                callback.onServerDiscovered(serverUrl);
                            }
                            break;
                        }
                    } catch (java.net.SocketTimeoutException ste) {
                        // Resend ping once more in case packet was dropped
                        for (InetAddress target : targets) {
                            try {
                                DatagramPacket sendPacket = new DatagramPacket(sendData, sendData.length, target, UDP_PORT);
                                socket.send(sendPacket);
                            } catch (Exception ignored) {}
                        }
                    }
                }
            } catch (Exception e) {
                Log.d(TAG, "UDP Discovery ended: " + e.getMessage());
            } finally {
                if (socket != null && !socket.isClosed()) {
                    socket.close();
                }
                if (multicastLock != null && multicastLock.isHeld()) {
                    try { multicastLock.release(); } catch (Exception ignored) {}
                }
            }
        }, "UdpDiscoveryThread").start();
    }

    private static List<InetAddress> getBroadcastTargets(Context context) {
        List<InetAddress> targets = new ArrayList<>();
        try {
            // 1. Global broadcast fallback
            targets.add(InetAddress.getByName("255.255.255.255"));
        } catch (Exception ignored) {}

        // 2. Discover true broadcast addresses from all active network interfaces
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces.hasMoreElements()) {
                NetworkInterface networkInterface = interfaces.nextElement();
                if (networkInterface.isLoopback() || !networkInterface.isUp()) continue;

                for (InterfaceAddress interfaceAddress : networkInterface.getInterfaceAddresses()) {
                    InetAddress broadcast = interfaceAddress.getBroadcast();
                    if (broadcast != null && !targets.contains(broadcast)) {
                        targets.add(broadcast);
                        Log.d(TAG, "Found interface broadcast: " + broadcast.getHostAddress());
                    }
                }
            }
        } catch (Exception ignored) {}

        // 3. Add default gateway from DHCP
        try {
            WifiManager wifi = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifi != null) {
                DhcpInfo dhcp = wifi.getDhcpInfo();
                if (dhcp != null && dhcp.gateway != 0) {
                    byte[] gatewayQuads = new byte[4];
                    for (int k = 0; k < 4; k++) {
                        gatewayQuads[k] = (byte) ((dhcp.gateway >> k * 8) & 0xFF);
                    }
                    InetAddress gw = InetAddress.getByAddress(gatewayQuads);
                    if (!targets.contains(gw)) {
                        targets.add(gw);
                    }
                }
            }
        } catch (Exception ignored) {}

        return targets;
    }
}
