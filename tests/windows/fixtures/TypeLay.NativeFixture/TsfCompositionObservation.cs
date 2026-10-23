using System.Runtime.InteropServices;

internal static partial class TsfContextAdmission
{
    // Research-only raw COM reads. Slots and native widths follow SDK msctf.h.
    // No text-service registration, profile activation or composition mutation.
    private static CompositionSnapshot ObserveComposition(object context, uint cookie)
    {
        var result = new CompositionSnapshot { Attempted = true, Thread = GetCurrentThreadId() };
        nint unknown = 0, composition = 0, enumerator = 0, view = 0;
        try
        {
            unknown = Marshal.GetIUnknownForObject(context); // Own one additional reference.
            Guid iid = new("d40c8aae-ac92-4fc7-9a11-0ee0e23aa39b");
            int hr = Marshal.QueryInterface(unknown, in iid, out nint returned);
            result.QueryHResult = hr;
            if (!TakeComOutput(hr, returned, ref composition, result))
                return result;
            // IUnknown(0..2), StartComposition(3), EnumCompositions(4), FindComposition(5).
            returned = 0;
            hr = ComRead<FindComposition>(composition, 5)(composition, cookie, 0, out returned);
            result.FindHResult = hr;
            if (!TakeComOutput(hr, returned, ref enumerator, result))
                return result;
            // IEnumITfCompositionView: Clone(3), Next(4), Reset(5), Skip(6).
            // Next writes only fetched array entries; an empty result need not
            // clear the unused slot. Never retain the preceding enum pointer.
            returned = 0;
            uint fetched = 0;
            hr = ComRead<NextComposition>(enumerator, 4)(enumerator, 1, out returned, out fetched);
            result.NextHResult = hr;
            result.Fetched = fetched;
            result.ViewPresent = returned != 0;
            if (hr == 0 && fetched == 1 && returned != 0)
            {
                view = returned;
                result.ObservedState = "present";
            }
            else if (hr == 1 && fetched == 0 && returned == 0)
                result.ObservedState = "none-in-exact-context-at-read";
            else
            {
                // Never Release a pointer whose failed/inconsistent output does
                // not establish ownership. The owned process must then fail-stop.
                result.ReferencesReleased &= returned == 0;
                result.ContractFailure |= hr >= 0 || hr == unchecked((int)0x80070057);
            }
        }
        catch (Exception error) { result.Error = error.GetType().Name; }
        finally
        {
            result.ReferencesReleased &= ReleaseComPointer(view);
            result.ReferencesReleased &= ReleaseComPointer(enumerator);
            result.ReferencesReleased &= ReleaseComPointer(composition);
            result.ReferencesReleased &= ReleaseComPointer(unknown);
            if (!result.ReferencesReleased || result.Error is not null || result.ContractFailure)
                result.ObservedState = "unknown";
        }
        return result;
    }

    private static ProfileInventory ObserveProfiles()
    {
        var result = new ProfileInventory { Attempted = true, Thread = GetCurrentThreadId() };
        nint manager = 0, enumerator = 0;
        try
        {
            // CLSID_TF_InputProcessorProfiles, IID_ITfInputProcessorProfileMgr.
            Guid clsid = new("33c53a50-f456-4884-b049-85fd643ecfed");
            Guid iid = new("71c6e74c-0f28-11d8-a82a-00065b84435c");
            int hr = CoCreateInstance(in clsid, 0, 1, in iid, out nint returned);
            result.CreateHResult = hr;
            if (!TakeComOutput(hr, returned, ref manager, result))
                return result;
            Guid category = new("34745c63-b2f0-4784-8b67-5e12c8701a31"); // GUID_TFCAT_TIP_KEYBOARD
            // ProfileMgr slots: Activate3, Deactivate4, GetProfile5, Enum6,
            // ReleaseInputProcessor7, Register8, Unregister9, GetActive10.
            hr = ComRead<GetActiveProfile>(manager, 10)(manager, in category, out InputProfile active);
            result.ActiveHResult = hr;
            if (hr == 0)
                result.Active = ProfileIdentity.From(active);
            else if (hr > 1 || hr == unchecked((int)0x80070057))
                result.ContractFailure = true;
            returned = 0;
            hr = ComRead<EnumProfiles>(manager, 6)(manager, 0, out returned);
            result.EnumHResult = hr;
            if (!TakeComOutput(hr, returned, ref enumerator, result))
                return result;
            var next = ComRead<NextProfile>(enumerator, 4);
            for (int i = 0; i < 64; i++)
            {
                hr = next(enumerator, 1, out InputProfile profile, out uint fetched);
                result.NextHResult = hr;
                result.Fetched = fetched;
                if (hr == 1 && fetched == 0)
                {
                    result.Status = "complete";
                    return result;
                }
                if (hr != 0 || fetched != 1)
                {
                    result.ContractFailure |= hr >= 0 || hr == unchecked((int)0x80070057);
                    return result;
                }
                result.Profiles.Add(ProfileIdentity.From(profile));
            }
            result.Status = "truncated"; // Never infer absence from a capped inventory.
        }
        catch (Exception error) { result.Error = error.GetType().Name; }
        finally
        {
            result.ReferencesReleased &= ReleaseComPointer(enumerator);
            result.ReferencesReleased &= ReleaseComPointer(manager);
            if (!result.ReferencesReleased || result.Error is not null || result.ContractFailure)
                result.Status = "unknown";
        }
        return result;
    }

