using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VeraMedia.Api.Data;
using VeraMedia.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseInMemoryDatabase("VeraMediaDev");
        return;
    }

    options.UseMySql(connectionString, new MySqlServerVersion(new Version(5, 7, 0)));
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",
                "https://localhost:5173",
                "http://127.0.0.1:5173",
                "http://127.0.0.1:5174")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddJwtBearer("AllowExpired", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy =>
        policy.RequireAssertion(ctx =>
            string.Equals(ctx.User.FindFirst("is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase)));
});
builder.Services.AddHttpClient<IAiChatClient, OpenAiCompatibleChatClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VeraMedia/1.0 (+https://localhost)");
});
builder.Services.AddHttpClient<IImageGenerationService, OpenAiCompatibleImageGenerationService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VeraMedia/1.0 (+https://localhost)");
});
builder.Services.AddHttpClient<IWebPageContentService, WebPageContentService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; VeraMediaBot/1.0; +https://localhost)");
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IConversationIntentRouter, ConversationIntentRouter>();
builder.Services.AddScoped<IGenerationJobRunner, GenerationJobRunner>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IOfficeExportService, OfficeExportService>();
builder.Services.AddScoped<IAttachmentContentService, AttachmentContentService>();
builder.Services.AddSingleton<EdgeTtsClient>();
builder.Services.AddSingleton<IPptVideoTaskManager, PptVideoTaskManager>();
builder.Services.AddSingleton<IPptVideoPreviewStore, PptVideoPreviewStore>();
builder.Services.AddSingleton<IPptxThumbnailRenderer, PptxThumbnailRenderer>();
builder.Services.AddSingleton<ILibreOfficeService, LibreOfficeListenerService>();
builder.Services.AddHostedService(sp => (LibreOfficeListenerService)sp.GetRequiredService<ILibreOfficeService>());
builder.Services.AddScoped<IPptVideoConversionService, PptVideoConversionService>();
builder.Services.AddSingleton<IGenerationJobQueue, GenerationJobQueue>();
builder.Services.AddHostedService<GenerationJobWorker>();
builder.Services.AddScoped<IAiProviderResolver, AiProviderResolver>();
builder.Services.AddScoped<IAppSettingsService, AppSettingsService>();
builder.Services.AddSingleton<IEmailCodeStore, InMemoryEmailCodeStore>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.Configure<SeedUserOptions>(builder.Configuration.GetSection("SeedUser"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<EmailCodeOptions>(builder.Configuration.GetSection("EmailCode"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton(sp =>
{
    var settings = new MutableRuntimeSettings();
    builder.Configuration.GetSection("Auth").Bind(settings.Auth);
    builder.Configuration.GetSection("EmailCode").Bind(settings.EmailCode);
    return settings;
});

var app = builder.Build();

await DatabaseSeeder.SeedDefaultUserAsync(app.Services, builder.Configuration);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseCors("frontend");
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/api", () => Results.Ok(new { name = "VeraMedia API", status = "ok" }));

app.MapFallbackToFile("index.html");

app.Run();
