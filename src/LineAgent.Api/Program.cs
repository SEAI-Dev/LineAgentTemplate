using Hangfire;
using Hangfire.SqlServer;
using LineAgent.Api.Jobs;
using LineAgent.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ===== Services =====
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient("LINE");
builder.Services.AddHttpClient("SEOS");

builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<IItemService, ItemService>();
builder.Services.AddSingleton<ILineMessagingService, LineMessagingService>();
builder.Services.AddSingleton<IChannelService, ChannelService>();
builder.Services.AddSingleton<IRegistrationService, RegistrationService>();
builder.Services.AddSingleton<IEmployeeSyncService, EmployeeSyncService>();
builder.Services.AddTransient<NotificationJobs>();

// Hangfire
var useSqlite = builder.Configuration["Database:Type"]?.Equals("sqlite", StringComparison.OrdinalIgnoreCase) != false
    || Environment.GetEnvironmentVariable("LINEAGENT_DB")?.Equals("sqlite", StringComparison.OrdinalIgnoreCase) == true;

builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();

    if (useSqlite)
        config.UseInMemoryStorage();
    else
        config.UseSqlServerStorage(builder.Configuration.GetConnectionString("MSSQL")!, new SqlServerStorageOptions
        {
            SchemaName = "HangFire", QueuePollInterval = TimeSpan.FromSeconds(15)
        });
});
builder.Services.AddHangfireServer();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ===== SQLite Auto-Init =====
if (useSqlite)
{
    var factory = app.Services.GetRequiredService<IDbConnectionFactory>();
    using var db = factory.CreateConnection();
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Items (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Description TEXT,
            Priority INTEGER NOT NULL DEFAULT 2,
            Status INTEGER NOT NULL DEFAULT 0,
            Category TEXT,
            DueDate TEXT,
            CompletedAt TEXT,
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS LineUsers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            LineUserId TEXT NOT NULL UNIQUE,
            DisplayName TEXT,
            IsActive INTEGER NOT NULL DEFAULT 1,
            NotifyTypes TEXT NOT NULL DEFAULT 'Daily,Weekly,Monthly',
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS NotificationLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            NotificationType TEXT NOT NULL,
            RecipientUserId TEXT,
            Content TEXT NOT NULL,
            Success INTEGER NOT NULL DEFAULT 1,
            ErrorMessage TEXT,
            SentAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS LineChannels (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChannelName TEXT NOT NULL,
            ChannelId TEXT NOT NULL,
            ChannelSecret TEXT NOT NULL,
            ChannelAccessToken TEXT NOT NULL,
            WebhookPath TEXT NOT NULL UNIQUE,
            BranchNo TEXT,
            IsActive INTEGER NOT NULL DEFAULT 1,
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS Employees (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId TEXT NOT NULL UNIQUE,
            FullNameInChinese TEXT NOT NULL DEFAULT '',
            Mobile TEXT,
            PasswordHash TEXT,
            AssignBranchNo TEXT,
            JobTitle TEXT,
            DepartmentId INTEGER,
            DepartmentName TEXT,
            IsAlive INTEGER NOT NULL DEFAULT 1,
            LastSyncAt TEXT,
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS LineRegistrations (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            LineUserId TEXT NOT NULL,
            EmployeeUserId TEXT NOT NULL,
            ChannelId INTEGER NOT NULL,
            DisplayName TEXT,
            IsActive INTEGER NOT NULL DEFAULT 1,
            RegisteredAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            LastInteractionAt TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Employees_Mobile ON Employees(Mobile);
        CREATE INDEX IF NOT EXISTS IX_LineRegistrations_LineUserId ON LineRegistrations(LineUserId, ChannelId);
        CREATE INDEX IF NOT EXISTS IX_LineChannels_WebhookPath ON LineChannels(WebhookPath);
        """;
    cmd.ExecuteNonQuery();

    // Seed default user from env (legacy)
    var defaultUserId = Environment.GetEnvironmentVariable("LINE_DEFAULT_USER_ID")
        ?? app.Configuration["Line:DefaultUserId"];
    if (!string.IsNullOrEmpty(defaultUserId))
    {
        using var seedCmd = db.CreateCommand();
        seedCmd.CommandText = $"INSERT OR IGNORE INTO LineUsers (LineUserId, DisplayName) VALUES ('{defaultUserId}', 'Default')";
        seedCmd.ExecuteNonQuery();
    }
}

// ===== Pipeline =====
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseCors();
app.UseAuthorization();
app.MapControllers();
app.UseHangfireDashboard("/hangfire");

// ===== Scheduled Jobs =====
var tz = TimeZoneInfo.GetSystemTimeZones()
    .FirstOrDefault(t => t.Id == "Asia/Taipei" || t.Id == "Taipei Standard Time")
    ?? TimeZoneInfo.Local;

RecurringJob.AddOrUpdate<NotificationJobs>(
    "daily-reminder", j => j.DailyReminderAsync(), "0 9 * * 1-5",
    new RecurringJobOptions { TimeZone = tz });

app.Run();
