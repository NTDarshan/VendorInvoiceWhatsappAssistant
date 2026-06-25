using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using VendorInvoiceAssistant.Data;
using VendorInvoiceAssistant.Services;

namespace VendorInvoiceAssistant.Controllers
{
    /// <summary>Handles WhatsApp webhook verification and incoming message events.</summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class WhatsAppWebhookController : ControllerBase
    {
        private readonly VendorAgentService _agent;
        private readonly WhatsAppService _whatsAppService;
        private readonly InvoiceService _invoiceService;
        private readonly ILogger<WhatsAppWebhookController> _logger;

        public WhatsAppWebhookController(VendorAgentService agent, WhatsAppService whatsAppService, InvoiceService invoiceService, ILogger<WhatsAppWebhookController> logger)
        {
            _agent = agent;
            _whatsAppService = whatsAppService;
            _invoiceService = invoiceService;
            _logger = logger;
        }

        /// <summary>Verifies the WhatsApp webhook subscription with Meta.</summary>
        [HttpGet]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult Verify(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string verifyToken,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            if (verifyToken == "vendor-invoice-poc")
                return Content(challenge);

            return Unauthorized();
        }

        /// <summary>Receives incoming WhatsApp messages and routes them through the AI agent.</summary>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Receive([FromBody] JsonElement payload)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("WEBHOOK POST RECEIVED: {Body}", payload.GetRawText());

            var value = payload.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value");

            if (!value.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
                return Ok();

            var firstMessage = messages[0];
            var phoneNumber = firstMessage.GetProperty("from").GetString()!;
            var messageId = firstMessage.GetProperty("id").GetString()!;
            var messageType = firstMessage.GetProperty("type").GetString();

            string userMessage;

            if (messageType == "interactive")
            {
                userMessage = firstMessage
                    .GetProperty("interactive")
                    .GetProperty("list_reply")
                    .GetProperty("id")
                    .GetString()!;
            }
            else if (messageType == "text")
            {
                userMessage = firstMessage.GetProperty("text").GetProperty("body").GetString()!;
            }
            else
            {
                return Ok();
            }

            await _whatsAppService.MarkAsReadWithTypingAsync(messageId);
            await _whatsAppService.SendTypingIndicatorAsync(phoneNumber);

            var vendor = await _invoiceService.GetVendorByPhone(phoneNumber);
            var vendorName = vendor?.VendorName ?? "Vendor";

            if (IsGreeting(userMessage))
            {
                await _whatsAppService.SendInteractiveList(phoneNumber, vendorName);
                return Ok();
            }

            // Menu options that need an invoice selection list — bypass the AI agent
            if (_invoiceListOptions.Contains(userMessage))
            {
                var invoices = await _invoiceService.GetInvoicesByPhone(phoneNumber);
                var filtered = FilterInvoicesByOption(invoices, userMessage);
                if (filtered.Count == 0)
                    await _whatsAppService.SendMessage(phoneNumber, "No invoices found for this selection.");
                else
                    await _whatsAppService.SendInvoiceSelectionList(phoneNumber, filtered);
                return Ok();
            }

            // Keep typing indicator alive while the AI agent processes (it expires after ~25 s)
            using var cts = new CancellationTokenSource();
            _ = RefreshTypingIndicatorAsync(phoneNumber, cts.Token);

            var response = await _agent.RespondAsync(phoneNumber, userMessage, phoneNumber);
            cts.Cancel();

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Phone: {Phone} | Message: {Msg} | ShowMenu: {Menu} | Reply: {Reply}",
                    phoneNumber, userMessage, response.ShowMenu, response.Text);

            if (response.ShowMenu)
                await _whatsAppService.SendInteractiveList(phoneNumber, vendorName);
            else
                await _whatsAppService.SendMessage(phoneNumber, response.Text);

            return Ok();
        }

        private async Task RefreshTypingIndicatorAsync(string phoneNumber, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(8000, ct);
                    if (!ct.IsCancellationRequested)
                        await _whatsAppService.SendTypingIndicatorAsync(phoneNumber);
                }
            }
            catch (OperationCanceledException) { }
        }

        // Options that show an invoice selection list rather than going to the AI agent
        private static readonly HashSet<string> _invoiceListOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "invoice_status", "payment_date", "outstanding_invoices", "rejected_invoices", "approval_status"
        };

        private static List<Invoice> FilterInvoicesByOption(List<Invoice> invoices, string optionId) =>
            optionId switch
            {
                "outstanding_invoices" => [.. invoices.Where(i =>
                    !string.Equals(i.Status, "Paid", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(i.Status, "Approved", StringComparison.OrdinalIgnoreCase))],
                "rejected_invoices" => [.. invoices.Where(i =>
                    string.Equals(i.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.Status, "Rejected and On Hold", StringComparison.OrdinalIgnoreCase))],
                _ => invoices
            };

        [HttpGet("test")]
        public IActionResult Get() => Ok("API Working");

        private static readonly HashSet<string> _greetingWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "hi", "hello", "hey", "hii", "helo", "good morning", "good afternoon",
            "good evening", "good night", "namaste", "start", "menu", "help"
        };

        private static bool IsGreeting(string message)
        {
            var trimmed = message.Trim();
            return _greetingWords.Contains(trimmed);
        }
    }
}
