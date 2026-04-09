using Payment_Integration_API.Models;

namespace Payment_Integration_API.Services;

public interface IPaymentProvider
{
    Task<PaymentResult> ChargeCustomerAsync(PaymentRequest request);
    Task<PaymentVerificationResult> VerifyAsync(string reference, object amountInKobo);
}