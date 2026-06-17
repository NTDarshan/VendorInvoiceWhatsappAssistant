using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace VendorInvoiceAssistant.Services
{
    public class AiService
    {
        private readonly IConfiguration _configuration;

        public AiService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<string> AskAsync(string userMessage)
        {
            var client = CreateChatClient();

            var response = await client.CompleteChatAsync(
                new SystemChatMessage(
                    """
                    You are a friendly and professional Vendor Invoice Assistant on WhatsApp.
                    - If the user expresses gratitude (thanks, thank you, great, awesome, etc.), respond warmly and let them know you're available if they need anything else.
                    - Keep all replies concise (under 3 lines) and use WhatsApp formatting (*bold*) where appropriate.
                    - Do not mention invoice data unless the user asks about it.
                    """),
                new UserChatMessage(userMessage));

            return response.Value.Content[0].Text;
        }

        // Returns one of: Greeting | InvoiceStatus | Other
        public async Task<string> DetectIntent(string message)
        {
            var client = CreateChatClient();

            var response = await client.CompleteChatAsync(
                new SystemChatMessage(
                    """
                    You are an intent classifier for a Vendor Invoice Assistant.
                    Classify the user message into exactly one of these intents:
                    - Greeting     : user is greeting (hi, hello, good morning, how are you, etc.)
                    - InvoiceStatus: user is asking about a specific invoice status, payment, or details
                    - Other        : anything else

                    Respond with ONLY valid JSON in this exact format: {"intent":"<value>"}
                    No explanation, no markdown, no extra text.
                    """),
                new UserChatMessage(message));

            var json = response.Value.Content[0].Text.Trim();

            // Strip markdown code fences if model wraps the JSON
            if (json.StartsWith("```"))
                json = json.Split('\n').Skip(1).SkipLast(1).Aggregate((a, b) => a + b);

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("intent").GetString() ?? "Other";
        }

        // Returns the invoice number extracted from the message, or empty string if not found
        public async Task<string> ExtractInvoiceNumber(string message)
        {
            var client = CreateChatClient();

            var response = await client.CompleteChatAsync(
                new SystemChatMessage(
                    """
                    Extract the invoice number from the user message.
                    Invoice numbers follow patterns like INV-1001, PO-202, BILL-5 etc.
                    Respond with ONLY valid JSON: {"invoice_number":"<value>"}
                    If no invoice number is found, return: {"invoice_number":""}
                    No explanation, no markdown, no extra text.
                    """),
                new UserChatMessage(message));

            var json = response.Value.Content[0].Text.Trim();

            if (json.StartsWith("```"))
                json = json.Split('\n').Skip(1).SkipLast(1).Aggregate((a, b) => a + b);

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("invoice_number").GetString() ?? string.Empty;
        }

        // Extracts both intent and invoice number from a natural-language message in one call.
        // intent values: invoice_status | payment_date | outstanding_invoices | rejected_invoices |
        //                approval_status | raise_dispute | talk_to_ap | greeting | other
        public async Task<(string intent, string invoiceNumber)> ExtractInvoiceIntent(string message)
        {
            var client = CreateChatClient();

            var response = await client.CompleteChatAsync(
                new SystemChatMessage(
                    """
                    You are an intent and entity extractor for a Vendor Invoice Assistant chatbot.

                    Classify the user message into exactly one intent:
                    - invoice_status       : asking about the status of a specific invoice
                    - payment_date         : asking about payment date / when they will be paid
                    - outstanding_invoices : asking about pending/outstanding invoices
                    - rejected_invoices    : asking about rejected or on-hold invoices
                    - approval_status      : asking about approval chain or who approved/rejected
                    - raise_dispute        : raising a dispute or query about an invoice
                    - talk_to_ap           : wants to talk to the Accounts Payable team
                    - greeting             : greeting message (hi, hello, good morning, etc.)
                    - gratitude            : expressing thanks or satisfaction (thanks, thank you, great, awesome, perfect, etc.)
                    - other                : anything else

                    Also extract the invoice number if present (patterns like INV-1001, PO-202, BILL-5, INV-2026-0037, etc.).

                    Respond with ONLY valid JSON:
                    {"intent":"<value>","invoice_number":"<value or empty string>"}
                    No explanation, no markdown, no extra text.
                    """),
                new UserChatMessage(message));

            var json = response.Value.Content[0].Text.Trim();

            if (json.StartsWith("```"))
                json = json.Split('\n').Skip(1).SkipLast(1).Aggregate((a, b) => a + b);

            using var doc = JsonDocument.Parse(json);
            var intent = doc.RootElement.GetProperty("intent").GetString() ?? "other";
            var invoiceNumber = doc.RootElement.GetProperty("invoice_number").GetString() ?? string.Empty;
            return (intent, invoiceNumber);
        }

        private ChatClient CreateChatClient()
        {
            var endpoint = _configuration["AzureOpenAI:Endpoint"]!;
            var apiKey = _configuration["AzureOpenAI:ApiKey"]!;
            var deployment = _configuration["AzureOpenAI:DeploymentName"]!;

            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            return client.GetChatClient(deployment);
        }
    }
}
