using System.Text.Json;
using EnableBankingUploader.Core.FireflyIii;
using EnableBankingUploader.Core.FireflyIii.Models;

namespace EnableBankingUploader.Tests;

[TestClass]
public class LenientDateOnlyConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new LenientDateOnlyConverter() },
    };

    // Minimal Firefly III transaction API response with an ISO datetime date (the crash case).
    private const string FireflyPayloadWithOffset =
        """{"data":[{"id":"1","attributes":{"transactions":[{"external_id":"ext-1","date":"2018-09-17T00:00:00+02:00","amount":"100.00","description":"Test"}]}}],"meta":{"pagination":{"current_page":1,"total_pages":1}}}""";

    private const string FireflyPayloadPlainDate =
        """{"data":[{"id":"1","attributes":{"transactions":[{"external_id":"ext-1","date":"2018-09-17","amount":"100.00","description":"Test"}]}}],"meta":{"pagination":{"current_page":1,"total_pages":1}}}""";

    [TestMethod]
    public void Read_IsoDateTimeWithOffset_ParsesDate()
    {
        var response = JsonSerializer.Deserialize<PaginatedResponse<Transaction>>(FireflyPayloadWithOffset, Options)!;
        var date = response.Data[0].Attributes.Transactions[0].Date;
        Assert.AreEqual(new DateOnly(2018, 9, 17), date);
    }

    [TestMethod]
    public void Read_PlainDate_ParsesDate()
    {
        var response = JsonSerializer.Deserialize<PaginatedResponse<Transaction>>(FireflyPayloadPlainDate, Options)!;
        var date = response.Data[0].Attributes.Transactions[0].Date;
        Assert.AreEqual(new DateOnly(2018, 9, 17), date);
    }

    [TestMethod]
    public void Write_ProducesPlainDateFormat()
    {
        var store = new TransactionStore(false, [
            new TransactionSplit("withdrawal", new DateOnly(2018, 9, 17), "50.00", "Test",
                "NOK", "ext-1", "Source", "Dest", null, null),
        ]);
        var json = JsonSerializer.Serialize(store, Options);
        Assert.Contains("\"2018-09-17\"", json);
    }

    [TestMethod]
    public void Read_InvalidString_ThrowsJsonException()
    {
        var json = """{"data":[{"id":"1","attributes":{"transactions":[{"external_id":"ext-1","date":"not-a-date","amount":"100.00","description":"Test"}]}}],"meta":{"pagination":{"current_page":1,"total_pages":1}}}""";
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<PaginatedResponse<Transaction>>(json, Options));
    }
}
