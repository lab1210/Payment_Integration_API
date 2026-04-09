using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Payment_Integration_API.Data;
using Payment_Integration_API.Entities;
using Payment_Integration_API.Models;
using Payment_Integration_API.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Payment_Integration_API.Services;

public class PaymentService
{
    private readonly IPaymentProviderFactory _providerFactory;
    private readonly PaymentDbContext _dbContext;
    private readonly FlutterwaveOptions _flutterwaveOptions;
    private readonly PaystackOptions _paystackOptions;
    private readonly InterswitchOptions _interswitchOptions;

    public PaymentService(
        IPaymentProviderFactory providerFactory,
        PaymentDbContext dbContext,
        IOptions<FlutterwaveOptions> flutterwaveOptions,
        IOptions<PaystackOptions> paystackOptions,
        IOptions<InterswitchOptions> interswitchOptions)
    {
        _providerFactory    = providerFactory;
        _dbContext          = dbContext;
        _flutterwaveOptions = flutterwaveOptions.Value;
        _paystackOptions    = paystackOptions.Value;
        _interswitchOptions = interswitchOptions.Value;
    }

    // ── Initiate ─────────────────────────────────────────────────────────────

    public async Task<PaymentResult> InitiateAsync(PaymentRequest request)
    {
        var provider = _providerFactory.GetProvider(request.Provider);
        var result   = await provider.ChargeCustomerAsync(request);

        var transaction = new PaymentTransaction
        {
            Id              = Guid.NewGuid(),
            Provider        = request.Provider,
            Reference       = request.Reference,
            TransactionId   = result.TransactionId,
            Amount          = request.Amount,
            Currency        = request.Currency,
            CustomerEmail   = request.CustomerEmail,
            Status          = result.Status,
            IsSuccess       = result.Success,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
            RawRequestJson  = JsonSerializer.Serialize(request),
            RawResponseJson = result.RawResponseJson
        };

        _dbContext.PaymentTransactions.Add(transaction);
        await _dbContext.SaveChangesAsync();

        return result;
    }

    // ── Verify ───────────────────────────────────────────────────────────────

    public async Task<PaymentVerificationResult> VerifyAsync(PaymentProvider provider, string reference)
    {
        // Fetch existing transaction to get the amount
        var existing = await _dbContext.PaymentTransactions
            .FirstOrDefaultAsync(x => x.Reference == reference);

        if (existing == null)
            return new PaymentVerificationResult { Success = false, Status = "not_found" };

        // Convert amount to kobo for provider verification
        var amountInKobo = (int)Math.Round(existing.Amount * 100);

        var paymentProvider = _providerFactory.GetProvider(provider);
        var result          = await paymentProvider.VerifyAsync(reference, amountInKobo);

        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (result.Amount > 0)
                existing.Amount = result.Amount;
            existing.Status        = result.Status;
            existing.IsSuccess     = result.Success;
            existing.TransactionId = result.TransactionId ?? existing.TransactionId;
            existing.UpdatedAt     = DateTime.UtcNow;
            existing.RawResponseJson = result.RawResponseJson;
            await _dbContext.SaveChangesAsync();
        }

