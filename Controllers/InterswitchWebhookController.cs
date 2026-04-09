using Microsoft.AspNetCore.Mvc;
using Payment_Integration_API.Models;
using Payment_Integration_API.Services;

namespace Payment_Integration_API.Controllers;

[ApiController]
[Route("api/webhooks/interswitch")]
public class InterswitchWebhookController : ControllerBase
{
    private readonly PaymentService _paymentService;
    private readonly ILogger<InterswitchWebhookController> _logger;

    public InterswitchWebhookController(
        PaymentService paymentService,
        ILogger<InterswitchWebhookController> logger)
    {
        _paymentService = paymentService;
        _logger         = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        _logger.LogInformation("Interswitch webhook received");

        // ProcessWebhookAsync handles:
        //   1. Reading the raw body
        //   2. Verifying X-Interswitch-Signature (HmacSHA512)
        //   3. Parsing the payload
        //   4. Updating the DB
        // We must return 200 immediately with no body.
        // Fire-and-forget so the response is not held up by DB work.
        _ = Task.Run(() => _paymentService.ProcessWebhookAsync(
                PaymentProvider.Interswitch, Request));

        // Interswitch docs: return 200 with NO response body.
        // Any non-200 will trigger up to 5 retries.
        return Ok();
    }
}