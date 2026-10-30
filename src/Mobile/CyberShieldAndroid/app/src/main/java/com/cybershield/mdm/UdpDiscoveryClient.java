package com.cybershield.mdm;

import android.content.Context;
import android.net.DhcpInfo;
import android.net.wifi.WifiManager;
import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;

/**
 * Automatically discovers the Central Server on the local Wi-Fi network
 * by broadcasting to UDP port 5002.
 */
public class UdpDiscoveryClient {
    private static final String TAG = "CyberShield_UDP";
    private static final int UDP_PORT = 5002;
    private static final String DISCOVERY_REQUEST = "CYBERSHIELD_DISCOVER";
    private static final String SERVER_PREFIX = "CYBERSHIELD_SERVER|";

    public interface DiscoveryCallback {
        void onServerDiscovered(String serverHubUrl);
    }

    public static void discoverServerAsync(Context context, DiscoveryCallback callback) {
        new Thread(() -> {
            DatagramSocket socket = null;
            try {
                socket = new DatagramSocket();
                socket.setBroadcast(true);
                socket.setSoTimeout(2500); // Wait up to 2.5s for reply

                byte[] sendData = DISCOVERY_REQUEST.getBytes();

                // Send to local subnet broadcast
                InetAddress broadcastAddr = getBroadcastAddress(context);
                if (broadcastAddr != null) {
                    DatagramPacket sendPacket = new DatagramPacket(sendData, sendData.length, broadcastAddr, UDP_PORT);
                    socket.send(sendPacket);
                }

                // Also send to global broadcast fallback
                DatagramPacket globalPacket = new DatagramPacket(sendData, sendData.length, InetAddress.getByName("255.255.255.255"), UDP_PORT);
                socket.send(globalPacket);

                Log.i(TAG, "Broadcasted UDP discovery packet on port " + UDP_PORT);

                // Listen for reply from server
                byte[] recvBuf = new byte[1024];
                DatagramPacket recvPacket = new DatagramPacket(recvBuf, recvBuf.length);
                socket.receive(recvPacket);

                String response = new String(recvPacket.getData(), 0, recvPacket.getLength()).trim();
                Log.i(TAG, "Received UDP response from " + recvPacket.getAddress().getHostAddress() + ": " + response);

                if (response.startsWith(SERVER_PREFIX)) {
                    String serverUrl = response.substring(SERVER_PREFIX.length()).trim();
                    if (!serverUrl.isEmpty() && callback != null) {
                        callback.onServerDiscovered(serverUrl);
                    }
                }
            } catch (Exception e) {
                Log.d(TAG, "UDP Discovery ended or timed out: " + e.getMessage());
            } finally {
                if (socket != null && !socket.isClosed()) {
                    socket.close();
                }
            }
        }).start();
    }

    private static InetAddress getBroadcastAddress(Context context) {
        try {
            WifiManager wifi = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifi == null) return null;
            DhcpInfo dhcp = wifi.getDhcpInfo();
            if (dhcp == null) return null;

            int broadcast = (dhcp.ipAddress & dhcp.netmask) | ~dhcp.netmask;
            byte[] quads = new byte[4];
            for (int k = 0; k < 4; k++) {
                quads[k] = (byte) ((broadcast >> k * 8) & 0xFF);
            }
            return InetAddress.getByAddress(quads);
        } catch (Exception e) {
            return null;
        }
    }
}
