// Adapted from ApolloF/DLSS5-Updater (MIT). See licenses/ApolloF-NvDrs/LICENSE.
using System.Runtime.InteropServices;
using System.Text;

namespace Dlss5CompatApp;

public sealed class NvApiException(int status, string call)
    : Exception($"{call} failed: {NvDrs.Describe(status)} ({status})")
{
    public int Status { get; } = status;
    public bool NeedsAdmin => Status == NvDrs.InvalidUserPrivilege;
}

/// <summary>
/// Minimal NVIDIA driver-settings (DRS) access through nvapi64.dll, the same API NVIDIA Profile Inspector uses.
/// Function ids and struct layouts are from NVIDIA's nvapi headers (nvapi_interface.h / nvapi.h).
/// Structs are marshalled as raw buffers at their documented offsets.
/// </summary>
public sealed class NvDrs : IDisposable
{
    public const int Ok = 0;
    public const int InvalidUserPrivilege = -137;
    public const int SettingNotFound = -160;
    public const int ProfileNotFound = -163;
    public const int ExecutableNotFound = -166;
    public const int AmbiguousPath = -182;

    private const int UnicodeBytes = 4096;          // NvAPI_UnicodeString: NvU16[2048]
    private const int SettingSize = 0x3020;         // NVDRS_SETTING (12320 bytes)
    private const uint SettingVer = SettingSize | (1u << 16);
    private const int ProfileSize = 0x1014;         // NVDRS_PROFILE (4116 bytes)
    private const uint ProfileVer = ProfileSize | (1u << 16);
    private const int AppSize = 0x500C;             // NVDRS_APPLICATION_V4 (20492 bytes)
    private const uint AppVer = AppSize | (4u << 16);

