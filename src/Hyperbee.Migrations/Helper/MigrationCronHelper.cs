using Cronos;

namespace Hyperbee.Migrations.Helper;


public class MigrationCronHelper
{
    public MigrationCronHelper()
    {
    }

    public async Task<bool> CronDelayAsync( string cron )
    {
        var cronExpression = CronExpression.Parse( cron );
        var currentTime = DateTime.UtcNow;

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

