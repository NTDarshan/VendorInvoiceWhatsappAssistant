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
        private readonly VendorAgentService _agent;
        private readonly WhatsAppService _whatsAppService;
        private readonly ILogger<WhatsAppWebhookController> _logger;

        public WhatsAppWebhookController(VendorAgentService agent, WhatsAppService whatsAppService, ILogger<WhatsAppWebhookController> logger)
        {
            _agent = agent;
            _whatsAppService = whatsAppService;
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

            var response = await _agent.RespondAsync(phoneNumber, userMessage, phoneNumber);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Phone: {Phone} | Message: {Msg} | ShowMenu: {Menu} | Reply: {Reply}",
                    phoneNumber, userMessage, response.ShowMenu, response.Text);

            if (response.ShowMenu)
                await _whatsAppService.SendInteractiveList(phoneNumber);
            else
                await _whatsAppService.SendMessage(phoneNumber, response.Text);

            return Ok();
        }

        [HttpGet("test")]
        public IActionResult Get() => Ok("API Working");
    }
}
