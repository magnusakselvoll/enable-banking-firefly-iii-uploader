using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.EnableBanking.Models;

public record TransactionAmount(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currency")] string Currency);

public record Transaction(
    [property: JsonPropertyName("transaction_id")] string? TransactionId,
    [property: JsonPropertyName("entry_reference")] string? EntryReference,
    [property: JsonPropertyName("transaction_amount")] TransactionAmount TransactionAmount,
    [property: JsonPropertyName("credit_debit_indicator")] string? CreditDebitIndicator,
    [property: JsonPropertyName("booking_date")] DateOnly? BookingDate,
    [property: JsonPropertyName("value_date")] DateOnly? ValueDate,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("remittance_information")] IReadOnlyList<string>? RemittanceInformation,
    [property: JsonPropertyName("creditor_name")] string? CreditorName,
    [property: JsonPropertyName("debtor_name")] string? DebtorName);
