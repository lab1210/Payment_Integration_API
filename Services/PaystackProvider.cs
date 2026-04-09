using System.Text.Json;
using Microsoft.Extensions.Options;
using Payment_Integration_API.Models;
using Payment_Integration_API.Options;

namespace Payment_Integration_API.Services;

public class PaystackProvider : IPaymentProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;

    public PaystackProvider(HttpClient httpClient, IOptions<PaystackOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<PaymentResult> ChargeCustomerAsync(PaymentRequest request)
    {
        var payload = new
        {
            email = request.CustomerEmail,
            amount = Convert.ToInt32(request.Amount * 100), // kobo
            reference = request.Reference,
            callback_url = request.RedirectUrl,
            metadata = ConvertMetadataToDictionary(request.Metadata)
        };

        var httpResponse = await _httpClient.PostAsJsonAsync("/transaction/initialize", payload);
        var content = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
            return new PaymentResult { Success = false, Provider = PaymentProvider.Paystack, Status = "failed", Message = httpResponse.ReasonPhrase, RawResponseJson = content };

        var doc = JsonDocument.Parse(content);
        var status = doc.RootElement.GetProperty("status").GetBoolean();
        var data = doc.RootElement.GetProperty("data");
        var authorizationUrl = data.GetProperty("authorization_url").GetString();
        var transactionId = data.GetProperty("reference").GetString() ?? string.Empty;

        return new PaymentResult
        {
            Success = status,
            Provider = PaymentProvider.Paystack,
            TransactionId = transactionId,
            Status = status ? "initialized" : "failed",
            RedirectUrl = authorizationUrl,
            RawResponseJson = content
        };
    }

    public async Task<PaymentVerificationResult> VerifyAsync(string reference, object amountInKobo)
    {
        var httpResponse = await _httpClient.GetAsync($"/transaction/verify/{Uri.EscapeDataString(reference)}");
        var content = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
            return new PaymentVerificationResult { Success = false, Status = "failed", RawResponseJson = content };

        var doc = JsonDocument.Parse(content);
        var status = doc.RootElement.GetProperty("status").GetBoolean();
        var data = doc.RootElement.GetProperty("data");
        return new PaymentVerificationResult
        {
            Success = status,
            Status = data.GetProperty("status").GetString() ?? "unknown",
            TransactionId = data.GetProperty("reference").GetString(),
            RawResponseJson = content
        };
    }

    private static Dictionary<string, object>? ConvertMetadataToDictionary(SchoolPaymentMetadata? metadata)
    {
        if (metadata == null) return null;
        return new Dictionary<string, object>
        {
            ["studentId"] = metadata.StudentId,
            ["feeType"] = metadata.FeeType,
            // ["invoiceId"] = metadata.InvoiceId
        };
    }
}
