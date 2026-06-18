using Microsoft.EntityFrameworkCore;

namespace VendorInvoiceAssistant.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Vendor> Vendors => Set<Vendor>();
        public DbSet<Invoice> Invoices => Set<Invoice>();
        public DbSet<InvoiceApproval> InvoiceApprovals => Set<InvoiceApproval>();
        public DbSet<VendorConversation> VendorConversations => Set<VendorConversation>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Invoice>()
                .HasOne(i => i.Vendor)
                .WithMany(v => v.Invoices)
                .HasForeignKey(i => i.VendorId);

            modelBuilder.Entity<Invoice>()
                .Property(i => i.InvoiceAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Invoice>()
                .Property(i => i.TaxableAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Invoice>()
                .Property(i => i.TaxAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Invoice>()
                .Property(i => i.TotalAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<InvoiceApproval>()
                .HasOne(ia => ia.Invoice)
                .WithMany(i => i.InvoiceApprovals)
                .HasForeignKey(ia => ia.InvoiceId);

            modelBuilder.Entity<VendorConversation>()
                .HasOne(vc => vc.Vendor)
                .WithMany(v => v.VendorConversations)
                .HasForeignKey(vc => vc.VendorId);

            modelBuilder.Entity<VendorConversation>()
                .HasOne(vc => vc.RelatedInvoice)
                .WithMany()
                .HasForeignKey(vc => vc.RelatedInvoiceId)
                .IsRequired(false);
        }
    }
}
