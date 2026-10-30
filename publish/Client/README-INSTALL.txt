======================================================================
CYBERSHIELD MDM - ENTERPRISE CLIENT AGENT INSTALLATION GUIDE
======================================================================

HOW TO INSTALL ON CLIENT LAPTOPS:
----------------------------------------------------------------------
1. Copy this entire folder (or extract CyberShield-Windows-Client.zip) onto the client laptop:
   - EndpointManager.Client.exe
   - appsettings.json
   - install-agent.ps1
   - Install-Client.bat (or Install-Client-AutoStart.bat)

2. Right-click on "Install-Client.bat" (or Install-Client-AutoStart.bat) and choose:
   "Run as administrator" (Double-click karne par bhi script automatically Admin rights mang legi).

3. Setup complete hone par Green SUCCESS message aayega.
   Client agent automatically background me start ho jayega aur Server portal pe
   turant "Online" show karega.

4. LAPTOP RESTART CHECK:
   Ab aap laptop restart karke check kar sakte hain:
   - Laptop restart hone par Agent automatically background me start ho jayega.
   - Office Wi-Fi ho ya Mobile Hotspot, 2-5 seconds me portal pe Online dikhega.
   - Server portal se "Remote Desktop" lene par physical screen live dikhegi aur mouse/keyboard control hoga.
   - "Send Message" bhejne par active screen pe notification dialog turant appear hoga.

ALL ISSUES RESOLVED IN THIS UPDATE:
----------------------------------------------------------------------
1. LAPTOP RESTART AUTO-START:
   - Triple-Layer Persistence:
     * Layer 1: Elevated Task Scheduler (Triggers on Logon with Administrator privileges).
     * Layer 2: HKLM & HKCU Registry Run Keys.
     * Layer 3: All-Users Startup Folder (C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup).
   - Battery Mode Allowed: "-AllowStartIfOnBatteries" and "-DontStopIfGoingOnBatteries" enabled.

2. REMOTE DESKTOP (SCREEN STREAM):
   - Windows Session 0 Isolation fixed: Agent active interactive user session me run karta hai,
     jisse physical desktop surface (1920x1080 / any resolution) live 30-60 FPS capture hota hai.
   - Screen lock fallback frame added if display is asleep/locked.

3. SEND MESSAGE (NOTIFICATION):
   - WTSSendMessage API + MB_SERVICE_NOTIFICATION + msg.exe broadcast fallback implement kiya gaya hai.
   - Active user desktop session par popup message direct screen par render hota hai.

4. OFFICE WI-FI VS MOBILE HOTSPOT:
   - Dual-IP Auto-Switching: Pehle local office IP (192.168.68.13:5000) probe hoti hai (1ms response on office Wi-Fi).
   - Agar office Wi-Fi nahi hai (Mobile Hotspot / WFH), to remote public IP (14.194.19.6:5000) automatically switch ho jati hai.

HOW TO UNINSTALL:
----------------------------------------------------------------------
Right-click on "Uninstall-Client.bat" -> "Run as administrator".
Scheduled Task, Registry keys, Startup folder aur program files complete clean ho jayenge.
======================================================================