        return result;
    }

    // ── Status ───────────────────────────────────────────────────────────────

    public async Task<PaymentStatusResult> GetStatusAsync(string reference, bool refresh = false)
    {
        var transaction = await _dbContext.PaymentTransactions
            .FirstOrDefaultAsync(x => x.Reference == reference);

        if (transaction == null)
            return new PaymentStatusResult { Reference = reference, Status = "not_found" };

        if (refresh || IsPendingStatus(transaction.Status))
        {
            var verification = await VerifyAsync(transaction.Provider, reference);
            if (verification.Success || !string.IsNullOrEmpty(verification.Status))
            {
                transaction.Status        = verification.Status;
                transaction.IsSuccess     = verification.Success;
                transaction.TransactionId = verification.TransactionId ?? transaction.TransactionId;
                transaction.UpdatedAt     = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }
        }

        return new PaymentStatusResult
        {
            Reference     = transaction.Reference,
            Provider      = transaction.Provider,
            Status        = transaction.Status,
            IsSuccess     = transaction.IsSuccess,
            CreatedAt     = transaction.CreatedAt,
            UpdatedAt     = transaction.UpdatedAt,
            TransactionId = transaction.TransactionId
        };
    }

    // ── Webhook ──────────────────────────────────────────────────────────────

    public async Task<bool> ProcessWebhookAsync(PaymentProvider provider, HttpRequest request)
    {
        try
        {
            request.EnableBuffering();

            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Position = 0;

            // Step 1: Verify webhook signature
            if (!VerifyWebhookSignature(provider, body, request.Headers))
                return false;

            var payload = JsonDocument.Parse(body).RootElement;

            string reference;

            // Step 2: Extract reference from webhook
            switch (provider)
            {
                case PaymentProvider.Paystack:
                    reference = payload.GetProperty("data").GetProperty("reference").GetString() ?? "";
                    break;

                case PaymentProvider.Flutterwave:
                    reference = payload.GetProperty("data").GetProperty("tx_ref").GetString() ?? "";
                    break;

                case PaymentProvider.Interswitch:
                    reference = TryGetString(payload, "uuid") ?? "";
                    break;

                default:
                    return false;
            }

            if (string.IsNullOrWhiteSpace(reference))
                return false;

            // Step 3: Match reference with DB
            var transaction = await _dbContext.PaymentTransactions
                .FirstOrDefaultAsync(x => x.Reference == reference);

            if (transaction == null)
            {
                // Unknown reference and may be possible fraud
                return false;
            }

            // Step 4: Idempotency (avoid double processing)
            if (transaction.IsSuccess)
                return true;

            // Step 5: VERIFY WITH PROVIDER (CRITICAL)
            var verification = await VerifyAsync(provider, reference);

            // Step 6: Ensure provider ALSO confirms this reference
            if (!verification.Success)
            {
                transaction.Status    = "failed";
                transaction.IsSuccess = false;
                transaction.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                return true;
            }

            // Step 7: Ensure reference consistency
            if (!string.Equals(transaction.Reference, reference, StringComparison.Ordinal))
            {
                return false; // mismatch → reject
            }

            // Step 8: Validate amount (ANTI-FRAUD)
            if (verification.Amount != transaction.Amount)
            {
                return false;
            }

            // Step 9: Update DB ONLY AFTER FULL VERIFICATION
            transaction.Status        = "success";
            transaction.IsSuccess     = true;
            transaction.TransactionId = verification.TransactionId ?? transaction.TransactionId;
            transaction.UpdatedAt     = DateTime.UtcNow;
            transaction.RawResponseJson = verification.RawResponseJson;

            await _dbContext.SaveChangesAsync();

            return true;
        }
        catch
        {
            return false;
        }
    }

    //  Webhook Signature Verification

    private bool VerifyWebhookSignature(PaymentProvider provider, string body, IHeaderDictionary headers)
    {
        switch (provider)
        {
            case PaymentProvider.Paystack:
            {
                var sig = headers["x-paystack-signature"].ToString();
                if (string.IsNullOrEmpty(sig)) return false;
                using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_paystackOptions.WebhookSecret));
                var hash       = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
                var expected   = BitConverter.ToString(hash).Replace("-", "").ToLower();
                return sig == expected;
            }

            case PaymentProvider.Flutterwave:
            {
                // Flutterwave uses a plain shared secret in the verif-hash header
                var hash = headers["verif-hash"].ToString();
                return hash == _flutterwaveOptions.WebhookSecret;
            }

            case PaymentProvider.Interswitch:
            {
                // Interswitch signs with HmacSHA512 and sends the result (hex) in X-Interswitch-Signature
                var sig = headers["X-Interswitch-Signature"].ToString();
                if (string.IsNullOrEmpty(sig)) return false;

                using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_interswitchOptions.WebhookSecret));
                var hash       = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
                var expected   = BitConverter.ToString(hash).Replace("-", "").ToLower();
                return sig.ToLower() == expected;
            }

            default:
                return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsPendingStatus(string status) =>
        status.Equals("initialized", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("pending",     StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrEmpty(status);

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
}