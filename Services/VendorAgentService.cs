using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using VendorInvoiceAssistant.Data;

namespace VendorInvoiceAssistant.Services
{
    /// <summary>
    /// The natural-language brain of the assistant. Instead of hard-coded intent branches, it
    /// gives the model (a) a snapshot of every invoice the vendor owns, (b) recent conversation
    /// history, and (c) a set of tools it can call. The model decides whether to answer directly,
    /// ask a clarifying question, handle multiple intents, or escalate to a human (HIL).
    /// </summary>
    public class VendorAgentService
    {
        private readonly IConfiguration _configuration;
        private readonly InvoiceService _invoiceService;
        private readonly ConversationHistoryService _history;
        private readonly ILogger<VendorAgentService> _logger;

        public VendorAgentService(
            IConfiguration configuration,
            InvoiceService invoiceService,
            ConversationHistoryService history,
            ILogger<VendorAgentService> logger)
        {
            _configuration = configuration;
            _invoiceService = invoiceService;
            _history = history;
            _logger = logger;
        }

        // ── Tool definitions exposed to the model ────────────────────────────
        private static readonly ChatTool GetInvoiceDetailsTool = ChatTool.CreateFunctionTool(
            functionName: "get_invoice_details",
            functionDescription: "Fetch the full details of ONE specific invoice the vendor owns: amounts, dates, payment info, rejection reason, and the full approval chain (approver names, emails, levels, status, action dates). Call this once you know exactly which invoice the vendor means.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "invoice_number": {
                  "type": "string",
                  "description": "The exact invoice number, e.g. INV-2026-0037"
                }
              },
              "required": ["invoice_number"]
            }
            """));

        private static readonly ChatTool EscalateToApTool = ChatTool.CreateFunctionTool(
            functionName: "escalate_to_ap",
            functionDescription: "Route the request to the human Accounts Payable team (Human-in-the-Loop). Use this for sensitive actions you must NOT perform yourself (e.g. changing bank account details), policy/legal questions you cannot answer reliably (e.g. penalty interest rates), or genuine disputes the vendor wants logged. This records the request for a human to follow up.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "reason": {
                  "type": "string",
                  "description": "Short reason for escalation, e.g. 'Bank account change request' or 'Penalty interest policy question' or 'Payment amount dispute'."
                },
                "invoice_number": {
                  "type": "string",
                  "description": "Related invoice number if applicable, otherwise empty string."
                }
              },
              "required": ["reason"]
            }
            """));

        /// <summary>
        /// Runs the agentic loop for one inbound vendor message and returns the assistant reply.
        /// Also persists both the inbound and outbound turns to conversation history.
        /// </summary>
        public async Task<string> RespondAsync(string phoneNumber, string userMessage, string sessionId)
        {
            var vendor = await _invoiceService.GetVendorByPhone(phoneNumber);
            if (vendor == null)
                return "I'm sorry, I couldn't find a vendor account linked to this number. Please contact the AP team to get set up.";

            var snapshots = await _invoiceService.GetInvoiceSnapshots(phoneNumber);
            var historyTurns = await _history.GetRecentTurns(vendor.VendorId);

            // Log the inbound turn before we answer, so it's part of history if anything fails later.
            await _history.LogTurn(vendor.VendorId, "Inbound", userMessage, sessionId);

            var client = CreateChatClient();

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt(vendor, snapshots))
            };

            // Replay prior turns so multi-turn clarification works.
            foreach (var turn in historyTurns)
            {
                if (turn.Direction == "Inbound")
                    messages.Add(new UserChatMessage(turn.MessageText));
                else
                    messages.Add(new AssistantChatMessage(turn.MessageText));
            }

            messages.Add(new UserChatMessage(userMessage));

            var options = new ChatCompletionOptions
            {
                Tools = { GetInvoiceDetailsTool, EscalateToApTool },
                Temperature = 0.2f
            };

            // Agentic loop: keep resolving tool calls until the model produces a final answer.
            const int maxIterations = 5;
            string finalReply = "I'm sorry, I couldn't process that. Could you please rephrase?";
            int? relatedInvoiceId = null;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var completion = (await client.CompleteChatAsync(messages, options)).Value;

                if (completion.FinishReason == ChatFinishReason.ToolCalls)
                {
                    // The model must see its own tool-call request echoed back before the results.
                    messages.Add(new AssistantChatMessage(completion));

                    foreach (var toolCall in completion.ToolCalls)
                    {
                        var (result, invoiceId) = await ExecuteTool(toolCall, phoneNumber, vendor, sessionId);
                        if (invoiceId.HasValue) relatedInvoiceId = invoiceId;
                        messages.Add(new ToolChatMessage(toolCall.Id, result));
                    }

                    continue; // let the model use the tool results
                }

                // Normal completion — we have the final text.
                finalReply = completion.Content.Count > 0 ? completion.Content[0].Text : finalReply;
                break;
            }

            await _history.LogTurn(vendor.VendorId, "Outbound", finalReply, sessionId, relatedInvoiceId);
            return finalReply;
        }

        // ── Tool execution ───────────────────────────────────────────────────
        private async Task<(string result, int? relatedInvoiceId)> ExecuteTool(
            ChatToolCall toolCall, string phoneNumber, Vendor vendor, string sessionId)
        {
            try
            {
                using var args = JsonDocument.Parse(toolCall.FunctionArguments);

                switch (toolCall.FunctionName)
                {
                    case "get_invoice_details":
                    {
                        var invoiceNumber = args.RootElement.GetProperty("invoice_number").GetString() ?? "";
                        var inv = await _invoiceService.GetFullInvoiceForVendor(phoneNumber, invoiceNumber);

                        if (inv == null)
                        {
                            var anyInvoice = await _invoiceService.FindInvoiceByNumber(invoiceNumber);
                            if (anyInvoice == null)
                                return ($"{{\"error\":\"Invoice {invoiceNumber} not found in the system.\"}}", null);

                            return ($"{{\"error\":\"Invoice {invoiceNumber} exists but is NOT linked to this vendor account.\"}}", null);
                        }

                        return (SerializeInvoice(inv), inv.InvoiceId);
                    }

                    case "escalate_to_ap":
                    {
                        var reason = args.RootElement.GetProperty("reason").GetString() ?? "Vendor request";
                        var invNum = args.RootElement.TryGetProperty("invoice_number", out var inEl)
                            ? inEl.GetString() ?? "" : "";

                        int? relatedInvoiceId = null;
                        if (!string.IsNullOrWhiteSpace(invNum))
                        {
                            var inv = await _invoiceService.FindInvoiceByNumber(invNum);
                            if (inv != null && inv.VendorId == vendor.VendorId)
                                relatedInvoiceId = inv.InvoiceId;
                        }

                        await _history.LogTurn(
                            vendor.VendorId,
                            "Escalation",
                            $"[HIL] Reason: {reason}" + (string.IsNullOrWhiteSpace(invNum) ? "" : $" | Invoice: {invNum}"),
                            sessionId,
                            relatedInvoiceId);

                        _logger.LogInformation("Escalated to AP — Vendor {VendorId}, Reason: {Reason}", vendor.VendorId, reason);
                        return ("{\"status\":\"escalated\",\"message\":\"Request logged for the AP team to follow up.\"}", relatedInvoiceId);
                    }

                    default:
                        return ($"{{\"error\":\"Unknown tool {toolCall.FunctionName}\"}}", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool execution failed for {Tool}", toolCall.FunctionName);
                return ("{\"error\":\"Internal error while fetching data.\"}", null);
            }
        }

        // ── Prompt building ────────────────────────────────────────────────────
        private string BuildSystemPrompt(Vendor vendor, List<InvoiceSnapshot> snapshots)
        {
            var today = _configuration["Assistant:Today"]; // optional override for testing
            var todayLine = string.IsNullOrWhiteSpace(today)
                ? $"Today's date is {DateTime.UtcNow:dd MMM yyyy}."
                : $"Today's date is {today}.";

            var sb = new StringBuilder();
            if (snapshots.Count == 0)
            {
                sb.AppendLine("This vendor currently has NO invoices in the system.");
            }
            else
            {
                sb.AppendLine($"This vendor has {snapshots.Count} invoice(s). Summary (most recent first):");
                foreach (var s in snapshots)
                {
                    sb.Append($"- {s.InvoiceNumber}");
                    if (!string.IsNullOrWhiteSpace(s.PONumber)) sb.Append($" (PO: {s.PONumber})");
                    sb.Append($" | {s.Status}");
                    sb.Append($" | {s.CurrencyCode} {s.InvoiceAmount:N2}");
                    sb.Append($" | Invoice date: {s.InvoiceDate:dd MMM yyyy}");
                    if (s.DueDate.HasValue) sb.Append($" | Due: {s.DueDate.Value:dd MMM yyyy}");
                    if (!string.IsNullOrWhiteSpace(s.InvoiceType)) sb.Append($" | Type: {s.InvoiceType}");
                    if (!string.IsNullOrWhiteSpace(s.Remarks)) sb.Append($" | Remarks: {s.Remarks}");
                    sb.AppendLine();
                }
            }

            return $$"""
                You are a professional, friendly Accounts Payable (AP) assistant for vendors, speaking over WhatsApp.
                You are talking to vendor "{{vendor.VendorName}}" (code {{vendor.VendorCode}}).
                {{todayLine}}

                ## What you know
                Below is a snapshot of ALL invoices belonging to THIS vendor. Use it to identify which invoice the
                vendor means and to detect ambiguity. It does NOT contain full details (approval chain, payment
                reference, etc.) — call the get_invoice_details tool for that.

                {{sb}}

                ## How to behave
                1. RESOLVE THE INVOICE FIRST. The vendor may refer to an invoice by number, PO number, month
                   ("my January invoice"), amount ("my 1.5 lakh invoice"), type ("my managed services invoice"),
                   or "latest". Match against the snapshot above.
                2. ASK FOR CLARIFICATION when genuinely ambiguous:
                   - If MORE THAN ONE invoice matches (e.g. "my invoice" with 3 invoices, or several share the
                     same amount/type), list the candidates and ask which one. Be specific — show invoice numbers
                     with a distinguishing detail (amount, date, or status).
                   - If exactly one invoice matches, do NOT ask — proceed and confirm naturally in your answer.
                   - If the intent is unclear ("tell me about INV-X"), you may give a concise full summary OR ask
                     what they'd like (status, payment date, or approval details). Prefer a helpful summary.
                3. MULTI-INTENT: if the vendor asks for several things in one message (e.g. status of all invoices
                   AND total pending), answer ALL of them in one clear, structured reply. Call get_invoice_details
                   for each invoice you need.
                4. IMPLICIT / STATEMENT-STYLE messages ("my invoice is stuck for a month", "I haven't received
                   payment for my January invoice"): infer the intent. Verify against the data BEFORE escalating —
                   if records show it WAS paid, share the proof (amount, date, payment reference) and suggest they
                   check their bank instead of escalating.
                5. ESCALATE via the escalate_to_ap tool for:
                   - Sensitive changes you must never perform yourself (e.g. updating bank account / IFSC details).
                   - Policy or legal questions not answerable from invoice data (e.g. penalty interest rates).
                   - Genuine disputes the vendor wants logged.
                   After escalating, tell the vendor you've routed it to the AP team.
                6. STAY IN SCOPE: only discuss this vendor's invoices and AP matters. Never reveal another vendor's
                   data. Never invent values not present in the data.

                ## Style
                - Concise and warm. Use WhatsApp formatting (*bold* for key values). Indian number formatting and
                  the ₹ symbol for INR amounts. Keep replies short — a few lines, not essays.
                - When you state a fact (a date, amount, approver), it must come from the snapshot or a tool result.
                """;
        }

        private static string SerializeInvoice(InvoiceDetails inv)
        {
            var payload = new
            {
                invoiceNumber = inv.InvoiceNumber,
                status = inv.InvoiceStatus,
                invoiceDate = inv.InvoiceDate.ToString("yyyy-MM-dd"),
                invoiceAmount = inv.InvoiceAmount,
                taxableAmount = inv.TaxableAmount,
                taxAmount = inv.TaxAmount,
                totalAmount = inv.TotalAmount,
                currencyCode = inv.CurrencyCode,
                poNumber = inv.PONumber,
                invoiceType = inv.InvoiceType,
                paymentTerms = inv.PaymentTerms,
                priority = inv.Priority,
                dueDate = inv.DueDate?.ToString("yyyy-MM-dd"),
                expectedPaymentDate = inv.ExpectedPaymentDate?.ToString("yyyy-MM-dd"),
                paymentDate = inv.PaymentDate?.ToString("yyyy-MM-dd"),
                paymentReference = inv.PaymentReference,
                approvedBy = inv.ApprovedBy,
                approvedDate = inv.ApprovedDate?.ToString("yyyy-MM-dd"),
                rejectionReason = inv.RejectionReason,
                remarks = inv.Remarks,
                approvalChain = inv.Approvals.Select(a => new
                {
                    level = a.Level,
                    approverName = a.ApproverName,
                    approverEmail = a.ApproverEmail,
                    status = a.Status,
                    actionDate = a.ActionDate?.ToString("yyyy-MM-dd"),
                    comments = a.Comments
                })
            };

            return JsonSerializer.Serialize(payload);
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
