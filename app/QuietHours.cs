using System.Runtime.InteropServices;

namespace WinTapeRecorder;

// Whether Do not disturb (Focus) is on. Windows publishes it only as a notification state that Settings and the shell read:
// 0 is off, anything else is on (priority only or alarms only). Read-only; null when it can't be read
static class QuietHours
{
    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope, out uint changeStamp, byte[] buffer, ref uint bufferSize);
    const ulong ActiveProfile = 0x0D83063EA3BF1C75;

    public static bool? DoNotDisturbOn()
    {
        try
        {
            var name = ActiveProfile; var buffer = new byte[16]; uint size = (uint)buffer.Length;
            if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size) != 0 || size < 4) return null;
            return BitConverter.ToInt32(buffer, 0) != 0;
        }
        catch { return null; }
    }
}
