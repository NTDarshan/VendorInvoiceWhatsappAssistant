using System.ComponentModel.DataAnnotations;

namespace VendorInvoiceAssistant.Data
{
    public class VendorConversation
    {
        [Key]
        public int ConversationId { get; set; }

        public int VendorId { get; set; }

        public string Channel { get; set; } = string.Empty;

        public string Direction { get; set; } = string.Empty;

        public string MessageText { get; set; } = string.Empty;

        public int? RelatedInvoiceId { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public Vendor Vendor { get; set; } = null!;

        public Invoice? RelatedInvoice { get; set; }
    }
}
