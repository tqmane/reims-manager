namespace ReimsManager;

/// <summary>Consent/validation happens before this sequence. Stop on the first failure.</summary>
internal static class SettingsCommitSequence
{
    internal static async Task<bool> Apply(bool hardware, bool identity, Func<Task<bool>> saveHardware, Func<Task<bool>> saveIdentity)
    {
        if (hardware && !await saveHardware()) return false;
        if (identity && !await saveIdentity()) return false;
        return true;
    }
}
