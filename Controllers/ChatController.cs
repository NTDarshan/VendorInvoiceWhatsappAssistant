using Microsoft.AspNetCore.Mvc;
using VendorInvoiceAssistant.Models;
using VendorInvoiceAssistant.Services;

namespace VendorInvoiceAssistant.Controllers
{
    /// <summary>Handles AI-powered chat interactions for vendor invoice queries.</summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class ChatController : ControllerBase
    {
        private readonly VendorAgentService _agent;

        public ChatController(VendorAgentService agent)
        {
            _agent = agent;
        }

        /// <summary>Send a message and receive an AI-generated response about vendor invoices.</summary>
        /// <param name="request">The chat request containing the vendor's phone number and message.</param>
        [HttpPost]
        [ProducesResponseType(typeof(AgentResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            var response = await _agent.RespondAsync(request.PhoneNumber, request.Message, request.PhoneNumber);
            return Ok(response);
        }
    }
}
