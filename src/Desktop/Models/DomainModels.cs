namespace PhoneBackup.Desktop.Models;

public static class PhoneNumberNormalizer
{
    public static string Normalize(string input, string defaultCountryCode = "82")
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("001", StringComparison.Ordinal)) digits = digits[3..];
        else if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.StartsWith("0", StringComparison.Ordinal)) digits = defaultCountryCode + digits[1..];
        return digits.Length == 0 ? string.Empty : "+" + digits;
    }
}