    // NVDRS_SETTING offsets
    private const int OffId = 4 + UnicodeBytes;     // 4100
    private const int OffType = OffId + 4;
    private const int OffLocation = OffType + 4;
    private const int OffIsCurrentPredefined = OffLocation + 4;
    private const int OffCurrentValue = OffIsCurrentPredefined + 8 + 4100; // after isPredefinedValid + predefinedValue

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutHandleFn(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionOutFn(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindAppFn(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindProfileFn(IntPtr session, IntPtr name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProfileFn(IntPtr session, IntPtr info, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateAppFn(IntPtr session, IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProfileInfoFn(IntPtr session, IntPtr profile, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingExFn(IntPtr session, IntPtr profile, uint id, IntPtr setting, ref uint reserved);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSettingExFn(IntPtr session, IntPtr profile, IntPtr setting, uint reserved1, uint reserved2);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorMessageFn(int status, IntPtr buffer);

    private static readonly object InitGate = new();
    private static bool _initDone;
    private static QueryInterfaceFn? _qi;

    private static T Fn<T>(uint id) where T : Delegate
    {
        var ptr = _qi!(id);
        if (ptr == IntPtr.Zero) throw new EntryPointNotFoundException($"nvapi function 0x{id:X8} not available");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>True when an NVIDIA driver with nvapi64.dll is present and initialises.</summary>
    public static bool Available
    {
        get
        {
            lock (InitGate)
            {
                if (_initDone) return _qi is not null;
                _initDone = true;
                try
                {
                    if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"), out var lib)) return false;
                    if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi)) return false;
                    _qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qi);
                    if (Fn<InitFn>(0x0150E828)() != Ok) _qi = null;
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
                {
                    _qi = null;
                }
                return _qi is not null;
            }
        }
    }

    public static string Describe(int status)
    {
        if (_qi is null) return "nvapi error";
        var buf = Marshal.AllocHGlobal(64);
        try
        {
            return Fn<ErrorMessageFn>(0x6C2D048C)(status, buf) == Ok ? Marshal.PtrToStringAnsi(buf) ?? "" : "nvapi error";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private readonly IntPtr _session;

    private NvDrs(IntPtr session) => _session = session;

    /// <summary>Opens a DRS session with the current settings loaded. Throws when no NVIDIA driver is present.</summary>
    public static NvDrs Open()
    {
        if (!Available) throw new InvalidOperationException("No NVIDIA driver (nvapi64.dll) found.");
        Check(Fn<OutHandleFn>(0x0694D52E)(out var session), "CreateSession");
        var drs = new NvDrs(session);
        try { Check(Fn<HandleFn>(0x375DBD6B)(session), "LoadSettings"); }
        catch { drs.Dispose(); throw; }
        return drs;
    }

    public void Dispose() => Fn<HandleFn>(0xDAD9CFF8)(_session);

    private static void Check(int status, string call)
    {
        if (status != Ok) throw new NvApiException(status, call);
    }

    private static IntPtr Unicode(string s)
    {
        var buf = Marshal.AllocHGlobal(UnicodeBytes);
        Clear(buf, UnicodeBytes);
        var bytes = Encoding.Unicode.GetBytes(s.Length > 2047 ? s[..2047] : s);
        Marshal.Copy(bytes, 0, buf, bytes.Length);
        return buf;
    }

    private static void Clear(IntPtr buf, int size)
    {
        var i = 0;
        for (; i + 8 <= size; i += 8) Marshal.WriteInt64(buf, i, 0);
        for (; i < size; i++) Marshal.WriteByte(buf, i, 0);
    }

    public IntPtr BaseProfile()
    {
        Check(Fn<SessionOutFn>(0xDA8466A0)(_session, out var p), "GetBaseProfile");
        return p;
    }

    /// <summary>The profile the driver applies to this exe (full path first, then bare name), or null.</summary>
    public IntPtr? FindProfileForExe(string exePath)
    {
        foreach (var name in new[] { exePath, Path.GetFileName(exePath) })
        {
            var nameBuf = Unicode(name);
            var app = Marshal.AllocHGlobal(AppSize);
            try
            {
                Clear(app, AppSize);
                Marshal.WriteInt32(app, 0, (int)AppVer);
                var status = Fn<FindAppFn>(0xEEE566B2)(_session, nameBuf, out var profile, app);
                if (status == Ok) return profile;
                if (status != ExecutableNotFound && status != ProfileNotFound) Check(status, "FindApplicationByName");
            }
            finally
            {
                Marshal.FreeHGlobal(nameBuf);
                Marshal.FreeHGlobal(app);
            }
        }
        return null;
    }

    public readonly record struct ProfileDetails(string Name, uint ApplicationCount);
    public string ProfileName(IntPtr profile) => DescribeProfile(profile).Name;
    public ProfileDetails DescribeProfile(IntPtr profile)
    {
        var info = Marshal.AllocHGlobal(ProfileSize);
        try
        {
            Clear(info, ProfileSize);
            Marshal.WriteInt32(info, 0, (int)ProfileVer);
            Check(Fn<GetProfileInfoFn>(0x61CD6FD6)(_session, profile, info), "GetProfileInfo");
            return new(Marshal.PtrToStringUni(info + 4) ?? "", unchecked((uint)Marshal.ReadInt32(info, 4108)));
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>Existing profile for the exe, or a new "&lt;name&gt;" profile containing it.</summary>
    public IntPtr GetOrCreateProfileForExe(string exePath, string name)
    {
        if (FindProfileForExe(exePath) is { } existing) return existing;

        IntPtr profile;
        var nameBuf = Unicode(name);
        try
        {
            if (Fn<FindProfileFn>(0x7E4A9A0B)(_session, nameBuf, out profile) != Ok)
            {
                var info = Marshal.AllocHGlobal(ProfileSize);
                try
                {
                    Clear(info, ProfileSize);
                    Marshal.WriteInt32(info, 0, (int)ProfileVer);
                    var n = Encoding.Unicode.GetBytes(name.Length > 2047 ? name[..2047] : name);
                    Marshal.Copy(n, 0, info + 4, n.Length);
                    Check(Fn<CreateProfileFn>(0xCC176068)(_session, info, out profile), "CreateProfile");
                }
                finally { Marshal.FreeHGlobal(info); }
            }
        }
        finally { Marshal.FreeHGlobal(nameBuf); }

        var app = Marshal.AllocHGlobal(AppSize);
        try
        {
            Clear(app, AppSize);
            Marshal.WriteInt32(app, 0, (int)AppVer);
            var exe = Encoding.Unicode.GetBytes(Path.GetFileName(exePath).ToLowerInvariant());
            Marshal.Copy(exe, 0, app + 8, exe.Length); // appName follows version + isPredefined
            Check(Fn<CreateAppFn>(0x4347A9DE)(_session, profile, app), "CreateApplication");
        }
        finally { Marshal.FreeHGlobal(app); }
        return profile;
    }

/// <summary>A setting as the driver reports it for one profile.</summary>
    /// <param name="Predefined">The value is NVIDIA's shipped default for this profile, not a user value.</param>
    /// <param name="Location">0 = this profile, 1 = inherited from the global profile, 2 = base, 3 = driver default.</param>
    public readonly record struct Setting(uint Value, bool Predefined, int Location)
    {
        public bool IsUserValue => Location == 0 && !Predefined;
    }

    // Like NVIDIA Profile Inspector, get / set / delete go through nvapi's extended entry points first: the public ones
    // silently ignore hidden settings (Smooth Motion, RTX HDR, ...). The public ids are the fallback on drivers without them.
    private static IntPtr Ext(uint id) => _qi!(id);

    /// <summary>The setting as seen from this profile, or null when neither the profile nor the driver defines it.</summary>
    public Setting? Read(IntPtr profile, uint id)
    {
        var s = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Clear(s, SettingSize);
            Marshal.WriteInt32(s, 0, (int)SettingVer);
            int status;
            if (Ext(0xEA99498D) is var ext && ext != IntPtr.Zero)
            {
                uint reserved = 0;
                status = Marshal.GetDelegateForFunctionPointer<GetSettingExFn>(ext)(_session, profile, id, s, ref reserved);
            }
            else status = Fn<GetSettingFn>(0x73BF8338)(_session, profile, id, s);
            if (status == SettingNotFound) return null;
            Check(status, $"GetSetting 0x{id:X8}");
            return new Setting((uint)Marshal.ReadInt32(s, OffCurrentValue),
                Marshal.ReadInt32(s, OffIsCurrentPredefined) != 0,
                Marshal.ReadInt32(s, OffLocation));
        }
        finally { Marshal.FreeHGlobal(s); }
    }

    /// <summary>The value set in this profile by the user or an app, or null when it only has the driver default.</summary>
    public uint? Get(IntPtr profile, uint id) => Read(profile, id) is { IsUserValue: true } s ? s.Value : null;

    public void Set(IntPtr profile, uint id, uint value)
    {
        var s = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Clear(s, SettingSize);
            Marshal.WriteInt32(s, 0, (int)SettingVer);
            Marshal.WriteInt32(s, OffId, (int)id);
            Marshal.WriteInt32(s, OffType, 0);     // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(s, OffLocation, 0); // NVDRS_CURRENT_PROFILE_LOCATION
            Marshal.WriteInt32(s, OffCurrentValue, (int)value);
            var status = Ext(0x8A2CF5F5) is var ext && ext != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<SetSettingExFn>(ext)(_session, profile, s, 0, 0)
                : Fn<SetSettingFn>(0x577DD202)(_session, profile, s);
            Check(status, $"SetSetting 0x{id:X8}");
        }
        finally { Marshal.FreeHGlobal(s); }
    }

    /// <summary>Removes this profile's own value so the global / NVIDIA default applies again.</summary>
    public void Delete(IntPtr profile, uint id)
    {
        var status = Ext(0xD20D29DF) is var ext && ext != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<DeleteSettingFn>(ext)(_session, profile, id)
            : Fn<DeleteSettingFn>(0xE4A26362)(_session, profile, id);
        if (status is not (Ok or SettingNotFound)) Check(status, $"DeleteProfileSetting 0x{id:X8}");
    }

    public void Save() => Check(Fn<HandleFn>(0xFCBC7E14)(_session), "SaveSettings");
}
