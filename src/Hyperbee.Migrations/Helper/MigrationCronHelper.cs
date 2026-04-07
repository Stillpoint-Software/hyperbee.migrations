using Cronos;

namespace Hyperbee.Migrations.Helper;

public class MigrationCronHelper
{
    public async Task<bool> CronDelayAsync( string cron )
    {
        return await CronDelayAsync( cron, CancellationToken.None );
    }

    public async Task<bool> CronDelayAsync( string cron, CancellationToken cancellationToken )
    {
        var cronExpression = CronExpression.Parse( cron );
        var currentTime = DateTime.UtcNow;

        var nextOccurrence = cronExpression.GetNextOccurrence( currentTime, TimeZoneInfo.Utc );

        var results = nextOccurrence <= currentTime;

        if ( !results )
        {
            var delay = (nextOccurrence - currentTime) ?? TimeSpan.Zero;
            await Task.Delay( delay, cancellationToken );
        }

        return true;
    }

    public static bool IsDue( string cronExpression, DateTimeOffset? lastRunOn )
    {
        if ( lastRunOn == null )
            return true;

        var expression = CronExpression.Parse( cronExpression );
        var nextOccurrence = expression.GetNextOccurrence( lastRunOn.Value.UtcDateTime, TimeZoneInfo.Utc );

        return nextOccurrence.HasValue && nextOccurrence.Value <= DateTime.UtcNow;
    }
}
