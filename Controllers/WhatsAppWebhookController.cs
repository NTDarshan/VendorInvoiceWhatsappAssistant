using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using VendorInvoiceAssistant.Services;

namespace VendorInvoiceAssistant.Controllers
{
    /// <summary>Handles WhatsApp webhook verification and incoming message events.</summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class WhatsAppWebhookController : ControllerBase
    {
        private readonly ChatService _chatService;
        private readonly WhatsAppService _whatsAppService;
        private readonly ILogger<WhatsAppWebhookController> _logger;

        public WhatsAppWebhookController(ChatService chatService, WhatsAppService whatsAppService, ILogger<WhatsAppWebhookController> logger)
        {
            _chatService = chatService;
            _whatsAppService = whatsAppService;
            _logger = logger;
        }

        /// <summary>Verifies the WhatsApp webhook subscription with Meta.</summary>
        /// <param name="mode">The hub mode sent by Meta (must be "subscribe").</param>
        /// <param name="verifyToken">The verification token to validate.</param>
        /// <param name="challenge">The challenge string to echo back on success.</param>
        [HttpGet]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult Verify(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string verifyToken,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            if (verifyToken == "vendor-invoice-poc")
            {
                return Content(challenge);
            }

            return Unauthorized();
        }

        /// <summary>Receives incoming WhatsApp messages and replies via the state-machine flow.</summary>
        /// <param name="payload">The raw WhatsApp webhook payload from Meta.</param>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Receive([FromBody] JsonElement payload)
        {
            var value = payload
                .GetProperty("entry")[0]
                .GetProperty("changes")[0]
                .GetProperty("value");

            if (!value.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
                return Ok();

            var firstMessage = messages[0];
            var phoneNumber = firstMessage.GetProperty("from").GetString()!;
            var messageType = firstMessage.GetProperty("type").GetString();

            string userMessage;

            if (messageType == "interactive")
            {
                // User tapped a row in the interactive list — extract the row id
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

            var (replyText, sendInteractiveList, invoicesForSelection) = await _chatService.ProcessMessage(phoneNumber, userMessage);

            _logger.LogInformation("Phone: {Phone} | Message: {Msg} | sendInteractiveList: {Flag} | invoiceList: {InvCount} | Reply: {Reply}",
                phoneNumber, userMessage, sendInteractiveList, invoicesForSelection?.Count ?? 0, replyText);

            if (invoicesForSelection != null)
                await _whatsAppService.SendInvoiceSelectionList(phoneNumber, invoicesForSelection);
            else if (sendInteractiveList)
                await _whatsAppService.SendInteractiveList(phoneNumber);
            else
                await _whatsAppService.SendMessage(phoneNumber, replyText);

            return Ok();
        }

        [HttpGet ("test")]
        public IActionResult Get()
        {
            return Ok("API Working");
        }
    }
}