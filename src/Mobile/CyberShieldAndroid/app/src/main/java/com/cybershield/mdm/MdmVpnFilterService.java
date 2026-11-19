package com.cybershield.mdm;

import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Ultra-Reliable On-Device DNS Web & Content Filter using Android VpnService.
 * Intercepts DNS queries on port 53 and blocks corporate blacklisted domains (NXDOMAIN),
 * while forwarding allowed queries asynchronously to upstream DNS without breaking internet access.
 * 
 * Crucial Design Highlights:
 * 1. Split-Tunnel Virtual DNS: Only routes 10.120.0.2/32 into TUN. All regular HTTP/HTTPS web traffic
 *    runs natively at full hardware speed directly over physical Wi-Fi or Cellular network.
 * 2. Excludes CyberShield MDM itself from VPN via addDisallowedApplication so internal sockets
 *    (SignalR heartbeat, upstream DNS queries) never loop or get blocked.
 * 3. Does NOT invoke lockdown, preventing Android NetworkMonitor from falsely flagging the device as having no internet.
 * 4. Resolves and binds upstream sockets strictly to physical network interfaces (bypassing VPN).
 */
public class MdmVpnFilterService extends VpnService implements Runnable {
    private static final String TAG = "CyberShield_WebFilter";

    private static final Set<String> blockedDomains = Collections.synchronizedSet(new HashSet<>());
    private static final Set<String> allowedExceptions = Collections.synchronizedSet(new HashSet<>());

    private static boolean isFilterActive = false;
    private ParcelFileDescriptor vpnInterface = null;
    private Thread vpnThread = null;
    private volatile boolean isRunning = false;
    private ExecutorService dnsWorkers = null;
    private final Object outLock = new Object();

    public static void updatePolicies(List<String> blocked, List<String> allowed) {
        if (blocked != null) {
            synchronized (blockedDomains) {
                blockedDomains.clear();
                for (String d : blocked) {
                    if (d != null && !d.trim().isEmpty()) {
                        String clean = d.trim().toLowerCase();
                        if (clean.endsWith(".")) {
                            clean = clean.substring(0, clean.length() - 1);
                        }
                        blockedDomains.add(clean);
                    }
                }
            }
        }
        if (allowed != null) {
            synchronized (allowedExceptions) {
                allowedExceptions.clear();
                for (String a : allowed) {
                    if (a != null && !a.trim().isEmpty()) {
                        String clean = a.trim().toLowerCase();
                        if (clean.endsWith(".")) {
                            clean = clean.substring(0, clean.length() - 1);
                        }
                        allowedExceptions.add(clean);
                    }
                }
            }
        }
        Log.i(TAG, "WebFilter policies updated: " + blockedDomains.size() + " blocked, " + allowedExceptions.size() + " allowed");
    }

    public static boolean isRunning() {
        return isFilterActive;
    }

    public static void startFilter(Context context) {
        try {
            Intent intent = new Intent(context, MdmVpnFilterService.class);
            context.startService(intent);
        } catch (Exception e) {
            Log.e(TAG, "Failed to start WebFilter service: " + e.getMessage());
        }
    }

