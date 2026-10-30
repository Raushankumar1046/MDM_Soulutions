package com.cybershield.mdm;

import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * On-Device DNS Web Filter using Android VpnService.
 * Intercepts DNS queries on port 53 and blocks corporate blacklisted domains.
 */
public class MdmVpnFilterService extends VpnService implements Runnable {
    private static final String TAG = "CyberShield_WebFilter";

    private static final Set<String> blockedDomains = Collections.synchronizedSet(new HashSet<>());
    private static final Set<String> allowedExceptions = Collections.synchronizedSet(new HashSet<>());

    private static boolean isFilterActive = false;
    private ParcelFileDescriptor vpnInterface = null;
    private Thread vpnThread = null;
    private boolean isRunning = false;

    public static void updatePolicies(List<String> blocked, List<String> allowed) {
        if (blocked != null) {
            synchronized (blockedDomains) {
                blockedDomains.clear();
                for (String d : blocked) {
                    if (d != null && !d.trim().isEmpty()) {
                        blockedDomains.add(d.trim().toLowerCase());
                    }
                }
            }
        }
        if (allowed != null) {
            synchronized (allowedExceptions) {
                allowedExceptions.clear();
                for (String a : allowed) {
                    if (a != null && !a.trim().isEmpty()) {
                        allowedExceptions.add(a.trim().toLowerCase());
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
        Intent intent = new Intent(context, MdmVpnFilterService.class);
        context.startService(intent);
    }

    public static void stopFilter(Context context) {
        Intent intent = new Intent(context, MdmVpnFilterService.class);
        context.stopService(intent);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (!isRunning) {
            isRunning = true;
            vpnThread = new Thread(this, "MdmVpnFilterThread");
            vpnThread.start();
        }
        return START_STICKY;
    }

    @Override
    public void run() {
        try {
            Builder builder = new Builder();
            builder.addAddress("10.120.0.1", 32);
            builder.addDnsServer("10.120.0.1");
            builder.addRoute("10.120.0.1", 32);

            // Intercept common public DNS queries as well so apps cannot bypass local filter
            builder.addRoute("8.8.8.8", 32);
            builder.addRoute("8.8.4.4", 32);
            builder.addRoute("1.1.1.1", 32);
            builder.addRoute("1.0.0.1", 32);
            builder.addRoute("9.9.9.9", 32);

            builder.setSession("CyberShield DNS Filter");
            builder.setMtu(1500);
            builder.setBlocking(true);

            vpnInterface = builder.establish();
            if (vpnInterface == null) {
                Log.w(TAG, "VPN permission not yet granted or interface could not be established.");
                isFilterActive = false;
                return;
            }

            isFilterActive = true;
            Log.i(TAG, "WebFilter active. Intercepting DNS traffic for corporate compliance.");

            FileInputStream in = new FileInputStream(vpnInterface.getFileDescriptor());
            FileOutputStream out = new FileOutputStream(vpnInterface.getFileDescriptor());
            byte[] packet = new byte[32767];

            while (isRunning) {
                int length = in.read(packet);
                if (length > 0) {
                    handlePacket(packet, length, out);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "VPN service loop ended: " + e.getMessage());
        } finally {
            isFilterActive = false;
            closeVpn();
        }
    }

    private void handlePacket(byte[] packet, int length, FileOutputStream out) {
        try {
            // Check if IPv4 (version 4)
            int version = (packet[0] >> 4) & 0x0F;
            if (version != 4) return;

            int protocol = packet[9] & 0xFF;
            if (protocol != 17) return; // UDP is protocol 17

            int ipHeaderLen = (packet[0] & 0x0F) * 4;
            int srcPort = ((packet[ipHeaderLen] & 0xFF) << 8) | (packet[ipHeaderLen + 1] & 0xFF);
            int dstPort = ((packet[ipHeaderLen + 2] & 0xFF) << 8) | (packet[ipHeaderLen + 3] & 0xFF);

            if (dstPort == 53) {
                // DNS Query
                int udpHeaderLen = 8;
                int dnsOffset = ipHeaderLen + udpHeaderLen;
                if (length > dnsOffset) {
                    String domain = parseDnsQuestionDomain(packet, dnsOffset, length);
                    if (domain != null) {
                        boolean isBlocked = isDomainBlocked(domain);
                        if (isBlocked) {
                            Log.w(TAG, "BLOCKED ACCESS TO BLACKLISTED DOMAIN: " + domain);
                            // Synthesize valid RFC DNS NXDOMAIN response
                            sendBlockedDnsResponse(packet, ipHeaderLen, length, domain, out);
                            return;
                        }
                    }
                }
            }

            // Forward non-blocked packet via standard upstream
            forwardDnsPacket(packet, length, out);

        } catch (Exception ignored) {}
    }

    private boolean isDomainBlocked(String domain) {
        if (domain == null) return false;
        String clean = domain.toLowerCase().trim();

        // Check if allowed exception
        for (String allowed : allowedExceptions) {
            if (clean.equals(allowed) || clean.endsWith("." + allowed)) {
                return false;
            }
        }

        // Check against blocked list
        for (String blocked : blockedDomains) {
            if (clean.equals(blocked) || clean.endsWith("." + blocked)) {
                return true;
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
            byte[] resp = req.clone();
            int dnsOffset = ipHeaderLen + 8;
            if (length <= dnsOffset + 12) return;

            // DNS Header: Set QR=1 (response), AA=1, RA=1, RCODE=3 (NXDOMAIN)
            resp[dnsOffset + 2] = (byte) 0x85;
            resp[dnsOffset + 3] = (byte) 0x83; // NXDOMAIN
            resp[dnsOffset + 6] = 0; // ANCOUNT = 0
            resp[dnsOffset + 7] = 0;
            resp[dnsOffset + 8] = 0; // NSCOUNT = 0
            resp[dnsOffset + 9] = 0;
            resp[dnsOffset + 10] = 0; // ARCOUNT = 0
            resp[dnsOffset + 11] = 0;

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
            resp[10] = 0;
            resp[11] = 0;
            int sum = 0;
            for (int i = 0; i < ipHeaderLen; i += 2) {
                sum += ((resp[i] & 0xFF) << 8) | (resp[i + 1] & 0xFF);
            }
            while ((sum >> 16) > 0) sum = (sum & 0xFFFF) + (sum >> 16);
            int checksum = ~sum & 0xFFFF;
            resp[10] = (byte) (checksum >> 8);
            resp[11] = (byte) checksum;

            out.write(resp, 0, length);
            Log.w(TAG, "Sent NXDOMAIN reply for blocked domain: " + domain);
        } catch (Exception e) {
            Log.e(TAG, "Error generating blocked DNS response: " + e.getMessage());
        }
    }

    private void forwardDnsPacket(byte[] packet, int length, FileOutputStream out) {
        DatagramSocket upstream = null;
        try {
            upstream = new DatagramSocket();
            protect(upstream); // Prevent routing loop into TUN interface
            upstream.setSoTimeout(2000);

            int ipHeaderLen = (packet[0] & 0x0F) * 4;
            int dnsOffset = ipHeaderLen + 8;
            int dnsLength = length - dnsOffset;
            if (dnsLength <= 0) return;

            DatagramPacket sendPkt = new DatagramPacket(packet, dnsOffset, dnsLength, InetAddress.getByName("1.1.1.1"), 53);
            upstream.send(sendPkt);

            byte[] recvBuf = new byte[2048];
            DatagramPacket recvPkt = new DatagramPacket(recvBuf, recvBuf.length);
            upstream.receive(recvPkt);

            // Return response back to TUN interface
            byte[] reply = new byte[ipHeaderLen + 8 + recvPkt.getLength()];
            System.arraycopy(packet, 0, reply, 0, ipHeaderLen + 8);
            System.arraycopy(recvPkt.getData(), 0, reply, ipHeaderLen + 8, recvPkt.getLength());

            // Set new total length in IP header
            reply[2] = (byte) (reply.length >> 8);
            reply[3] = (byte) (reply.length & 0xFF);

            // Set new UDP length
            int udpLen = 8 + recvPkt.getLength();
            reply[ipHeaderLen + 4] = (byte) (udpLen >> 8);
            reply[ipHeaderLen + 5] = (byte) (udpLen & 0xFF);

            // Swap src/dst IP
            for (int i = 0; i < 4; i++) {
                byte tmp = reply[12 + i];
                reply[12 + i] = reply[16 + i];
                reply[16 + i] = tmp;
            }

            // Swap src/dst Port
            byte p1 = reply[ipHeaderLen]; byte p2 = reply[ipHeaderLen + 1];
            reply[ipHeaderLen] = reply[ipHeaderLen + 2]; reply[ipHeaderLen + 1] = reply[ipHeaderLen + 3];
            reply[ipHeaderLen + 2] = p1; reply[ipHeaderLen + 3] = p2;

            // Clear UDP checksum
            reply[ipHeaderLen + 6] = 0;
            reply[ipHeaderLen + 7] = 0;

            // Recompute IPv4 Header Checksum
            reply[10] = 0; reply[11] = 0;
            int sum = 0;
            for (int i = 0; i < ipHeaderLen; i += 2) {
                sum += ((reply[i] & 0xFF) << 8) | (reply[i + 1] & 0xFF);
            }
            while ((sum >> 16) > 0) sum = (sum & 0xFFFF) + (sum >> 16);
            int checksum = ~sum & 0xFFFF;
            reply[10] = (byte) (checksum >> 8);
            reply[11] = (byte) checksum;

            out.write(reply, 0, reply.length);
        } catch (Exception ignored) {
        } finally {
            if (upstream != null && !upstream.isClosed()) {
                upstream.close();
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
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        isRunning = false;
        closeVpn();
    }
}
