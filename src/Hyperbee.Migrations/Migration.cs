
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    public abstract Task UpAsync( CancellationToken cancellationToken = default );
    public virtual Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}