namespace Payment_Integration_API.Models;

public enum PaymentProvider
{
    Flutterwave,
    Paystack,
    Interswitch
}

public class SchoolPaymentMetadata
{
    public string StudentId { get; set; } = string.Empty;
    public string FeeType { get; set; } = string.Empty;
    // public string InvoiceId { get; set; } = string.Empty;
}

public class PaymentRequest
{
    public PaymentProvider Provider { get; set; }
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "NGN";
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }
    public string? RedirectUrl { get; set; }
    public SchoolPaymentMetadata? Metadata {get; set;}
}

public class PaymentResult
{
    public bool Success { get; set; }
    public PaymentProvider Provider { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? RedirectUrl { get; set; }
    public string? Message { get; set; }
    public string? RawResponseJson { get; set; }
}

public class PaymentVerificationResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? TransactionId { get; set; }
    public decimal Amount { get; set; }
    public string? RawResponseJson { get; set; }
    public string? Message { get; internal set; }
}

public class PaymentStatusResult
{
    public string Reference { get; set; } = string.Empty;
    public PaymentProvider Provider { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? TransactionId { get; set; }
}