    public static void stopFilter(Context context) {
        try {
            Intent intent = new Intent(context, MdmVpnFilterService.class);
            context.stopService(intent);
        } catch (Exception e) {
            Log.e(TAG, "Failed to stop WebFilter service: " + e.getMessage());
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (!isRunning) {
            isRunning = true;
            dnsWorkers = Executors.newFixedThreadPool(12);
            vpnThread = new Thread(this, "MdmVpnFilterThread");
            vpnThread.start();
        }
        return START_STICKY;
    }

    private Network getPhysicalActiveNetwork(ConnectivityManager cm) {
        if (cm == null) return null;
        try {
            for (Network net : cm.getAllNetworks()) {
                NetworkCapabilities caps = cm.getNetworkCapabilities(net);
                if (caps != null && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                        && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                    return net;
                }
            }
        } catch (Exception ignored) {}
        return null;
    }

    @Override
    public void run() {
        try {
            // Check if VPN preparation is needed
            Intent prepIntent = VpnService.prepare(this);
            if (prepIntent != null) {
                Log.w(TAG, "VpnService prepare returned intent. Ensuring Device Owner always-on authorization.");
                try {
                    DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
                    ComponentName admin = new ComponentName(this, MdmDeviceAdminReceiver.class);
                    if (dpm != null && dpm.isDeviceOwnerApp(getPackageName())) {
                        dpm.setAlwaysOnVpnPackage(admin, getPackageName(), false);
                    }
                } catch (Exception e) {
                    Log.e(TAG, "Error configuring always-on VPN package: " + e.getMessage());
                }
            }

            Builder builder = new Builder();

            // 1. Assign virtual interface address: 10.120.0.1/30
            builder.addAddress("10.120.0.1", 30);

            // 2. Assign virtual DNS Server address: 10.120.0.2
            // Must be a different IP than interface address (10.120.0.1)
            // so Linux kernel routes packets destined for 10.120.0.2:53 into the TUN interface!
            builder.addDnsServer("10.120.0.2");
            builder.addRoute("10.120.0.2", 32);

            // 3. Exclude CyberShield MDM app itself from the VPN
            // Sockets opened by CyberShield (SignalR, upstream DNS, updates) bypass VPN completely.
            try {
                builder.addDisallowedApplication(getPackageName());
            } catch (Exception ignored) {}

            // 4. Set underlying network to active physical network (Wi-Fi or Cellular)
            ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
            Network physicalNet = getPhysicalActiveNetwork(cm);
            if (physicalNet != null) {
                try {
                    builder.setUnderlyingNetworks(new Network[] { physicalNet });
                } catch (Exception ignored) {}
            }

            // 5. Configure interface parameters
            builder.setSession("CyberShield DNS Filter");
            builder.setMtu(1500);
            builder.setBlocking(true);

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                builder.setMetered(false);
            }

            vpnInterface = builder.establish();
            if (vpnInterface == null) {
                Log.w(TAG, "VPN interface could not be established.");
                isFilterActive = false;
                return;
            }

            isFilterActive = true;
            Log.i(TAG, "WebFilter successfully established. DNS queries routed through local content filter.");

            // Resolve available upstream DNS servers
            List<InetAddress> upstreams = getUpstreamDnsServers();

            FileInputStream in = new FileInputStream(vpnInterface.getFileDescriptor());
            FileOutputStream out = new FileOutputStream(vpnInterface.getFileDescriptor());
            byte[] packet = new byte[32767];

            while (isRunning) {
                int length = in.read(packet);
                if (length > 0) {
                    handlePacket(packet, length, out, upstreams);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "VPN service loop ended: " + e.getMessage());
        } finally {
            isFilterActive = false;
            closeVpn();
        }
    }

    private List<InetAddress> getUpstreamDnsServers() {
        List<InetAddress> list = new ArrayList<>();

        // 1. Discover physical network DNS servers from active connectivity
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm != null) {
                Network physicalNet = getPhysicalActiveNetwork(cm);
                if (physicalNet != null) {
                    LinkProperties lp = cm.getLinkProperties(physicalNet);
                    if (lp != null) {
                        for (InetAddress dns : lp.getDnsServers()) {
                            if (dns instanceof java.net.Inet4Address && !dns.isLoopbackAddress()) {
                                String host = dns.getHostAddress();
                                if (host != null && !host.startsWith("10.120.") && !host.startsWith("127.")) {
                                    if (!list.contains(dns)) {
                                        list.add(dns);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } catch (Exception ignored) {}

        // 2. Public high-speed DNS fallbacks
        try {
            InetAddress g1 = InetAddress.getByName("8.8.8.8");
            if (!list.contains(g1)) list.add(g1);
        } catch (Exception ignored) {}
        try {
            InetAddress cf1 = InetAddress.getByName("1.1.1.1");
            if (!list.contains(cf1)) list.add(cf1);
        } catch (Exception ignored) {}
        try {
            InetAddress g2 = InetAddress.getByName("8.8.4.4");
            if (!list.contains(g2)) list.add(g2);
        } catch (Exception ignored) {}

        return list;
    }

    private void handlePacket(byte[] packet, int length, FileOutputStream out, List<InetAddress> upstreams) {
        try {
            // Check if IPv4
            int version = (packet[0] >> 4) & 0x0F;
            if (version != 4) return;

            int protocol = packet[9] & 0xFF;
            int ipHeaderLen = (packet[0] & 0x0F) * 4;

            // Only process UDP DNS (protocol 17)
            if (protocol != 17) return;
            if (length < ipHeaderLen + 8 + 12) return;

            int dstPort = ((packet[ipHeaderLen + 2] & 0xFF) << 8) | (packet[ipHeaderLen + 3] & 0xFF);

            // Only process DNS queries (destination port 53)
            if (dstPort != 53) return;

            int udpHeaderLen = 8;
            int dnsOffset = ipHeaderLen + udpHeaderLen;
            if (length <= dnsOffset) return;

            String domain = parseDnsQuestionDomain(packet, dnsOffset, length);
            if (domain != null && isDomainBlocked(domain)) {
                Log.w(TAG, ">>> BLOCKED ACCESS TO BLACKLISTED DOMAIN: " + domain);
                // Return immediate RFC-compliant NXDOMAIN response
                sendBlockedDnsResponse(packet, ipHeaderLen, length, domain, out);
                return;
            }

            // Allowed domain: dispatch asynchronously to upstream workers
            if (dnsWorkers != null && !dnsWorkers.isShutdown()) {
                byte[] packetCopy = Arrays.copyOf(packet, length);
                dnsWorkers.execute(() -> forwardDnsPacket(packetCopy, length, out, upstreams));
            }

        } catch (Exception ignored) {}
    }

    private boolean isDomainBlocked(String domain) {
        if (domain == null) return false;
        String clean = domain.toLowerCase().trim();
        if (clean.endsWith(".")) {
            clean = clean.substring(0, clean.length() - 1);
        }

        // 1. Check if explicitly allowed exception
        synchronized (allowedExceptions) {
            for (String allowed : allowedExceptions) {
                if (clean.equals(allowed) || clean.endsWith("." + allowed)) {
                    return false;
                }
            }
        }

        // 2. Check against blocked domain patterns
        synchronized (blockedDomains) {
            for (String blocked : blockedDomains) {
                if (clean.equals(blocked) || clean.endsWith("." + blocked) || clean.contains(blocked)) {
                    return true;
                }
            }
        }
        return false;
    }

    private String parseDnsQuestionDomain(byte[] data, int dnsOffset, int totalLength) {
        try {
            int qOffset = dnsOffset + 12; // Skip 12-byte DNS header
            StringBuilder sb = new StringBuilder();

            while (qOffset < totalLength) {
                int len = data[qOffset] & 0xFF;
                if (len == 0) break;
                qOffset++;

                if (sb.length() > 0) sb.append(".");
                for (int i = 0; i < len && qOffset < totalLength; i++) {
                    sb.append((char) data[qOffset++]);
                }
            }
            return sb.toString();
        } catch (Exception e) {
            return null;
        }
    }

    private void sendBlockedDnsResponse(byte[] req, int ipHeaderLen, int length, String domain, FileOutputStream out) {
        try {
            int dnsOffset = ipHeaderLen + 8;
            if (length <= dnsOffset + 12) return;

            // Find end of Question section:
            int qOffset = dnsOffset + 12;
            while (qOffset < length) {
                int len = req[qOffset] & 0xFF;
                if (len == 0) {
                    qOffset++; // skip null terminator
                    break;
                }
                qOffset += 1 + len;
            }
            qOffset += 4; // skip QTYPE (2) + QCLASS (2)
            if (qOffset > length) qOffset = length;

            int respLength = qOffset; // Cleanly truncate at end of question
            byte[] resp = Arrays.copyOf(req, respLength);

            // Update IP total length
            resp[2] = (byte) ((respLength >> 8) & 0xFF);
            resp[3] = (byte) (respLength & 0xFF);

            // Update UDP length
            int udpLen = respLength - ipHeaderLen;
            resp[ipHeaderLen + 4] = (byte) ((udpLen >> 8) & 0xFF);
            resp[ipHeaderLen + 5] = (byte) (udpLen & 0xFF);

            // DNS Header:
            // Byte 2: Set QR=1 (response), AA=1 (authoritative), preserve RD
            resp[dnsOffset + 2] = (byte) ((req[dnsOffset + 2] & 0x01) | 0x80 | 0x04);
            // Byte 3: Set RA=1 (Recursion Available), RCODE=3 (NXDOMAIN)
            resp[dnsOffset + 3] = (byte) 0x83;

            resp[dnsOffset + 4] = 0; resp[dnsOffset + 5] = 1; // QDCOUNT = 1
            resp[dnsOffset + 6] = 0; resp[dnsOffset + 7] = 0; // ANCOUNT = 0
            resp[dnsOffset + 8] = 0; resp[dnsOffset + 9] = 0; // NSCOUNT = 0
            resp[dnsOffset + 10] = 0; resp[dnsOffset + 11] = 0; // ARCOUNT = 0

            // Swap src/dst IP
            for (int i = 0; i < 4; i++) {
                byte tmp = resp[12 + i];
                resp[12 + i] = resp[16 + i];
                resp[16 + i] = tmp;
            }

            // Swap src/dst Port
            byte p1 = resp[ipHeaderLen]; byte p2 = resp[ipHeaderLen + 1];
            resp[ipHeaderLen] = resp[ipHeaderLen + 2]; resp[ipHeaderLen + 1] = resp[ipHeaderLen + 3];
            resp[ipHeaderLen + 2] = p1; resp[ipHeaderLen + 3] = p2;

            // Clear UDP checksum
            resp[ipHeaderLen + 6] = 0;
            resp[ipHeaderLen + 7] = 0;

            // Recompute IPv4 Header Checksum
            resp[10] = 0; resp[11] = 0;
            int sum = 0;
            for (int i = 0; i < ipHeaderLen; i += 2) {
                sum += ((resp[i] & 0xFF) << 8) | (resp[i + 1] & 0xFF);
            }
            while ((sum >> 16) > 0) sum = (sum & 0xFFFF) + (sum >> 16);
            int checksum = ~sum & 0xFFFF;
            resp[10] = (byte) (checksum >> 8);
            resp[11] = (byte) checksum;

            synchronized (outLock) {
                out.write(resp, 0, respLength);
            }
            Log.w(TAG, "Sent NXDOMAIN reply for blocked domain: " + domain);
        } catch (Exception e) {
            Log.e(TAG, "Error generating blocked DNS response: " + e.getMessage());
        }
    }

    private void forwardDnsPacket(byte[] packet, int length, FileOutputStream out, List<InetAddress> upstreams) {
        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        int dnsOffset = ipHeaderLen + 8;
        int dnsLength = length - dnsOffset;
        if (dnsLength <= 0) return;

        ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        Network physicalNet = getPhysicalActiveNetwork(cm);

        for (InetAddress server : upstreams) {
            DatagramSocket upstream = null;
            try {
                upstream = new DatagramSocket();
                protect(upstream); // Prevent routing loop into TUN interface

                if (physicalNet != null) {
                    try {
                        physicalNet.bindSocket(upstream);
                    } catch (Exception ignored) {}
                }

                upstream.setSoTimeout(2500);

                DatagramPacket sendPkt = new DatagramPacket(packet, dnsOffset, dnsLength, server, 53);
                upstream.send(sendPkt);

                byte[] recvBuf = new byte[2048];
                DatagramPacket recvPkt = new DatagramPacket(recvBuf, recvBuf.length);
                upstream.receive(recvPkt);

                int recvLen = recvPkt.getLength();
                int recvOffset = recvPkt.getOffset();
                if (recvLen <= 0) continue;

                // Build return IP packet back to TUN interface
                byte[] reply = new byte[ipHeaderLen + 8 + recvLen];
                System.arraycopy(packet, 0, reply, 0, ipHeaderLen + 8);
                System.arraycopy(recvBuf, recvOffset, reply, ipHeaderLen + 8, recvLen);

                // Set new total length in IP header
                reply[2] = (byte) ((reply.length >> 8) & 0xFF);
                reply[3] = (byte) (reply.length & 0xFF);

                // Set new UDP length
                int udpLen = 8 + recvLen;
                reply[ipHeaderLen + 4] = (byte) ((udpLen >> 8) & 0xFF);
                reply[ipHeaderLen + 5] = (byte) (udpLen & 0xFF);

                // Swap src/dst IP
                for (int i = 0; i < 4; i++) {
                    reply[12 + i] = packet[16 + i];
                    reply[16 + i] = packet[12 + i];
                }

                // Swap src/dst Port
                reply[ipHeaderLen] = packet[ipHeaderLen + 2];
                reply[ipHeaderLen + 1] = packet[ipHeaderLen + 3];
                reply[ipHeaderLen + 2] = packet[ipHeaderLen];
                reply[ipHeaderLen + 3] = packet[ipHeaderLen + 1];

                reply[ipHeaderLen + 6] = 0;
                reply[ipHeaderLen + 7] = 0;

                reply[10] = 0; reply[11] = 0;
                int sum = 0;
                for (int i = 0; i < ipHeaderLen; i += 2) {
                    sum += ((reply[i] & 0xFF) << 8) | (reply[i + 1] & 0xFF);
                }
                while ((sum >> 16) > 0) sum = (sum & 0xFFFF) + (sum >> 16);
                int checksum = ~sum & 0xFFFF;
                reply[10] = (byte) (checksum >> 8);
                reply[11] = (byte) checksum;

                synchronized (outLock) {
                    out.write(reply, 0, reply.length);
                }
                return; // Successfully resolved and delivered

            } catch (Exception ignored) {
                // Try next upstream resolver
            } finally {
                if (upstream != null && !upstream.isClosed()) {
                    try { upstream.close(); } catch (Exception ignored) {}
                }
            }
        }
    }

    private void closeVpn() {
        try {
            if (vpnInterface != null) {
                vpnInterface.close();
                vpnInterface = null;
            }
        } catch (Exception ignored) {}

        if (dnsWorkers != null) {
            try {
                dnsWorkers.shutdownNow();
                dnsWorkers = null;
            } catch (Exception ignored) {}
        }
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        isRunning = false;
        if (vpnThread != null) {
            vpnThread.interrupt();
            vpnThread = null;
        }
        closeVpn();
    }
}
