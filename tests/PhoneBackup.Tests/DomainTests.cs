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
        var result = RecordingMetadataParser.Parse("Recordings/Call_2026-08-19_01012345678.m4a", DateTimeOffset.Now);
        Assert.Equal("+821012345678", result.PhoneNumber);
        Assert.Equal(new DateTime(2026, 8, 19), result.RecordedAt!.Value.Date);
    }

    [Theory]
    [InlineData("최정은.인사팀장.강서우리들병원_01087669640_20240604173959.m4a", "최정은", "+821087669640", 2024, 6, 4)]
    [InlineData("장재민.58.정형외과_01052733793_20240409173940.m4a", "장재민", "+821052733793", 2024, 4, 9)]
    public void RecordingParser_ExtractsSamsungStructuredName(string fileName, string contactName, string phone, int year, int month, int day)
    {
        var result = RecordingMetadataParser.Parse(fileName, DateTimeOffset.Now);
        Assert.Equal(contactName, result.ContactName);
        Assert.Equal(phone, result.PhoneNumber);
        Assert.Equal(new DateTime(year, month, day), result.RecordedAt!.Value.Date);
    }

    [Fact]
    public void RecordingParser_ExtractsHospitalTargetAndAffiliationInEitherOrder()
    {
        var requestedOrder = RecordingMetadataParser.Parse("최정은.인사팀장.강서우리들병원_01087669640_20240604173959.m4a", DateTimeOffset.Now);
        var deviceOrder = RecordingMetadataParser.Parse("창원파티마병원.주정숙.인사과장_01062545790_20240524110848.m4a", DateTimeOffset.Now);

        Assert.Equal("병원", requestedOrder.Target);
        Assert.Equal("강서우리들병원", requestedOrder.Affiliation);
        Assert.Equal("병원", deviceOrder.Target);
        Assert.Equal("주정숙", deviceOrder.ContactName);
        Assert.Equal("창원파티마병원", deviceOrder.Affiliation);
    }

    [Fact]
    public void RecordingParser_ExtractsDoctorTargetAndNormalizesGeneralDoctorAffiliation()
    {
        var doctor = RecordingMetadataParser.Parse("장재민.58.정형외과_01052733793_20240409173940.m4a", DateTimeOffset.Now);
        var generalDoctor = RecordingMetadataParser.Parse("김동욱.96년생.일반의(응급의학과)_01093739190_20240909172119.m4a", DateTimeOffset.Now);

        Assert.Equal("의사", doctor.Target);
        Assert.Equal("정형외과", doctor.Affiliation);
        Assert.Equal("의사", generalDoctor.Target);
        Assert.Equal("일반의", generalDoctor.Affiliation);
    }

    [Fact]
    public void RecordingParser_ExtractsPhoneAndTimestampWithoutContactName()
    {
        var result = RecordingMetadataParser.Parse("01027315428_20240125180703.m4a", DateTimeOffset.Now);
        Assert.Null(result.ContactName);
        Assert.Equal("+821027315428", result.PhoneNumber);
        Assert.Equal(new DateTime(2024, 1, 25), result.RecordedAt!.Value.Date);
        Assert.Equal(18, result.RecordedAt.Value.Hour);
        Assert.Equal(7, result.RecordedAt.Value.Minute);
        Assert.Null(result.Target);
        Assert.Null(result.Affiliation);
    }
}
