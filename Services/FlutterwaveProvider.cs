using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payment_Integration_API.Models;
using Payment_Integration_API.Options;


namespace Payment_Integration_API.Services;

public class FlutterwaveProvider : IPaymentProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;

    public FlutterwaveProvider(HttpClient httpClient, IOptions<FlutterwaveOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<PaymentResult> ChargeCustomerAsync(PaymentRequest request)
    {
        var payload = new
        {
            tx_ref = request.Reference,
            amount = request.Amount.ToString("F2"),
            currency = request.Currency,
            redirect_url = request.RedirectUrl,
            customer = new { email = request.CustomerEmail, phone_number = request.CustomerPhone, name = request.CustomerName },
            customizations = new { title = request.Metadata?.FeeType ?? "Bill Payment", description = "Customer to company payment" },
            meta = ConvertMetadataToDictionary(request.Metadata)
        };

        var httpResponse = await _httpClient.PostAsJsonAsync("payments", payload);
        var content = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
        {
            return new PaymentResult
            {
                Success = false,
                Provider = PaymentProvider.Flutterwave,
                Status = "failed",
                Message = $"Flutterwave initialize failed: {httpResponse.StatusCode}",
                RawResponseJson = content
            };
        }

        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? "failed"
            : "failed";

        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
        {
            return new PaymentResult
            {
                Success = false,
                Provider = PaymentProvider.Flutterwave,
                Status = status,
                Message = "Flutterwave initialize failed: missing response data",
                RawResponseJson = content
            };
        }

        var transactionId = data.TryGetProperty("id", out var idProp) ? idProp.GetInt32().ToString() : string.Empty;
        var link = data.TryGetProperty("link", out var linkProp) ? linkProp.GetString() : null;

        return new PaymentResult
        {
            Success = status.Equals("success", StringComparison.OrdinalIgnoreCase),
            Provider = PaymentProvider.Flutterwave,
            TransactionId = transactionId,
            Status = status,
            RedirectUrl = link,
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

    public async Task<PaymentVerificationResult> VerifyAsync(string reference, object amountInKobo)
    {
        var httpResponse = await _httpClient.GetAsync($"transactions/verify_by_reference?tx_ref={Uri.EscapeDataString(reference)}");
        var content = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
        {
            return new PaymentVerificationResult { Success = false, Status = "failed", RawResponseJson = content };
        }

        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? "failed"
            : "failed";

        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
        {
            return new PaymentVerificationResult { Success = false, Status = status, RawResponseJson = content };
        }

        var transactionId = data.TryGetProperty("id", out var idProp) ? idProp.GetInt32().ToString() : string.Empty;
        var providerStatus = data.TryGetProperty("status", out var providerStatusProp) ? providerStatusProp.GetString() ?? "unknown" : "unknown";
        return new PaymentVerificationResult { Success = status.Equals("success", StringComparison.OrdinalIgnoreCase), Status = providerStatus, TransactionId = transactionId, RawResponseJson = content };
    }
}
