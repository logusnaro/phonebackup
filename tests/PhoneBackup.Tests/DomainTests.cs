using PhoneBackup.Desktop.Models;
using PhoneBackup.Desktop.Services;
using Xunit;

namespace PhoneBackup.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("010-1234-5678", "+821012345678")]
    [InlineData("+82 10 1234 5678", "+821012345678")]
    [InlineData("001-82-10-1234-5678", "+821012345678")]
    public void PhoneNumberNormalizer_NormalizesKoreanNumbers(string input, string expected)
        => Assert.Equal(expected, PhoneNumberNormalizer.Normalize(input));

    [Fact]
    public void RecordingParser_PreservesOriginalNameAndExtractsDateAndPhone()
    {
        var result = BackupService.ParseRecording("Recordings/Call_2026-08-19_01012345678.m4a", 20, DateTimeOffset.Now, "AABB");
        Assert.Equal("Call_2026-08-19_01012345678.m4a", result.OriginalFileName);
        Assert.Equal("+821012345678", result.ParsedPhoneNumber);
        Assert.Equal(new DateTime(2026, 8, 19), result.RecordedAt!.Value.Date);
    }

    [Theory]
    [InlineData("최정은.인사팀장.강서우리들병원_01087669640_20240604173959.m4a", "최정은", "+821087669640", 2024, 6, 4)]
    [InlineData("장재민.58.정형외과_01052733793_20240409173940.m4a", "장재민", "+821052733793", 2024, 4, 9)]
    public void RecordingParser_ExtractsSamsungStructuredName(string fileName, string contactName, string phone, int year, int month, int day)
    {
        var result = BackupService.ParseRecording(fileName, 20, DateTimeOffset.Now, "AABB");
        Assert.Equal(contactName, result.ParsedContactName);
        Assert.Equal(phone, result.ParsedPhoneNumber);
        Assert.Equal(new DateTime(year, month, day), result.RecordedAt!.Value.Date);
    }

    [Fact]
    public void RecordingParser_ExtractsPhoneAndTimestampWithoutContactName()
    {
        var result = BackupService.ParseRecording("01027315428_20240125180703.m4a", 20, DateTimeOffset.Now, "AABB");
        Assert.Null(result.ParsedContactName);
        Assert.Equal("+821027315428", result.ParsedPhoneNumber);
        Assert.Equal(new DateTime(2024, 1, 25), result.RecordedAt!.Value.Date);
        Assert.Equal(18, result.RecordedAt.Value.Hour);
        Assert.Equal(7, result.RecordedAt.Value.Minute);
    }
}
