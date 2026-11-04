using Windows.Devices.Bluetooth;

namespace DeskTouch.DeviceShare;

// Diagnostics only: discovering BLE peripheral capability does NOT register a
// GATT HID service, pair a phone, or prove that Android accepts HID reports.
internal sealed record BluetoothHidCapability(
    string State, bool? LowEnergy, bool? Peripheral, string Detail)
{
    internal bool Candidate => State == "Candidate" &&
        LowEnergy == true && Peripheral == true;

    internal static BluetoothHidCapability Checking =>
        new("Checking", null, null, "Awaiting this PC's Bluetooth adapter check.");

    internal static bool Valid(BluetoothHidCapability? report) =>
        report != null &&
        report.State is "Checking" or "Candidate" or "Unsupported" or "NoAdapter" or "Unknown" &&
        report.Detail is { Length: <= 240 } &&
        !report.Detail.Any(char.IsControl) &&
        (report.State != "Candidate" ||
         (report.LowEnergy == true && report.Peripheral == true));
}

internal sealed record BluetoothHidDeviceStatus(
    int Id, string Name, BluetoothHidCapability Capability);

internal static class BluetoothHidSelection
{
    // A candidate is not an established Bluetooth HID connection. Choosing
    // one never grants remote input authority or begins HID advertising.
    internal static int? PreferredCandidate(
        IReadOnlyList<BluetoothHidDeviceStatus> reports, int? previous = null)
    {
        var candidates = reports.Where(r => r.Capability.Candidate)
            .OrderBy(r => r.Id).ToArray();
        if (previous is int id && candidates.Any(r => r.Id == id))
            return id;
        return candidates.Length == 0 ? null : candidates[0].Id;
    }

    // Do not roam on tiny RSSI fluctuations. Only compare measured Android
    // signal strength after the actual HID endpoints have been paired.
    internal static bool IsMateriallyStronger(
        int currentRssiDbm, int challengerRssiDbm) =>
        challengerRssiDbm >= currentRssiDbm + 12;
}

internal static class BluetoothHidDiagnostics
{
    internal static async Task<BluetoothHidCapability> CheckAsync()
    {
        try
        {
            // Native Windows runtime API: no external PowerShell, administrator
            // privileges, PnP mutations, Bluetooth pairing, or keyboard hooks.
            var adapter = await Task.Run(async () =>
                await BluetoothAdapter.GetDefaultAsync())
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (adapter == null)
                return new("NoAdapter", false, false,
                    "Windows did not return a default Bluetooth radio.");
            bool lowEnergy = adapter.IsLowEnergySupported;
            bool peripheral = adapter.IsPeripheralRoleSupported;
            return new(lowEnergy && peripheral ? "Candidate" : "Unsupported",
                lowEnergy, peripheral, lowEnergy && peripheral
                    ? "BLE peripheral role available. Android HID pairing not yet tested."
                    : "Default Bluetooth radio cannot advertise BLE HID using this Windows API.");
        }
        catch (TimeoutException)
        {
            return new("Unknown", null, null,
                "Windows Bluetooth capability query timed out.");
        }
        catch (Exception ex)
        {
            // Do not transmit hardware IDs, addresses, stack traces, or
            // driver error details across the authenticated environment.
            return new("Unknown", null, null,
                "Windows could not read the Bluetooth adapter capabilities (" +
                ex.GetType().Name + ").");
        }
    }
}
