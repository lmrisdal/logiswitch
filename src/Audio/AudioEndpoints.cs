using System.Runtime.InteropServices;

namespace LogiSwitch.Audio;

internal sealed record AudioDevice(string Id, string Name, EDataFlow Flow);

/// <summary>Lists active audio endpoints and changes the Windows default device.</summary>
internal static class AudioEndpoints
{
    private const int DeviceStateActive = 0x1;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;
    private static PropertyKey _friendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static List<AudioDevice> ListActive(EDataFlow flow)
    {
        var result = new List<AudioDevice>();
        var enumerator = CreateEnumerator();
        try
        {
            Check(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var collection), "EnumAudioEndpoints");
            Check(collection.GetCount(out var count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0)
                {
                    continue;
                }
                try
                {
                    if (device.GetId(out var id) == 0)
                    {
                        result.Add(new AudioDevice(id, GetFriendlyName(device) ?? id, flow));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>The current default endpoint ID for a role, or null if there is none.</summary>
    public static string? GetDefaultId(EDataFlow flow, ERole role = ERole.Console)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0)
            {
                return null;
            }
            try
            {
                return device.GetId(out var id) == 0 ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Makes the endpoint the default for all three roles (console, multimedia, communications).</summary>
    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                Check(policy.SetDefaultEndpoint(deviceId, role), $"SetDefaultEndpoint({role})");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0)
        {
            return null;
        }
        try
        {
            if (store.GetValue(ref _friendlyName, out var value) != 0)
            {
                return null;
            }
            try
            {
                return value.ValueType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantNative.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
        }
    }
}
