
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    protected ILogger Logger { get; private set; }
    protected MigrationOptions Options { get; private set; }

    public abstract void Up();
    public virtual void Down()
    {
    }

    public virtual void Setup( MigrationOptions options, ILogger logger)
    {
        Logger = logger;
        Options = options;
    }
}