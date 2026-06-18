using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Caching.Memory;
using OpenAI.Chat;
using VendorInvoiceAssistant.Data;
using VendorInvoiceAssistant.Models;

namespace VendorInvoiceAssistant.Services
{
    /// <summary>
    /// Single entry point for all vendor messages. The model decides whether to answer
    /// directly, call a tool, show the menu, ask for clarification, or escalate to a human.
    /// </summary>
    public class VendorAgentService
    {
        private readonly IConfiguration _configuration;
        private readonly InvoiceService _invoiceService;
        private readonly ConversationHistoryService _history;
        private readonly ILogger<VendorAgentService> _logger;
        private readonly IMemoryCache _cache;

        private static readonly TimeSpan SystemPromptTtl = TimeSpan.FromMinutes(3);

        public VendorAgentService(
            IConfiguration configuration,
            InvoiceService invoiceService,
            ConversationHistoryService history,
            ILogger<VendorAgentService> logger,
            IMemoryCache cache)
        {
            _configuration = configuration;
            _invoiceService = invoiceService;
            _history = history;
            _logger = logger;
            _cache = cache;
        }

        // ── Tool definitions ─────────────────────────────────────────────────

        private static readonly ChatTool ShowMenuTool = ChatTool.CreateFunctionTool(
            functionName: "show_menu",
            functionDescription: "Show the interactive WhatsApp options menu to the vendor. Call this ONLY when the vendor greets you or starts a new conversation with no specific question. Do NOT call this for invoice queries — answer those directly.",
            functionParameters: BinaryData.FromString("""{"type":"object","properties":{}}"""));

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
            functionDescription: "Route the request to the human Accounts Payable team (Human-in-the-Loop). Use this for sensitive actions you must NOT perform yourself (e.g. changing bank account details), policy/legal questions you cannot answer reliably (e.g. penalty interest rates), or genuine disputes the vendor wants logged.",
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

        // ── Main entry point ─────────────────────────────────────────────────

        public async Task<AgentResponse> RespondAsync(string phoneNumber, string userMessage, string sessionId)
        {
            var vendor = await _invoiceService.GetVendorByPhone(phoneNumber);
            if (vendor == null)
                return new AgentResponse { Text = "I'm sorry, I couldn't find a vendor account linked to this number. Please contact the AP team to get set up." };

            var snapshots = await _invoiceService.GetInvoiceSnapshots(phoneNumber);
            var historyTurns = await _history.GetRecentTurns(vendor.VendorId);

            await _history.LogTurn(vendor.VendorId, "Inbound", userMessage, sessionId);

            var client = CreateChatClient();

            var cacheKey = $"sysprompt:{vendor.VendorId}";
            var systemPrompt = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SystemPromptTtl;
                return BuildSystemPrompt(vendor, snapshots);
            }) ?? BuildSystemPrompt(vendor, snapshots);

