using System.ComponentModel.DataAnnotations;

namespace VendorInvoiceAssistant.Data
{
    public class InvoiceApproval
    {
        [Key]
        public int ApprovalId { get; set; }

        public int InvoiceId { get; set; }

        public string ApproverName { get; set; } = string.Empty;

        public string ApproverEmail { get; set; } = string.Empty;

        public int Level { get; set; }

        public string Status { get; set; } = string.Empty;

        public DateTime? ActionDate { get; set; }

        public string Comments { get; set; } = string.Empty;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public Invoice Invoice { get; set; } = null!;
    }
}
