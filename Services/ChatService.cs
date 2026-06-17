using Microsoft.EntityFrameworkCore;
using VendorInvoiceAssistant.Data;
using VendorInvoiceAssistant.Models;

namespace VendorInvoiceAssistant.Services
{
    public class ChatService
    {
        private readonly InvoiceService _invoiceService;
        private readonly AppDbContext _db;
        private readonly AiService _aiService;
        private readonly VendorAgentService _agent;

        public ChatService(InvoiceService invoiceService, AppDbContext db, AiService aiService, VendorAgentService agent)
        {
            _invoiceService = invoiceService;
            _db = db;
            _aiService = aiService;
            _agent = agent;
        }

        public async Task<(string message, bool sendInteractiveList, List<Invoice>? invoicesForSelection)> ProcessMessage(string phoneNumber, string userMessage)
        {
            return await HandleAsync(new ChatRequest { PhoneNumber = phoneNumber, Message = userMessage });
        }

        public async Task<(string message, bool sendInteractiveList, List<Invoice>? invoicesForSelection)> HandleAsync(ChatRequest request)
        {
            var ctx = await _db.ConversationContexts
                .FirstOrDefaultAsync(c => c.PhoneNumber == request.PhoneNumber);

            if (ctx == null)
            {
                ctx = new ConversationContext
                {
                    PhoneNumber = request.PhoneNumber,
                    State = ConversationState.Idle,
                    LastUpdated = DateTime.UtcNow
                };
                _db.ConversationContexts.Add(ctx);
                await _db.SaveChangesAsync();
            }

            // ── Stale option guard ───────────────────────────────────────────
            // If the user taps a row from an OLD interactive list message when the
            // conversation has already moved on (state is Idle), ignore the tap and
            // let them know the session has expired.
            var allKnownOptionIds = new[]
            {
                "invoice_status", "payment_date", "outstanding_invoices",
                "rejected_invoices", "approval_status", "raise_dispute", "talk_to_ap", "other"
            };
            if (ctx.State == ConversationState.Idle && allKnownOptionIds.Contains(request.Message))
            {
                return ("This option is from a previous session and is no longer active. Please send *Hi* to start a new session.", false, null);
            }

            // ── Inside menu flow: awaiting menu selection ────────────────────
            if (ctx.State == ConversationState.AwaitingMenuSelection)
            {
                // "other" from the main menu = vendor wants to ask a free-form question
                if (request.Message == "other")
                {
                    ctx.State = ConversationState.AwaitingInvoiceNumber;
                    ctx.LastUpdated = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return ("Sure! Please go ahead and ask your question or type your invoice number.", false, null);
                }

                var knownOptions = new[]
                {
                    "invoice_status", "payment_date", "outstanding_invoices",
                    "rejected_invoices", "approval_status", "raise_dispute", "talk_to_ap"
                };

                if (knownOptions.Contains(request.Message))
                {
                    var invoices = await _invoiceService.GetInvoicesByPhone(request.PhoneNumber);

                    if (invoices.Count == 0)
                    {
                        ctx.State = ConversationState.Idle;
                        ctx.LastUpdated = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                        return ("No invoices found for your account.", false, null);
                    }

                    ctx.State = ConversationState.AwaitingInvoiceSelection;
                    ctx.SelectedMenuOption = request.Message;
                    ctx.LastUpdated = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return ("", false, invoices);
                }

                // Not a known option — re-show menu
                return ("", true, null);
            }

            // ── Inside menu flow: awaiting invoice selection ─────────────────
            if (ctx.State == ConversationState.AwaitingInvoiceSelection)
            {
                var menuOption = ctx.SelectedMenuOption ?? "invoice_status";

                // User tapped "Other" — ask them to type the invoice number
                if (request.Message == "other")
                {
                    ctx.State = ConversationState.AwaitingInvoiceNumber;
                    ctx.LastUpdated = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return ("Please reply with your invoice number (e.g. INV-1001).", false, null);
                }

                var inv = await _invoiceService.GetFullInvoiceForVendor(request.PhoneNumber, request.Message);

                ctx.State = ConversationState.Idle;
                ctx.SelectedMenuOption = null;
                ctx.LastUpdated = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                if (inv == null)
                    return ($"Invoice *{request.Message}* not found or not linked to your account.", false, null);

                var aiReply = await _aiService.AskAsync(BuildAiPrompt(inv, menuOption));
                return (aiReply, false, null);
            }

            // ── Option flow: user tapped "Other" then typed an invoice number ──
            // This state is only reached from the menu/list flow. Hand the typed
            // message to the AI agent, which resolves the invoice and replies with
            // full context (and can ask for clarification if needed).
            if (ctx.State == ConversationState.AwaitingInvoiceNumber)
            {
                ctx.State = ConversationState.Idle;
                ctx.SelectedMenuOption = null;
                ctx.LastUpdated = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                var agentReply = await _agent.RespondAsync(request.PhoneNumber, request.Message, request.PhoneNumber);
                return (agentReply, false, null);
            }

            // ── Natural-language path (Idle): greeting opens the menu, ──────────
            // everything else goes to the AI agent.
            var greeting = await _aiService.DetectIntent(request.Message);
            if (greeting == "Greeting")
            {
                ctx.State = ConversationState.AwaitingMenuSelection;
                ctx.LastUpdated = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return ("", true, null);
            }

            // The agent handles entity/intent ambiguity, multi-intent, implicit
            // statements, gratitude, and HIL escalation — all via tool-calling.
            var reply = await _agent.RespondAsync(request.PhoneNumber, request.Message, request.PhoneNumber);
            return (reply, false, null);
        }

