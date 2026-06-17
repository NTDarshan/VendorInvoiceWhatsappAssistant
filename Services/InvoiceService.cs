using Microsoft.EntityFrameworkCore;
using VendorInvoiceAssistant.Data;

namespace VendorInvoiceAssistant.Services
{
    public class InvoiceService
    {
        private readonly AppDbContext _db;

        public InvoiceService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<Invoice>> GetInvoicesByPhone(string phoneNumber)
        {
            return await _db.Invoices
                .Include(i => i.Vendor)
                .Where(i => i.Vendor.PhoneNumber == phoneNumber)
                .OrderByDescending(i => i.InvoiceDate)
                .ToListAsync();
        }

        /// <summary>Resolves the vendor that owns a WhatsApp phone number (null if unknown).</summary>
        public async Task<Vendor?> GetVendorByPhone(string phoneNumber)
        {
            return await _db.Vendors
                .FirstOrDefaultAsync(v => v.PhoneNumber == phoneNumber);
        }

        /// <summary>
        /// A compact, AI-friendly snapshot of every invoice the vendor has — enough for the
        /// model to resolve references like "my January invoice", "the 1.5 lakh one", or a PO number,
        /// and to detect ambiguity (multiple invoices match) before calling a tool.
        /// </summary>
        public async Task<List<InvoiceSnapshot>> GetInvoiceSnapshots(string phoneNumber)
        {
            var invoices = await _db.Invoices
                .Include(i => i.Vendor)
                .Where(i => i.Vendor.PhoneNumber == phoneNumber)
                .OrderByDescending(i => i.InvoiceDate)
                .ToListAsync();

            return invoices.Select(i => new InvoiceSnapshot
            {
                InvoiceNumber = i.InvoiceNumber,
                PONumber = i.PONumber,
                Status = i.Status,
                InvoiceAmount = i.InvoiceAmount,
                TotalAmount = i.TotalAmount,
                CurrencyCode = i.CurrencyCode,
                InvoiceDate = i.InvoiceDate,
                DueDate = i.DueDate,
                ExpectedPaymentDate = i.ExpectedPaymentDate,
                InvoiceType = i.InvoiceType,
                Remarks = i.Remarks
            }).ToList();
        }

        public async Task<InvoiceDetails?> GetFullInvoiceForVendor(string phoneNumber, string invoiceNumber)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Vendor)
                .Include(i => i.InvoiceApprovals)
                .FirstOrDefaultAsync(i => i.InvoiceNumber == invoiceNumber
                    && i.Vendor.PhoneNumber == phoneNumber);

            if (invoice == null) return null;

            return new InvoiceDetails
            {
                InvoiceId = invoice.InvoiceId,
                InvoiceNumber = invoice.InvoiceNumber,
                InvoiceStatus = invoice.Status,
                InvoiceAmount = invoice.InvoiceAmount,
                TaxableAmount = invoice.TaxableAmount,
                TaxAmount = invoice.TaxAmount,
                TotalAmount = invoice.TotalAmount,
                CurrencyCode = invoice.CurrencyCode,
                InvoiceDate = invoice.InvoiceDate,
                DueDate = invoice.DueDate,
                ExpectedPaymentDate = invoice.ExpectedPaymentDate,
                PaymentDate = invoice.PaymentDate,
                PaymentReference = invoice.PaymentReference,
                PONumber = invoice.PONumber,
                InvoiceType = invoice.InvoiceType,
                PaymentTerms = invoice.PaymentTerms,
                Priority = invoice.Priority,
                ApprovedBy = invoice.ApprovedBy,
                ApprovedDate = invoice.ApprovedDate,
                RejectionReason = invoice.RejectionReason,
                Remarks = invoice.Remarks,
                Approvals = invoice.InvoiceApprovals.OrderBy(a => a.Level).Select(a => new ApprovalDetail
                {
                    ApproverName = a.ApproverName,
                    ApproverEmail = a.ApproverEmail,
                    Level = a.Level,
                    Status = a.Status,
                    ActionDate = a.ActionDate,
                    Comments = a.Comments
                }).ToList()
            };
        }

        // Returns the invoice regardless of vendor — used to distinguish "not found" vs "not yours"
        public async Task<Invoice?> FindInvoiceByNumber(string invoiceNumber)
        {
            return await _db.Invoices
                .Include(i => i.Vendor)
                .FirstOrDefaultAsync(i => i.InvoiceNumber == invoiceNumber);
        }

        public async Task<InvoiceLookupResult> GetInvoiceStatus(string phoneNumber, string invoiceNumber)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Vendor)
                .FirstOrDefaultAsync(i => i.InvoiceNumber == invoiceNumber);

            if (invoice == null)
                return new InvoiceLookupResult { Status = InvoiceLookupStatus.NotFound };

            // Case-sensitive phone number comparison
            if (!string.Equals(invoice.Vendor.PhoneNumber, phoneNumber, StringComparison.Ordinal))
                return new InvoiceLookupResult { Status = InvoiceLookupStatus.NotBelongsToVendor };

            return new InvoiceLookupResult
            {
                Status = InvoiceLookupStatus.Found,
                Invoice = new InvoiceDetails
                {
                    InvoiceNumber = invoice.InvoiceNumber,
                    InvoiceStatus = invoice.Status,
                    InvoiceAmount = invoice.InvoiceAmount,
                    ExpectedPaymentDate = invoice.ExpectedPaymentDate
                }
            };
        }
    }

    public enum InvoiceLookupStatus { Found, NotFound, NotBelongsToVendor }

    public class InvoiceLookupResult
    {
        public InvoiceLookupStatus Status { get; set; }
        public InvoiceDetails? Invoice { get; set; }
    }

    /// <summary>Lightweight invoice summary used to give the AI awareness of all the vendor's invoices.</summary>
    public class InvoiceSnapshot
    {
        public string InvoiceNumber { get; set; } = string.Empty;
        public string PONumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal InvoiceAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
        public DateTime InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? ExpectedPaymentDate { get; set; }
        public string InvoiceType { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
    }

    public class InvoiceDetails
    {
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string InvoiceStatus { get; set; } = string.Empty;
        public decimal InvoiceAmount { get; set; }
        public decimal TaxableAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
        public DateTime InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? ExpectedPaymentDate { get; set; }
        public DateTime? PaymentDate { get; set; }
        public string PaymentReference { get; set; } = string.Empty;
        public string PONumber { get; set; } = string.Empty;
        public string InvoiceType { get; set; } = string.Empty;
        public string PaymentTerms { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string ApprovedBy { get; set; } = string.Empty;
        public DateTime? ApprovedDate { get; set; }
        public string RejectionReason { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        public List<ApprovalDetail> Approvals { get; set; } = new();
    }

    public class ApprovalDetail
    {
        public string ApproverName { get; set; } = string.Empty;
        public string ApproverEmail { get; set; } = string.Empty;
        public int Level { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? ActionDate { get; set; }
        public string Comments { get; set; } = string.Empty;
    }
}
