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
}
