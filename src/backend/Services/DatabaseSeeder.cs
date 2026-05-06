using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public static class DatabaseSeeder
{
    public static async Task SeedDefaultUserAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        await EnsureUserColumnsAsync(db);
        await EnsureSettingsTableAsync(db);
        await EnsureContentTablesAsync(db);
        await EnsureArticleSharesTableAsync(db);
        await EnsureGenerationJobsTableAsync(db);
        await EnsureAuditLogsTableAsync(db);

        var seed = configuration.GetSection("SeedUser").Get<SeedUserOptions>() ?? new SeedUserOptions();
        if (!seed.Enabled || string.IsNullOrWhiteSpace(seed.Email) || string.IsNullOrWhiteSpace(seed.Password))
        {
            return;
        }

        var email = seed.Email.Trim().ToLowerInvariant();
        var existing = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
        if (existing is not null)
        {
            existing.IsAdmin = true;
            existing.IsEnabled = true;
            if (!string.IsNullOrWhiteSpace(seed.DisplayName))
            {
                existing.DisplayName = seed.DisplayName.Trim();
            }
            await db.SaveChangesAsync();
            return;
        }

        db.Users.Add(new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? email.Split('@')[0] : seed.DisplayName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(seed.Password),
            IsAdmin = true,
            IsEnabled = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureUserColumnsAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await AddColumnIfMissingAsync(db, "users", "IsAdmin", "tinyint(1) NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "users", "IsEnabled", "tinyint(1) NOT NULL DEFAULT 1");
    }

    private static async Task EnsureSettingsTableAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS app_settings (
                Id bigint NOT NULL AUTO_INCREMENT,
                `Key` varchar(120) NOT NULL,
                Value text NOT NULL,
                PRIMARY KEY (Id),
                UNIQUE KEY IX_app_settings_Key (`Key`)
            )
            """);
    }

    private static async Task EnsureArticleSharesTableAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS article_shares (
                Id bigint NOT NULL AUTO_INCREMENT,
                ArticleId bigint NOT NULL,
                Token varchar(64) NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                ExpiresAt datetime(6) NULL,
                PRIMARY KEY (Id),
                UNIQUE KEY IX_article_shares_Token (Token),
                KEY IX_article_shares_ArticleId (ArticleId),
                CONSTRAINT FK_article_shares_articles_ArticleId FOREIGN KEY (ArticleId) REFERENCES articles (Id)
            )
            """);
    }

    private static async Task EnsureContentTablesAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS content_projects (
                Id bigint NOT NULL AUTO_INCREMENT,
                UserId bigint NOT NULL,
                Title varchar(160) NOT NULL,
                SourceUrl varchar(500) NULL,
                Status varchar(40) NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                UpdatedAt datetime(6) NOT NULL,
                PRIMARY KEY (Id),
                KEY IX_content_projects_UserId (UserId),
                CONSTRAINT FK_content_projects_users_UserId FOREIGN KEY (UserId) REFERENCES users (Id)
            )
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS articles (
                Id bigint NOT NULL AUTO_INCREMENT,
                ProjectId bigint NOT NULL,
                Title varchar(200) NOT NULL,
                Body mediumtext NOT NULL,
                Platform varchar(40) NOT NULL,
                Version int NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                PRIMARY KEY (Id),
                KEY IX_articles_ProjectId (ProjectId),
                CONSTRAINT FK_articles_content_projects_ProjectId FOREIGN KEY (ProjectId) REFERENCES content_projects (Id)
            )
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS generated_images (
                Id bigint NOT NULL AUTO_INCREMENT,
                ProjectId bigint NOT NULL,
                Prompt text NOT NULL,
                ImageUrl varchar(500) NULL,
                Status varchar(40) NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                PRIMARY KEY (Id),
                KEY IX_generated_images_ProjectId (ProjectId),
                CONSTRAINT FK_generated_images_content_projects_ProjectId FOREIGN KEY (ProjectId) REFERENCES content_projects (Id)
            )
            """);
    }

    private static async Task EnsureGenerationJobsTableAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS generation_jobs (
                Id bigint NOT NULL AUTO_INCREMENT,
                UserId bigint NOT NULL,
                ConversationId bigint NOT NULL,
                UserMessageId bigint NOT NULL,
                AssistantMessageId bigint NOT NULL,
                JobType varchar(40) NOT NULL,
                Status varchar(40) NOT NULL,
                RequestJson mediumtext NOT NULL,
                Content mediumtext NOT NULL,
                ThinkingJson mediumtext NOT NULL,
                MessageType varchar(40) NULL,
                Error varchar(1000) NULL,
                Version int NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                UpdatedAt datetime(6) NOT NULL,
                StartedAt datetime(6) NULL,
                CompletedAt datetime(6) NULL,
                PRIMARY KEY (Id),
                KEY IX_generation_jobs_UserId_ConversationId_Status (UserId, ConversationId, Status),
                KEY IX_generation_jobs_AssistantMessageId (AssistantMessageId),
                KEY IX_generation_jobs_ConversationId (ConversationId),
                CONSTRAINT FK_generation_jobs_users_UserId FOREIGN KEY (UserId) REFERENCES users (Id),
                CONSTRAINT FK_generation_jobs_conversations_ConversationId FOREIGN KEY (ConversationId) REFERENCES conversations (Id),
                CONSTRAINT FK_generation_jobs_conversation_messages_AssistantMessageId FOREIGN KEY (AssistantMessageId) REFERENCES conversation_messages (Id)
            )
            """);

        await AddColumnIfMissingAsync(db, "generation_jobs", "JobType", "varchar(40) NOT NULL DEFAULT 'conversation'");
        await AddColumnIfMissingAsync(db, "generation_jobs", "ThinkingJson", "mediumtext NULL");
        await AddColumnIfMissingAsync(db, "generation_jobs", "MessageType", "varchar(40) NULL");
        await AddColumnIfMissingAsync(db, "generation_jobs", "StartedAt", "datetime(6) NULL");
        await AddColumnIfMissingAsync(db, "generation_jobs", "CompletedAt", "datetime(6) NULL");
    }

    private static async Task EnsureAuditLogsTableAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS audit_logs (
                Id bigint NOT NULL AUTO_INCREMENT,
                ActorUserId bigint NULL,
                Operation varchar(80) NOT NULL,
                EntityType varchar(80) NOT NULL,
                EntityId varchar(80) NOT NULL,
                Detail text NOT NULL,
                IpAddress varchar(80) NOT NULL,
                UserAgent varchar(300) NOT NULL,
                CreatedAt datetime(6) NOT NULL,
                PRIMARY KEY (Id),
                KEY IX_audit_logs_CreatedAt (CreatedAt),
                KEY IX_audit_logs_ActorUserId (ActorUserId)
            )
            """);
    }

    private static async Task AddColumnIfMissingAsync(AppDbContext db, string table, string column, string definition)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
              AND COLUMN_NAME = @column
            """;
        var tableParam = check.CreateParameter();
        tableParam.ParameterName = "@table";
        tableParam.Value = table;
        check.Parameters.Add(tableParam);
        var columnParam = check.CreateParameter();
        columnParam.ParameterName = "@column";
        columnParam.Value = column;
        check.Parameters.Add(columnParam);

        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (exists)
        {
            return;
        }

        var alterSql = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition;
        await db.Database.ExecuteSqlRawAsync(alterSql);
    }
}
