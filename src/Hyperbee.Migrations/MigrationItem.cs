namespace Hyperbee.Migrations;

public class MigrationItem
{
    public Migration Migration { get; set; }
    public Direction Direction { get; set; }
    public string RecordId { get; set; }
    public MigrationAttribute Attribute { get; set; }
    public CancellationToken CancellationToken { get; set; }
}