        private static string BuildAiPrompt(InvoiceDetails inv, string menuOption)
        {
            var focus = menuOption switch
            {
                "payment_date"        => "Focus specifically on payment date, due date, and expected payment date. If already paid, mention the payment reference.",
                "outstanding_invoices"=> "Focus on the outstanding balance, due date, and urgency. Highlight if overdue.",
                "rejected_invoices"   => "Focus on the rejection or on-hold reason and what the vendor might need to do next.",
                "approval_status"     => "Focus on the approval chain — who approved/rejected at each level and current status.",
                "raise_dispute"       => "Acknowledge the vendor's concern. Summarize the invoice details and advise them to contact the AP team with the invoice number.",
                "talk_to_ap"          => "Provide the invoice summary and let the vendor know the AP team will assist them.",
                _                     => "Give a full friendly summary of the invoice status and key details."
            };

            var approvalLines = inv.Approvals.Any()
                ? string.Join("\n", inv.Approvals.Select(a =>
                    $"  Level {a.Level} - {a.ApproverName}: {a.Status}" +
                    (a.ActionDate.HasValue ? $" on {a.ActionDate.Value:dd MMM yyyy}" : "") +
                    (string.IsNullOrEmpty(a.Comments) ? "" : $" ({a.Comments})")))
                : "  No approval records.";

            return $"""
                You are a helpful Accounts Payable assistant. A vendor asked about their invoice via WhatsApp.
                {focus}
                Respond in a friendly, professional tone. Keep it concise (under 5 lines). Use WhatsApp formatting (*bold*).

                Invoice Data:
                - Invoice Number: {inv.InvoiceNumber}
                - Status: {inv.InvoiceStatus}
                - Invoice Date: {inv.InvoiceDate:dd MMM yyyy}
                - Invoice Amount: ₹{inv.InvoiceAmount:N2} ({inv.CurrencyCode})
                - Taxable Amount: ₹{inv.TaxableAmount:N2}
                - Tax Amount: ₹{inv.TaxAmount:N2}
                - Total Amount: ₹{inv.TotalAmount:N2}
                - PO Number: {inv.PONumber}
                - Invoice Type: {inv.InvoiceType}
                - Payment Terms: {inv.PaymentTerms}
                - Due Date: {(inv.DueDate.HasValue ? inv.DueDate.Value.ToString("dd MMM yyyy") : "Not set")}
                - Expected Payment Date: {(inv.ExpectedPaymentDate.HasValue ? inv.ExpectedPaymentDate.Value.ToString("dd MMM yyyy") : "Not set")}
                - Payment Date: {(inv.PaymentDate.HasValue ? inv.PaymentDate.Value.ToString("dd MMM yyyy") : "Not paid yet")}
                - Payment Reference: {(string.IsNullOrEmpty(inv.PaymentReference) ? "N/A" : inv.PaymentReference)}
                - Approved By: {(string.IsNullOrEmpty(inv.ApprovedBy) ? "Pending" : inv.ApprovedBy)}
                - Approved Date: {(inv.ApprovedDate.HasValue ? inv.ApprovedDate.Value.ToString("dd MMM yyyy") : "Pending")}
                - Rejection Reason: {(string.IsNullOrEmpty(inv.RejectionReason) ? "None" : inv.RejectionReason)}
                - Priority: {inv.Priority}
                - Remarks: {(string.IsNullOrEmpty(inv.Remarks) ? "None" : inv.Remarks)}
                - Approval Chain:
                {approvalLines}
                """;
        }
    }
}
