// Models/InterswitchWebhookPayload.cs
using System.Text.Json.Serialization;

namespace Payment_Integration_API.Models;

public class InterswitchWebhookPayload
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;      // e.g. "TRANSACTION.COMPLETED"

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = string.Empty;       // transaction reference

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("data")]
    public InterswitchWebhookData? Data { get; set; }
}

public class InterswitchWebhookData
{
    [JsonPropertyName("responseCode")]
    public string ResponseCode { get; set; } = string.Empty;

    [JsonPropertyName("responseDescription")]
    public string ResponseDescription { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }                        // in kobo

    [JsonPropertyName("paymentReference")]
    public string? PaymentReference { get; set; }

    [JsonPropertyName("merchantReference")]
    public string? MerchantReference { get; set; }

    [JsonPropertyName("retrievalReferenceNumber")]
    public string? RetrievalReferenceNumber { get; set; }

    [JsonPropertyName("currencyCode")]
    public string? CurrencyCode { get; set; }

    [JsonPropertyName("cardNumber")]
    public string? CardNumber { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    // Convenience
    public bool IsApproved => ResponseCode == "200";
    public decimal AmountInNaira => Amount / 100m;
}