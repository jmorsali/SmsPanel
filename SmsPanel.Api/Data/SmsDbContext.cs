using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Domain;
namespace SmsPanel.Api.Data;

public sealed class SmsDbContext(DbContextOptions<SmsDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>(); public DbSet<Campaign> Campaigns => Set<Campaign>(); public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>(); public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>(); public DbSet<SecondaryBankAccount> SecondaryBankAccounts => Set<SecondaryBankAccount>(); public DbSet<ReconciliationIssue> ReconciliationIssues => Set<ReconciliationIssue>(); public DbSet<MerchantImportBatch> MerchantImportBatches => Set<MerchantImportBatch>(); public DbSet<Merchant> Merchants => Set<Merchant>(); public DbSet<AppUser> AppUsers => Set<AppUser>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Person>().HasIndex(x => x.NationalCode).IsUnique(); m.Entity<Person>().HasIndex(x => x.Mobile); m.Entity<Person>().HasIndex(x => x.SourceImportBatchId); m.Entity<Person>().Property(x => x.Mobile).HasMaxLength(11); m.Entity<Person>().Property(x => x.CurrentIban).HasMaxLength(26);
        m.Entity<OutboundMessage>().HasIndex(x => new { x.CampaignId, x.PersonId }).IsUnique();
        m.Entity<OutboundMessage>().Property(x => x.RenderedText).HasMaxLength(1000);
        m.Entity<Campaign>().Property(x => x.ConfirmationTemplate).HasMaxLength(1000);
        m.Entity<Campaign>().Property(x => x.InvalidReplyTemplate).HasMaxLength(1000);
        m.Entity<InboundMessage>().HasIndex(x => new { x.Provider, x.ProviderMessageId }).IsUnique();
        m.Entity<SecondaryBankAccount>().HasIndex(x => new { x.PersonId, x.CampaignId }).IsUnique(); m.Entity<SecondaryBankAccount>().HasIndex(x => x.Iban);
        m.Entity<MerchantImportBatch>().HasIndex(x => x.FileHash);
        m.Entity<Merchant>().HasIndex(x => x.TerminalNumber).IsUnique();
        m.Entity<Merchant>().HasIndex(x => x.NationalId);
        m.Entity<Merchant>().Property(x => x.Mobile).HasMaxLength(11);
        m.Entity<Merchant>().HasOne<MerchantImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Merchant>().HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.SetNull);
        m.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        m.Entity<AppUser>().Property(x => x.Username).HasMaxLength(100); m.Entity<AppUser>().Property(x => x.DisplayName).HasMaxLength(200); m.Entity<AppUser>().Property(x => x.PasswordHash).HasMaxLength(1000);
    }
}
