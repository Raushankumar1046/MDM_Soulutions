======================================================================
  CYBERSHIELD MDM - ENTERPRISE WINDOWS CLIENT INSTALLATION GUIDE
======================================================================

KISI BHI LAPTOP / PC ME INSTALL KARNE KA STEP-BY-STEP PROCESS:
----------------------------------------------------------------------
1. Is zip package ko client laptop par extract karein:
   - EndpointManager.Client.exe  (Main Client Agent)
   - appsettings.json            (Server Configuration: 14.194.19.6:5000)
   - Install-Client.bat          (1-Click Elevated Setup Script)
   - Uninstall-Client.bat        (1-Click Complete Clean Uninstaller)

2. Right-click on "Install-Client.bat" aur select karein:
   "Run as administrator" (Double-click karne par bhi script automatically Admin rights mang legi).

3. Setup complete hone par GREEN SUCCESS message aayega.
   Client agent automatically background me start ho jayega aur Server portal
   (http://14.194.19.6:5000) pe turant "Online" show karega.

4. LAPTOP RESTART / POWER ON-OFF CHECK:
   - Laptop restart ya power off/on hone par agent automatically logon par background me start ho jayega.
   - Office Wi-Fi, LAN, ya Mobile Hotspot — 2 se 5 seconds me server se auto-connect ho jayega.
   - Server portal se "Remote Desktop", "PowerShell", "USB Lock", "Web Filtering", aur "FileVault" instantly manage kar sakte hain.

UNINSTALLATION:
----------------------------------------------------------------------
- "Uninstall-Client.bat" par right-click karke "Run as administrator" karein.
  Sare Scheduled Tasks, Registry Keys, Program Files aur Firewall Rules
  ek sath cleanly delete ho jayenge.
======================================================================
