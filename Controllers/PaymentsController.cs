using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Payment_Integration_API.Models;
using Payment_Integration_API.Services;

namespace Payment_Integration_API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly PaymentService _paymentService;

    public PaymentsController(PaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    [HttpPost("initiate")]
    public async Task<ActionResult<PaymentResult>> Initiate([FromBody] PaymentRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest("Amount must be greater than 0.");

        // Use studentId as reference if provided, otherwise use provided reference or generate GUID
        request.Reference = !string.IsNullOrWhiteSpace(request.Metadata?.StudentId)
            ? request.Metadata.StudentId
            : string.IsNullOrWhiteSpace(request.Reference)
                ? Guid.NewGuid().ToString("N")
                : request.Reference;

        var result = await _paymentService.InitiateAsync(request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpGet("status/{reference}")]
    public async Task<ActionResult<PaymentStatusResult>> GetStatus(string reference, [FromQuery] bool refresh = false)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest("Reference is required.");

        var result = await _paymentService.GetStatusAsync(reference, refresh);
        return Ok(result);
    }

    [HttpPost("verify")]
    public async Task<ActionResult<PaymentVerificationResult>> Verify([FromQuery] PaymentProvider provider, [FromQuery] string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest("Reference is required.");

        var result = await _paymentService.VerifyAsync(provider, reference);
        return Ok(result);
    }

    [HttpPost("webhook/paystack")]
    public async Task<IActionResult> PaystackWebhook()
    {
        var result = await _paymentService.ProcessWebhookAsync(PaymentProvider.Paystack, Request);
        return result ? Ok() : BadRequest();
    }

    [HttpPost("webhook/flutterwave")]
    public async Task<IActionResult> FlutterwaveWebhook()
    {
        var result = await _paymentService.ProcessWebhookAsync(PaymentProvider.Flutterwave, Request);
        return result ? Ok() : BadRequest();
    }

    [HttpPost("webhook/interswitch")]
    public async Task<IActionResult> InterswitchWebhook()
    {
        var result = await _paymentService.ProcessWebhookAsync(PaymentProvider.Interswitch, Request);
        return result ? Ok() : BadRequest();
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> GenericWebhook([FromBody] object content)
    {
        // Fallback: try to detect provider from payload or headers
        // For now, return received
        return Ok(new { received = true, content });
    }


    [HttpGet("debug/isw-config")]
    public IActionResult DebugIswConfig([FromServices] IOptions<Options.InterswitchOptions> opts)
    {
        var o = opts.Value;
        return Ok(new
        {
            o.ClientId,
            ClientSecretLength = o.ClientSecret?.Length,
            o.MerchantCode,
            o.PayItemId,
            o.TokenUrl,
            o.BaseUrl,
            o.WebCheckoutUrl
        });
    }
}