            var messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };

            foreach (var turn in historyTurns)
            {
                if (turn.Direction == "Inbound")
                    messages.Add(new UserChatMessage(turn.MessageText));
                else if (turn.Direction == "Outbound")
                    messages.Add(new AssistantChatMessage(turn.MessageText));
            }

            messages.Add(new UserChatMessage(userMessage));

            var options = new ChatCompletionOptions
            {
                Tools = { ShowMenuTool, GetInvoiceDetailsTool, EscalateToApTool },
                Temperature = 0.2f
            };

            const int maxIterations = 5;
            string finalReply = "I'm sorry, I couldn't process that. Could you please rephrase?";
            int? relatedInvoiceId = null;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var completion = (await client.CompleteChatAsync(messages, options)).Value;

                if (completion.FinishReason == ChatFinishReason.ToolCalls)
                {
                    messages.Add(new AssistantChatMessage(completion));

                    bool showMenu = false;
                    foreach (var toolCall in completion.ToolCalls)
                    {
                        if (toolCall.FunctionName == "show_menu")
                        {
                            showMenu = true;
                            // Add a synthetic result so the model state stays valid if we ever continue.
                            messages.Add(new ToolChatMessage(toolCall.Id, "{\"status\":\"menu_shown\"}"));
                            continue;
                        }

                        var (result, invoiceId) = await ExecuteTool(toolCall, phoneNumber, vendor, sessionId);
                        if (invoiceId.HasValue) relatedInvoiceId = invoiceId;
                        messages.Add(new ToolChatMessage(toolCall.Id, result));
                    }

                    // Return immediately when show_menu is called — the interactive list IS the response.
                    if (showMenu)
                    {
                        await _history.LogTurn(vendor.VendorId, "Outbound", "[MENU_SHOWN]", sessionId);
                        return new AgentResponse { ShowMenu = true };
                    }

                    continue;
                }

                finalReply = completion.Content.Count > 0 ? completion.Content[0].Text : finalReply;
                break;
            }

            await _history.LogTurn(vendor.VendorId, "Outbound", finalReply, sessionId, relatedInvoiceId);
            return new AgentResponse { Text = finalReply };
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
                            return ($"{{\"error\":\"Invoice {invoiceNumber} not found for this vendor account.\"}}", null);

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

                        if (_logger.IsEnabled(LogLevel.Information))
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

        // ── Prompt building ──────────────────────────────────────────────────

        private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pending", "Under Review", "Approved", "Rejected", "On Hold", "Processing"
        };

        private static int EstimateTokens(string text) => text.Length / 4;

        private static string FormatSnapshotLine(InvoiceSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append($"- {s.InvoiceNumber}");
            if (!string.IsNullOrWhiteSpace(s.PONumber)) sb.Append($" (PO: {s.PONumber})");
            sb.Append($" | {s.Status}");
            sb.Append($" | {s.CurrencyCode} {s.InvoiceAmount:N2}");
            sb.Append($" | Invoice date: {s.InvoiceDate:dd MMM yyyy}");
            if (s.DueDate.HasValue) sb.Append($" | Due: {s.DueDate.Value:dd MMM yyyy}");
            if (!string.IsNullOrWhiteSpace(s.InvoiceType)) sb.Append($" | Type: {s.InvoiceType}");
            if (!string.IsNullOrWhiteSpace(s.Remarks)) sb.Append($" | Remarks: {s.Remarks}");
            return sb.ToString();
        }

        private string BuildSystemPrompt(Vendor vendor, List<InvoiceSnapshot> snapshots)
        {
            var today = _configuration["Assistant:Today"];
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
                const int snapshotTokenBudget = 2000;

                var active = snapshots.Where(s => ActiveStatuses.Contains(s.Status ?? "")).ToList();
                var inactive = snapshots.Where(s => !ActiveStatuses.Contains(s.Status ?? "")).ToList();

                var lines = new List<string>();
                int tokensSoFar = 0;
                int truncatedCount = 0;

                foreach (var s in active)
                {
                    var line = FormatSnapshotLine(s);
                    lines.Add(line);
                    tokensSoFar += EstimateTokens(line);
                }

                foreach (var s in inactive)
                {
                    var line = FormatSnapshotLine(s);
                    if (tokensSoFar + EstimateTokens(line) <= snapshotTokenBudget)
                    {
                        lines.Add(line);
                        tokensSoFar += EstimateTokens(line);
                    }
                    else
                    {
                        truncatedCount++;
                    }
                }

                sb.AppendLine($"This vendor has {snapshots.Count} invoice(s). Summary (most recent first):");
                foreach (var line in lines)
                    sb.AppendLine(line);

                if (truncatedCount > 0)
                    sb.AppendLine($"+ {truncatedCount} older paid invoice(s) not shown (use get_invoice_details if the vendor asks about one by number).");

                if (truncatedCount > 0 && _logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Snapshot truncated: {Shown} shown, {Hidden} hidden for vendor {VendorId}",
                        lines.Count, truncatedCount, vendor.VendorId);
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

                ## Interactive menu option IDs
                The vendor may send one of these short IDs when they tap a button on the WhatsApp menu.
                Treat each as the corresponding intent — do NOT show the menu again for these:
                - invoice_status       → vendor wants status of a specific invoice
                - payment_date         → vendor wants payment / due date information
                - outstanding_invoices → vendor wants to see pending or overdue invoices
                - rejected_invoices    → vendor wants to see rejected or on-hold invoices
                - approval_status      → vendor wants the approval chain status
                - raise_dispute        → vendor wants to raise a dispute or query
                - talk_to_ap           → vendor wants to speak to the AP team (use escalate_to_ap)

                ## How to behave
                1. GREETINGS: Call show_menu ONLY when the vendor sends a greeting (hi, hello, good morning, etc.)
                   with no specific question. For all other messages, answer directly.
                2. RESOLVE THE INVOICE FIRST. The vendor may refer to an invoice by number, PO number, month
                   ("my January invoice"), amount ("my 1.5 lakh invoice"), type, or "latest". Match the snapshot.
                3. ASK FOR CLARIFICATION only when genuinely ambiguous — multiple invoices match with no
                   distinguishing detail. List candidates with a key detail (amount, date, status) and ask.
                   If exactly one matches, proceed without asking.
                4. MULTI-INTENT: answer ALL intents in one structured reply. Call get_invoice_details per invoice.
                5. IMPLICIT STATEMENTS ("my invoice is stuck for a month"): infer intent. Check data before
                   escalating — if records show it was paid, share the proof and suggest they check their bank.
                6. ESCALATE via escalate_to_ap for: sensitive changes (bank/IFSC updates), policy/legal questions,
                   genuine disputes. After escalating, tell the vendor it's routed to the AP team.
                7. STRICT DATA BOUNDARY: only discuss invoices in the snapshot above. If an invoice the vendor
                   mentions is not listed, say you can't find it — never invent data or reference other accounts.

                ## Style
                - Concise and warm. Use WhatsApp formatting (*bold* for key values). Indian number formatting and
                  ₹ symbol for INR. Keep replies short — a few lines, not essays.
                - Every fact (date, amount, approver) must come from the snapshot or a tool result.
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
