using Cronos;

namespace Hyperbee.Migrations.Helper;


public class MigrationCronHelper
{
    private readonly TimeProvider timeProvider;

    public MigrationCronHelper( TimeProvider timeProvider )
    {
        this.timeProvider = timeProvider;
    }

    public async Task<bool> CronDelayAsync( string cron )
    {
        var cronExpression = CronExpression.Parse( cron );
        var currentTime = timeProvider.GetUtcNow();

        var nextOccurrence = cronExpression.GetNextOccurrence( currentTime, TimeZoneInfo.Utc );

        var results = nextOccurrence <= currentTime;

        if ( !results )
        {
            var delay = (nextOccurrence - currentTime) ?? TimeSpan.Zero;
            await Task.Delay( delay );
        }
        return true;
    }
}

