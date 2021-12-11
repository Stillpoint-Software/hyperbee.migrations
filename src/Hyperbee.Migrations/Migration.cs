
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    public abstract Task UpAsync();
    public virtual Task DownAsync() => Task.CompletedTask;
}