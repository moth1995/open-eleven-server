namespace TenServer.Data;

/// <summary>Validation shared by profile creation and schema backfills.</summary>
public static class PlayerNamePolicy
{
    /// <summary>Maximum profile-name length accepted by the PES2010 client input field.</summary>
    public const int MaxLength = 15;

    public static bool TryValidate(string? supplied, out string name, out string normalized)
    {
        name = supplied?.Trim() ?? "";
        normalized = "";

        if (name.Length is < 1 or > MaxLength || name.Any(char.IsControl))
            return false;

        normalized = Normalize(name);
        return true;
    }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
