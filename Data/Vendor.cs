namespace VendorInvoiceAssistant.Data
{
    public class Vendor
    {
        public int VendorId { get; set; }

        public string VendorCode { get; set; } = string.Empty;

        public string VendorName { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public string GSTIN { get; set; } = string.Empty;

        public string PAN { get; set; } = string.Empty;

        public string PaymentTerms { get; set; } = string.Empty;

        public string BankAccount { get; set; } = string.Empty;

        public string IFSC { get; set; } = string.Empty;

        public string City { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string VendorCategory { get; set; } = string.Empty;

        public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

        public ICollection<VendorConversation> VendorConversations { get; set; } = new List<VendorConversation>();
    }
}
