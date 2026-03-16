using LineAgent.Api.Services;

namespace LineAgent.Api.Jobs;

public class NotificationJobs
{
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<NotificationJobs> _logger;

    public NotificationJobs(ILineMessagingService lineService, ILogger<NotificationJobs> logger)
    {
        _lineService = lineService;
        _logger = logger;
    }

    public async Task DailyReminderAsync()
    {
        _logger.LogInformation("Running daily reminder at {Time}", DateTime.Now);
        await _lineService.SendDailyReminderAsync();
    }

    // Add more scheduled jobs here:
    // public async Task WeeklyReviewAsync() { ... }
    // public async Task MonthlySummaryAsync() { ... }
}
