using System.Text.Json;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class GasPaymentEvidenceTests
{
    private static string Evidence(bool partial = false, params (string Method, string Ledger, string Reason)[] schedules)
        => JsonSerializer.Serialize(new { data = new { account = new {
            ledgers = new[] { new { number = "main-demo", ledgerType = "MAIN" }, new { number = "other-demo", ledgerType = "ELECTRIC_JUICE" } },
            paymentSchedules = new { pageInfo = new { hasNextPage = partial }, edges = schedules.Select(s => new { node = new {
                validFrom = "2026-01-01", validTo = (string?)null, scheduleType = s.Method, ledgerNumber = s.Ledger, reason = s.Reason
            } }).ToArray() }
        } } });

    [Fact]
    public void DatedMainLedgerEvidenceIgnoresUnrelatedPaymentSchedule()
    {
        var json = Evidence(false, ("BACS_TRANSFER", "other-demo", "GENERAL_ACCOUNT_PAYMENT"), ("DIRECT_DEBIT", "main-demo", "GENERAL_ACCOUNT_PAYMENT"));
        Assert.Equal("DIRECT_DEBIT", GasPricingImporter.ResolvePaymentMethod(json));
    }

    [Fact]
    public void NonDirectDebitRequiresPositiveMainLedgerEvidence()
    {
        Assert.Equal("NON_DIRECT_DEBIT", GasPricingImporter.ResolvePaymentMethod(Evidence(false, ("BACS_TRANSFER", "main-demo", "GENERAL_ACCOUNT_PAYMENT"))));
        Assert.Null(GasPricingImporter.ResolvePaymentMethod(Evidence(false, ("BACS_TRANSFER", "other-demo", "GENERAL_ACCOUNT_PAYMENT"))));
    }

    [Theory]
    [InlineData("UNKNOWN", "GENERAL_ACCOUNT_PAYMENT")]
    [InlineData("DIRECT_DEBIT", "OTHER_REASON")]
    public void UnsupportedOrUnrelatedEvidenceCannotSelectPrice(string method, string reason)
        => Assert.Null(GasPricingImporter.ResolvePaymentMethod(Evidence(false, (method, "main-demo", reason))));

    [Fact]
    public void PartialPaginationAndConflictingSchedulesRemainUnavailable()
    {
        Assert.Null(GasPricingImporter.ResolvePaymentMethod(Evidence(true, ("DIRECT_DEBIT", "main-demo", "GENERAL_ACCOUNT_PAYMENT"))));
        Assert.Null(GasPricingImporter.ResolvePaymentMethod(Evidence(false, ("DIRECT_DEBIT", "main-demo", "GENERAL_ACCOUNT_PAYMENT"), ("BACS_TRANSFER", "main-demo", "GENERAL_ACCOUNT_PAYMENT"))));
        Assert.Null(GasPricingImporter.ResolvePaymentMethod(Evidence()));
    }
}
