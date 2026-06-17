using Microsoft.EntityFrameworkCore;
using VendorInvoiceAssistant.Data;

namespace VendorInvoiceAssistant.Services
{
    /// <summary>
    /// Persists and replays vendor chat turns using the VendorConversations table,
    /// so the AI assistant has multi-turn memory (clarification follow-ups, etc.).
    /// </summary>
    public class ConversationHistoryService
    {
        private readonly AppDbContext _db;

        public ConversationHistoryService(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>Returns the most recent turns for a vendor, oldest-first, capped at <paramref name="take"/>.</summary>
        public async Task<List<VendorConversation>> GetRecentTurns(int vendorId, int take = 10)
        {
            var recent = await _db.VendorConversations
                .Where(c => c.VendorId == vendorId && c.Channel == "WhatsApp")
                .OrderByDescending(c => c.CreatedDate)
                .ThenByDescending(c => c.ConversationId)
                .Take(take)
                .ToListAsync();

            recent.Reverse(); // chronological order for replay
            return recent;
        }

        /// <summary>Logs a single turn (inbound from vendor or outbound from assistant).</summary>
        public async Task LogTurn(int vendorId, string direction, string messageText, string sessionId, int? relatedInvoiceId = null)
        {
            _db.VendorConversations.Add(new VendorConversation
            {
                VendorId = vendorId,
                Channel = "WhatsApp",
                Direction = direction,          // "Inbound" | "Outbound"
                MessageText = messageText,
                RelatedInvoiceId = relatedInvoiceId,
                SessionId = sessionId,
                CreatedDate = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
    }
}
