using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly ChatService _chatService;

        public ChatController(ChatService chatService)
        {
            _chatService = chatService;
        }

        /// <summary>Send a message and receive an AI-generated response about vendor invoices.</summary>
        /// <param name="request">The chat request containing the user's message.</param>
        /// <returns>The AI-generated response string.</returns>
        [HttpPost]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            var answer = await _chatService.HandleAsync(request);

            return Ok(answer);
        }
    }
}