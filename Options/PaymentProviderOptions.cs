namespace Payment_Integration_API.Options;

public class FlutterwaveOptions
{
    public string SecretKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.flutterwave.com/v3/payments";
    public string EncryptionKey { get; set; } = string.Empty; // optional Flutterwave encryption key
    public string WebhookSecret { get; set; } = string.Empty; // verif-hash for webhook verification
}

public class PaystackOptions
{
    public string SecretKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.paystack.co";
    public string WebhookSecret { get; set; } = string.Empty; // same as SecretKey for HMAC
}

public class InterswitchOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string MerchantCode { get; set; } = string.Empty;
    public string PayItemId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://sandbox.interswitchng.com";
    public string WebhookSecret { get; set; } = string.Empty; // for HMAC
    public string WebCheckoutUrl { get; set; } = string.Empty;

    public string TokenUrl => "https://passport.k8.isw.la/passport/oauth/token";
    public string GetTransactionUrl => "https://qa.interswitchng.com/collections/api/v1/gettransaction.json";
}