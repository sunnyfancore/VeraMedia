using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ContentProject> ContentProjects => Set<ContentProject>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<GeneratedImage> GeneratedImages => Set<GeneratedImage>();
    public DbSet<UsageLog> UsageLogs => Set<UsageLog>();
    public DbSet<ArticleShare> ArticleShares => Set<ArticleShare>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(190);
            entity.Property(x => x.DisplayName).HasMaxLength(80);
            entity.Property(x => x.PasswordHash).HasMaxLength(255);
        });

        modelBuilder.Entity<AiProvider>(entity =>
        {
            entity.ToTable("ai_providers");
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.BaseUrl).HasMaxLength(300);
            entity.Property(x => x.ApiKey).HasMaxLength(500);
            entity.Property(x => x.ProviderType).HasMaxLength(40);
        });

        modelBuilder.Entity<AiModel>(entity =>
        {
            entity.ToTable("ai_models");
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.ModelType).HasMaxLength(40);
            entity.Property(x => x.CapabilitiesJson).HasColumnType("text");
            entity.HasOne(x => x.Provider).WithMany(x => x.Models).HasForeignKey(x => x.ProviderId);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.Property(x => x.Title).HasMaxLength(160);
            entity.HasOne(x => x.User).WithMany(x => x.Conversations).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.ToTable("conversation_messages");
            entity.Property(x => x.Role).HasMaxLength(20);
            entity.Property(x => x.Content).HasColumnType("text");
            entity.Property(x => x.MetadataJson).HasColumnType("text");
            entity.HasOne(x => x.Conversation).WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId);
        });

        modelBuilder.Entity<GenerationJob>(entity =>
        {
            entity.ToTable("generation_jobs");
            entity.Property(x => x.JobType).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.RequestJson).HasColumnType("mediumtext");
            entity.Property(x => x.Content).HasColumnType("mediumtext");
            entity.Property(x => x.ThinkingJson).HasColumnType("mediumtext");
            entity.Property(x => x.MessageType).HasMaxLength(40);
            entity.Property(x => x.Error).HasMaxLength(1000);
            entity.HasIndex(x => new { x.UserId, x.ConversationId, x.Status });
            entity.HasIndex(x => x.AssistantMessageId);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId);
            entity.HasOne(x => x.AssistantMessage).WithMany().HasForeignKey(x => x.AssistantMessageId);
        });

        modelBuilder.Entity<ContentProject>(entity =>
        {
            entity.ToTable("content_projects");
            entity.Property(x => x.Title).HasMaxLength(160);
            entity.Property(x => x.SourceUrl).HasMaxLength(500);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.HasOne(x => x.User).WithMany(x => x.ContentProjects).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<Article>(entity =>
        {
            entity.ToTable("articles");
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Body).HasColumnType("mediumtext");
            entity.Property(x => x.Platform).HasMaxLength(40);
            entity.HasOne(x => x.Project).WithMany(x => x.Articles).HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<GeneratedImage>(entity =>
        {
            entity.ToTable("generated_images");
            entity.Property(x => x.Prompt).HasColumnType("text");
            entity.Property(x => x.ImageUrl).HasMaxLength(500);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.HasOne(x => x.Project).WithMany(x => x.Images).HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<UsageLog>(entity =>
        {
            entity.ToTable("usage_logs");
            entity.Property(x => x.Operation).HasMaxLength(80);
            entity.Property(x => x.ModelName).HasMaxLength(120);
            entity.HasOne(x => x.User).WithMany(x => x.UsageLogs).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.Property(x => x.Operation).HasMaxLength(80);
            entity.Property(x => x.EntityType).HasMaxLength(80);
            entity.Property(x => x.EntityId).HasMaxLength(80);
            entity.Property(x => x.Detail).HasColumnType("text");
            entity.Property(x => x.IpAddress).HasMaxLength(80);
            entity.Property(x => x.UserAgent).HasMaxLength(300);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.ActorUserId);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_settings");
            entity.HasIndex(x => x.Key).IsUnique();
            entity.Property(x => x.Key).HasMaxLength(120);
            entity.Property(x => x.Value).HasColumnType("text");
        });

        modelBuilder.Entity<ArticleShare>(entity =>
        {
            entity.ToTable("article_shares");
            entity.Property(x => x.Token).HasMaxLength(64);
            entity.HasIndex(x => x.Token).IsUnique();
            entity.HasOne(x => x.Article).WithMany(x => x.Shares).HasForeignKey(x => x.ArticleId);
        });

        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(nullableUtcConverter);
            }
        }
    }
}
