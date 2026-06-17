namespace VendorInvoiceAssistant.Data
{
    public class Invoice
    {
        public int InvoiceId { get; set; }

        public string InvoiceNumber { get; set; } = string.Empty;

        public int VendorId { get; set; }

        public decimal InvoiceAmount { get; set; }

        public string CurrencyCode { get; set; } = string.Empty;

        public DateTime InvoiceDate { get; set; }

        public string Status { get; set; } = string.Empty;

        public DateTime? ExpectedPaymentDate { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public string PONumber { get; set; } = string.Empty;

        public string InvoiceType { get; set; } = string.Empty;

        public string PaymentTerms { get; set; } = string.Empty;

        public DateTime? DueDate { get; set; }

        public decimal TaxableAmount { get; set; }

        public decimal TaxAmount { get; set; }

        public decimal TotalAmount { get; set; }

        public string ApprovedBy { get; set; } = string.Empty;

        public DateTime? ApprovedDate { get; set; }

        public string RejectionReason { get; set; } = string.Empty;

        public string PaymentReference { get; set; } = string.Empty;

        public DateTime? PaymentDate { get; set; }

        public string Remarks { get; set; } = string.Empty;

        public string Priority { get; set; } = string.Empty;

        public Vendor Vendor { get; set; } = null!;

        public ICollection<InvoiceApproval> InvoiceApprovals { get; set; } = new List<InvoiceApproval>();
    }
}
