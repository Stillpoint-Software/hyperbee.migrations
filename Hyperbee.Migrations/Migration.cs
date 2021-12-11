
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    public abstract Task Up();
    public virtual Task Down() => Task.CompletedTask;
}