using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record RecordingMetadata(
    DateTimeOffset? RecordedAt,
    string? PhoneNumber,
    string? ContactName,
    string? Target,
    string? Affiliation);

public static class RecordingMetadataParser
{
    public static RecordingMetadata Parse(string path, DateTimeOffset fallbackModifiedAt)
    {
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(name);
        var structured = Regex.Match(stem,
            @"^(?<prefix>.+)_(?<phone>(?:\+?82|0)[0-9\- ]{8,16})_(?<stamp>20\d{12})$");
        string? contactName = null;
        string? target = null;
        string? affiliation = null;
        string phone;
        DateTimeOffset? recorded = null;

        if (structured.Success)
        {
            phone = structured.Groups["phone"].Value;
            var parts = structured.Groups["prefix"].Value.Split('.',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3)
            {
                var hospitalIndex = Array.FindIndex(parts,
                    part => Regex.IsMatch(part, @"(?:병원|의원|의료원)(?:\([^)]*\))?$|센터$", RegexOptions.IgnoreCase));
                var yearIndex = Array.FindIndex(parts,
                    part => Regex.IsMatch(part, @"^\d{2}(?:년생)?$", RegexOptions.IgnoreCase));
                if (hospitalIndex >= 0)
                {
                    target = "병원";
                    affiliation = parts[hospitalIndex];
                    contactName = parts[hospitalIndex == 0 ? 1 : 0];
                }
                else if (yearIndex >= 0)
                {
                    target = "의사";
                    contactName = parts[0];
                    affiliation = parts[^1];
                    if (affiliation.StartsWith("일반의", StringComparison.OrdinalIgnoreCase)) affiliation = "일반의";
                }
                else contactName = parts[0];
            }
            if (DateTimeOffset.TryParseExact(structured.Groups["stamp"].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)) recorded = timestamp;
        }
        else
        {
            phone = Regex.Match(name, @"(?<!\d)(?:\+?82|0)\d{8,10}(?!\d)").Value;
            var timestamp = Regex.Match(stem, @"(?<!\d)(?<stamp>20\d{12})(?!\d)");
            if (timestamp.Success && DateTimeOffset.TryParseExact(timestamp.Groups["stamp"].Value,
                    "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                    out var parsedTimestamp)) recorded = parsedTimestamp;
            else
            {
                var date = Regex.Match(name, @"(?<y>20\d{2})[\-_\.]?(?<m>\d{2})[\-_\.]?(?<d>\d{2})");
                if (date.Success && DateTimeOffset.TryParse(
                        $"{date.Groups["y"].Value}-{date.Groups["m"].Value}-{date.Groups["d"].Value}",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) recorded = parsed;
            }
        }

        return new RecordingMetadata(recorded ?? fallbackModifiedAt,
            string.IsNullOrEmpty(phone) ? null : PhoneNumberNormalizer.Normalize(phone),
            contactName, target, affiliation);
    }
}
