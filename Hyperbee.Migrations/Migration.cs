
namespace Hyperbee.Migrations;

public abstract class Migration
{
    public abstract void Up();
    public virtual void Down()
    {
    }
}