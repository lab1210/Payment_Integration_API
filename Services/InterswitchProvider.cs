using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payment_Integration_API.Models;
using Payment_Integration_API.Options;

namespace Payment_Integration_API.Services;

public class InterswitchProvider : IPaymentProvider
{
    private readonly HttpClient _httpClient;
    private readonly InterswitchOptions _options;

    public InterswitchProvider(HttpClient httpClient, IOptions<InterswitchOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public Task<PaymentResult> ChargeCustomerAsync(PaymentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RedirectUrl))
        {
            return Task.FromResult(new PaymentResult
            {
                Success = false,
                Provider = PaymentProvider.Interswitch,
                Status = "failed",
                Message = "RedirectUrl is required for Interswitch payment initialization.",
                RawResponseJson = "{}"
            });
        }

        if (string.IsNullOrWhiteSpace(_options.MerchantCode) || string.IsNullOrWhiteSpace(_options.PayItemId))
        {
            return Task.FromResult(new PaymentResult
            {
                Success = false,
                Provider = PaymentProvider.Interswitch,
                Status = "configuration_error",
                Message = "Interswitch MerchantCode and PayItemId must be configured.",
                RawResponseJson = "{}"
            });
        }

        var amountInKobo = Convert.ToInt32(Math.Round(request.Amount * 100));
        var redirectUrl = BuildRedirectUrl(request, amountInKobo);
        var rawResponse = JsonSerializer.Serialize(new
        {
            merchantCode = _options.MerchantCode,
            payItemId = _options.PayItemId,
            amount = amountInKobo,
            currency = request.Currency,
            reference = request.Reference,
            redirectUrl,
            hash = GenerateHash(request.Reference, amountInKobo, request.RedirectUrl)
        });

        return Task.FromResult(new PaymentResult
        {
            Success = true,
            Provider = PaymentProvider.Interswitch,
            TransactionId = request.Reference,
            Status = "initialized",
            RedirectUrl = redirectUrl,
            Message = "Interswitch payment initialization completed.",
            RawResponseJson = rawResponse
        });
    }

    public async Task<PaymentVerificationResult> VerifyAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new PaymentVerificationResult
            {
                Success = false,
                Status = "failed",
                RawResponseJson = "{ \"message\": \"Reference is required.\" }"
            };
        }

        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
        {
            return new PaymentVerificationResult
            {
                Success = false,
                Status = "failed",
                Message = "Interswitch MerchantCode must be configured.",
                RawResponseJson = "{}"
            };
        }

        try
        {
            var statusUri = BuildStatusUri(reference);
            var httpResponse = await _httpClient.GetAsync(statusUri);
            var content = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                return new PaymentVerificationResult
                {
                    Success = false,
                    Status = "failed",
                    RawResponseJson = content
                };
            }

            var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var responseCode = TryGetString(root, "ResponseCode") ?? TryGetString(root, "responseCode");
            var paymentId = TryGetString(root, "PaymentId") ?? TryGetString(root, "paymentId") ?? TryGetString(root, "paymentReference");
            var description = TryGetString(root, "ResponseDescription") ?? TryGetString(root, "responseDescription") ?? "unknown";
            var isSuccess = responseCode == "00";

            return new PaymentVerificationResult
            {
                Success = isSuccess,
                Status = isSuccess ? "success" : description,
                TransactionId = paymentId,
                RawResponseJson = content
            };
        }
        catch (Exception ex)
        {
            return new PaymentVerificationResult
            {
                Success = false,
                Status = "failed",
                Message = ex.Message,
                RawResponseJson = "{}"
            };
        }
    }

    private Uri BuildStatusUri(string reference)
    {
        var host = GetHostUri();
        var builder = new UriBuilder(host)
        {
            Path = "/collections/api/v1/gettransaction"
        };

        var query = new Dictionary<string, string>
        {
            ["merchantcode"] = _options.MerchantCode,
            ["transactionreference"] = reference
        };

        builder.Query = string.Join("&", query.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        return builder.Uri;
    }

    private string BuildRedirectUrl(PaymentRequest request, int amountInKobo)
    {
#pragma warning disable CS8604 // Possible null reference argument.
        var query = new Dictionary<string, string?>
        {
            ["merchantCode"] = _options.MerchantCode,
            ["merchant_code"] = _options.MerchantCode,
            ["payItemId"] = _options.PayItemId,
            ["pay_item_id"] = _options.PayItemId,
            ["amount"] = amountInKobo.ToString(),
            ["currency"] = request.Currency,
            ["txn_ref"] = request.Reference,
            ["transactionReference"] = request.Reference,
            ["redirectUrl"] = request.RedirectUrl,
            ["siteRedirectUrl"] = request.RedirectUrl,
            ["customerName"] = request.CustomerName,
            ["customer_email"] = request.CustomerEmail,
            ["customerPhone"] = request.CustomerPhone,
            ["hash"] = GenerateHash(request.Reference, amountInKobo, request.RedirectUrl!)
        };
#pragma warning restore CS8604 // Possible null reference argument.

        var queryString = string.Join("&", query
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value!)}"));

        return _options.BaseUrl.TrimEnd('/') + "/?" + queryString;
    }

    private string GenerateHash(string transactionReference, int amount, string redirectUrl)
    {
        var hashSource = string.Concat(
            _options.MerchantCode,
            _options.PayItemId,
            transactionReference,
            amount.ToString(),
            redirectUrl,
            _options.ClientSecret);

        using var sha512 = SHA512.Create();
        var hashBytes = sha512.ComputeHash(Encoding.UTF8.GetBytes(hashSource));
        return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private Uri GetHostUri()
    {
        var baseUri = new Uri(_options.BaseUrl);
        return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port).Uri;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }
}
