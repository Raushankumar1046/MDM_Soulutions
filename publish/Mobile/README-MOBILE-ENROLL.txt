=========================================================================
  CYBERSHIELD MDM - ANDROID DEVICE ENROLLMENT KIT (USB CABLE METHOD)
=========================================================================

KISI BHI NAYE ANDROID MOBILE KO ENROLL KARNE KA STEP-BY-STEP PROCESS:
-------------------------------------------------------------------------
1. Mobile me Developer Options & USB Debugging ON karein:
   - Phone Settings -> About Phone -> Software Information me jayein.
   - "Build Number" par 7 baar lagataar tap karein (Developer mode ON ho jayega).
   - Phone Settings ke main page par sabse niche "Developer Options" kholein.
   - "USB Debugging" switch ko ON karein.

2. Important Note:
   - Device Owner banne ke liye phone me pehle se koi personal Google (Gmail)
     ya Samsung account logged in nahi hona chahiye.
   - Agar phone bilkul naya ya factory reset hai, to direct step 3 par jayein.
   - Agar already in-use phone hai, to Settings -> Accounts me jakar temporary
     Google/Samsung account remove kar lein (Enrollment ke baad wapas add kar sakte hain).

3. Mobile ko USB Cable se PC/Laptop se connect karein:
   - Phone screen par ek pop-up aayega: "Allow USB Debugging?"
   - "Always allow from this computer" ko check/tick karein aur OK / Allow press karein.

4. 1-Click Enrollment Script chalayein:
   - Is folder me "Enroll-Android-Device.bat" par double click karein.
   - Script automatically:
     [1] Connected Android mobile ko auto-detect karegi.
     [2] CyberShield-MDM-Client.apk phone me install karegi.
     [3] PERMANENT DEVICE OWNER mode activate karegi (Uninstall Blocked).
     [4] Hardware IMEI Read, GPS Live Location aur System permissions set karegi.
     [5] Central Server (14.194.19.6 / 192.168.68.152) se live connect kar degi.

5. Verification:
   - Phone par "Device security sync active" background me start ho jayega.
   - Server Portal (http://14.194.19.6:5000) ke "Mobile MDM" tab me
     mobile ka real IMEI, live GPS location aur ONLINE status turant show ho jayega.

UTILITY SCRIPT:
-------------------------------------------------------------------------
- Fix-Connected-Mobile-IMEI.bat:
  Agar kabhi kisi device par permissions refresh karni ho ya status re-check karna ho,
  to cable connect karke ise run karein.
=========================================================================