    private static bool TakeComOutput(int hr, nint returned, ref nint owned, ReadOnlyComObservation result)
    {
        if (hr == 0 && returned != 0)
        {
            owned = returned;
            return true;
        }
        result.ReferencesReleased &= returned == 0;
        result.ContractFailure |= hr >= 0 || hr == unchecked((int)0x80070057);
        return false;
    }
    private static bool ReleaseComPointer(nint value)
    {
        if (value == 0)
            return true;
        try
        {
            Marshal.Release(value);
            return true;
        }
        catch { return false; }
    }
    private static T ComRead<T>(nint value, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * nint.Size));

    internal class ReadOnlyComObservation
    {
        public bool Attempted
        {
            get; init;
        }
        public uint Thread
        {
            get; init;
        }
        public bool ReferencesReleased { get; set; } = true;
        public bool ContractFailure
        {
            get; set;
        }
        public string? Error
        {
            get; set;
        }
    }
    internal sealed class CompositionSnapshot : ReadOnlyComObservation
    {
        // Raw local observation; KeyResult.CompositionState also requires the
        // full source/read stack to remain valid after this method returns.
        public string ObservedState { get; set; } = "unknown";
        public int? QueryHResult
        {
            get; set;
        }
        public int? FindHResult
        {
            get; set;
        }
        public int? NextHResult
        {
            get; set;
        }
        public uint Fetched
        {
            get; set;
        }
        public bool ViewPresent
        {
            get; set;
        }
    }
    internal sealed class ProfileInventory : ReadOnlyComObservation
    {
        public string Status { get; set; } = "unknown";
        public bool Complete => Status == "complete";
        public string ActiveState => ActiveHResult == 0 && Active is not null ? "observed"
            : ActiveHResult == 1 ? "not-active" : "unknown";
        public int? CreateHResult
        {
            get; set;
        }
        public int? ActiveHResult
        {
            get; set;
        }
        public int? EnumHResult
        {
            get; set;
        }
        public int? NextHResult
        {
            get; set;
        }
        public uint Fetched
        {
            get; set;
        }
        public ProfileIdentity? Active
        {
            get; set;
        }
        public List<ProfileIdentity> Profiles { get; } = [];
    }
    internal sealed record ProfileIdentity(uint Type, ushort Language, Guid Clsid, Guid Profile, Guid Category,
        long Layout, uint Flags)
    {
        public bool Active => (Flags & 1) != 0;
        public bool Enabled => (Flags & 2) != 0;
        internal static ProfileIdentity From(InputProfile profile) => new(profile.Type, profile.Language,
            profile.Clsid, profile.Profile, profile.Category, (long)profile.Layout, profile.Flags);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InputProfile
    {
        public uint Type;
        public ushort Language;
        public Guid Clsid, Profile, Category;
        public nint SubstituteLayout;
        public uint Capabilities;
        public nint Layout;
        public uint Flags;
    }
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindComposition(nint self, uint cookie, nint range, out nint enumerator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NextComposition(nint self, uint count, out nint view, out uint fetched);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetActiveProfile(nint self, in Guid category, out InputProfile profile);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumProfiles(nint self, ushort language, out nint enumerator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NextProfile(nint self, uint count, out InputProfile profile, out uint fetched);
}
