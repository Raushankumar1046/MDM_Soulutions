package com.cybershield.mdm;

/**
 * Direct alias subclass for MdmDeviceAdminReceiver so that both:
 * - adb shell dpm set-device-owner com.cybershield.mdm/.DeviceAdminReceiver
 * - adb shell dpm set-device-owner com.cybershield.mdm/.MdmDeviceAdminReceiver
 * are recognized by Android Enterprise DevicePolicyManager.
 */
public class DeviceAdminReceiver extends MdmDeviceAdminReceiver {
}
