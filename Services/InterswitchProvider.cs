using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Payment_Integration_API.Models;
using Payment_Integration_API.Options;

namespace Payment_Integration_API.Services;

public class InterswitchProvider : IPaymentProvider
{
    private const string TokenCacheKey = "isw:token";

    private readonly HttpClient _httpClient;
    private readonly InterswitchOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InterswitchProvider> _logger;

    public InterswitchProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        IMemoryCache cache,
        ILogger<InterswitchProvider> logger)
    {
        _httpClient = httpClient;
        _options    = options.Value;
        _cache      = cache;
        _logger     = logger;
    }

    //  1. Initiate Payment

    public Task<PaymentResult> ChargeCustomerAsync(PaymentRequest request)
    {
        //  Validate required fields
        if (string.IsNullOrWhiteSpace(request.RedirectUrl))
            return Task.FromResult(Fail("RedirectUrl is required for Interswitch."));

        if (string.IsNullOrWhiteSpace(_options.MerchantCode) ||
            string.IsNullOrWhiteSpace(_options.PayItemId))
            return Task.FromResult(Fail("MerchantCode and PayItemId must be configured.", "configuration_error"));

        //  Build payment parameters
        // Interswitch amounts are always in KOBO (Naira × 100)
        var amountInKobo = (int)Math.Round(request.Amount * 100);
        var currency     = NormaliseCurrency(request.Currency);
        
        // Use studentId as txn_ref if provided, otherwise use request.Reference
        var txnRef = request.Metadata?.StudentId ?? request.Reference;
        var hash   = GenerateHash(txnRef, amountInKobo, request.RedirectUrl);

        //  Build the Interswitch Web Checkout URL 
        // This is the URL the customer's browser POSTs to (or is redirected to).
        // It is NOT your BaseUrl — it is the Interswitch checkout page.
        var checkoutParams = new Dictionary<string, string?>
        {
            ["merchant_code"]    = _options.MerchantCode,
            ["pay_item_id"]      = _options.PayItemId,
            ["amount"]           = amountInKobo.ToString(),
            ["currency"]         = currency,
            ["txn_ref"]          = txnRef,
            ["site_redirect_url"] = request.RedirectUrl,
            ["hash"]             = hash,
        };

        // Optional customer fields
        if (!string.IsNullOrWhiteSpace(request.CustomerName))
            checkoutParams["cust_name"]  = request.CustomerName;
        if (!string.IsNullOrWhiteSpace(request.CustomerEmail))
            checkoutParams["cust_id"]    = request.CustomerEmail;
        if (!string.IsNullOrWhiteSpace(request.CustomerPhone))
            checkoutParams["cust_mobile_no"] = request.CustomerPhone;

        // Optional school metadata
        if (request.Metadata is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Metadata.StudentId))
                checkoutParams["studentId"] = request.Metadata.StudentId;
            if (!string.IsNullOrWhiteSpace(request.Metadata.FeeType))
                checkoutParams["feeType"]   = request.Metadata.FeeType;
        }

        // The RedirectUrl returned here is the Interswitch CHECKOUT page —
        // the frontend should redirect the user (or POST a form) to this URL.
        var redirectUrl = BuildCheckoutUrl(checkoutParams);

        var rawResponse = JsonSerializer.Serialize(new
        {
            checkoutUrl  = _options.WebCheckoutUrl,
            merchantCode = _options.MerchantCode,
            payItemId    = _options.PayItemId,
            amount       = amountInKobo,
            currency,
            reference    = txnRef,
            redirectUrl,
            hash
        });

        _logger.LogInformation(
            "Interswitch payment initialised | Ref={Ref} | Amount={Amount} kobo",
            txnRef, amountInKobo);

        return Task.FromResult(new PaymentResult
        {
            Success          = true,
            Provider         = PaymentProvider.Interswitch,
            TransactionId    = txnRef,
            Status           = "initialized",
            RedirectUrl      = redirectUrl,   // send this to frontend
            Message          = "Redirect the customer to the RedirectUrl to complete payment.",
            RawResponseJson  = rawResponse
        });
    }

    //  2. Verify / Requery Transaction ─

    public async Task<PaymentVerificationResult> VerifyAsync(string reference, object amountInKobo)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return FailVerify("Reference is required.");

        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            return FailVerify("MerchantCode must be configured.");

        try
        {
            //  Get OAuth bearer token (cached) ─
            var token = await GetAccessTokenAsync();

            //  Build status query URL 
            // GET /collections/api/v1/gettransaction.json
            //     ?merchantcode=MX6072&transactionreference=<ref>
            var uri = $"{_options.GetTransactionUrl}" +
                $"?merchantcode={Uri.EscapeDataString(_options.MerchantCode)}" +
                $"&transactionreference={Uri.EscapeDataString(reference)}" +
                $"&amount={amountInKobo}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var httpResponse = await _httpClient.SendAsync(httpRequest);
            var content      = await httpResponse.Content.ReadAsStringAsync();

            _logger.LogDebug("Interswitch status response for {Ref}: {Body}", reference, content);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Interswitch status check failed | Ref={Ref} | Status={Status} | Body={Body}",
                    reference, httpResponse.StatusCode, content);

                return FailVerify($"Status check failed ({(int)httpResponse.StatusCode}).", content);
            }

            //  Parse response
            var doc          = JsonDocument.Parse(content);
            var root         = doc.RootElement;
            var responseCode = TryGetString(root, "ResponseCode")
                               ?? TryGetString(root, "responseCode")
                               ?? string.Empty;
            var description  = TryGetString(root, "ResponseDescription")
                               ?? TryGetString(root, "responseDescription")
                               ?? "unknown";
            var paymentRef   = TryGetString(root, "PaymentReference")
                               ?? TryGetString(root, "paymentReference")
                               ?? TryGetString(root, "PaymentId")
                               ?? TryGetString(root, "paymentId");
            var isSuccess    = responseCode == "00";

            var amountString = TryGetString(root, "Amount") 
                   ?? TryGetString(root, "amount");

            decimal amount = 0;

            if (!string.IsNullOrWhiteSpace(amountString) && decimal.TryParse(amountString, out var parsed))
            {
                amount = parsed / 100; // convert from kobo
            }

            _logger.LogInformation(
                "Interswitch verify | Ref={Ref} | Code={Code} | Desc={Desc}",
                reference, responseCode, description);

            return new PaymentVerificationResult
            {
                Success         = isSuccess,
                Status          = isSuccess ? "success" : description,
                TransactionId   = paymentRef,
                Message         = description,
                Amount = amount,
                RawResponseJson = content
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Interswitch VerifyAsync for Ref={Ref}", reference);
            return FailVerify(ex.Message);
        }
    }

    //  Token Acquisition (OAuth 2.0 Client Credentials) 

    private async Task<string> GetAccessTokenAsync()
    {
        // Return cached token if still valid
        if (_cache.TryGetValue(TokenCacheKey, out string? cached) &&
            !string.IsNullOrEmpty(cached))
            return cached;

        // Basic Auth = Base64(clientId:clientSecret)
        var credential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl);
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
        tokenRequest.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope",      "profile"),
        });

        var response = await _httpClient.SendAsync(tokenRequest);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Interswitch token request failed ({(int)response.StatusCode}): {body}");

        var doc         = JsonDocument.Parse(body);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()
                          ?? throw new InvalidOperationException("No access_token in Interswitch response");
        var expiresIn   = doc.RootElement.TryGetProperty("expires_in", out var exp)
                          ? exp.GetInt32() : 3600;

        // Cache with 60s early-renewal buffer
        var ttl = TimeSpan.FromSeconds(Math.Max(expiresIn - 60, 30));
        _cache.Set(TokenCacheKey, accessToken, ttl);

        _logger.LogInformation("Interswitch token refreshed, cached for {Secs}s", ttl.TotalSeconds);
        return accessToken;
    }

    //  Hash Generation

    /// <summary>
    /// Interswitch Web Checkout hash:
    /// SHA512( MerchantCode + PayItemId + TransactionRef + AmountInKobo + RedirectUrl + ClientSecret )
    /// </summary>
    private string GenerateHash(string reference, int amountInKobo, string redirectUrl)
    {
        var raw = string.Concat(
            _options.MerchantCode,
            _options.PayItemId,
            reference,
            amountInKobo.ToString(),
            redirectUrl,
            _options.ClientSecret);

        using var sha512   = SHA512.Create();
        var hashBytes      = sha512.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    //  Helpers

    /// <summary>
    /// Builds the full Interswitch checkout redirect URL with payment params
    /// as query string. The frontend redirects the user here.
    /// </summary>
    private string BuildCheckoutUrl(Dictionary<string, string?> parameters)
    {
        var queryString = string.Join("&", parameters
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value!)}"));

        return $"{_options.WebCheckoutUrl}?{queryString}";
    }

    /// <summary>
    /// Interswitch uses the ISO numeric currency code.
    /// 566 = NGN. Accepts "NGN", "566", or falls back to "566".
    /// </summary>
    private static string NormaliseCurrency(string currency) =>
        currency?.ToUpperInvariant() switch
        {
            "NGN" or "566" => "566",
            "USD" or "840" => "840",
            _              => "566"
        };

    private static PaymentResult Fail(string message, string status = "failed") =>
        new()
        {
            Success         = false,
            Provider        = PaymentProvider.Interswitch,
            Status          = status,
            Message         = message,
            RawResponseJson = "{}"
        };

    private static PaymentVerificationResult FailVerify(string message, string? raw = null) =>
        new()
        {
            Success         = false,
            Status          = "failed",
            Message         = message,
            RawResponseJson = raw ?? "{}"
        };

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
}