namespace Whisper.Outbox.SqlServer.UnitTests;

public class SqlOutboxInstallerTests
{
    // Constraint names are unique within a schema, so each carries its table's name.
    [Fact]
    public void ConstraintName_CarriesTheTableName()
    {
        SqlOutboxInstaller.ConstraintName("PK_OutboxRecords", "orders_outbox").Should().Be("PK_OutboxRecords_orders_outbox");
    }

    [Fact]
    public void ConstraintName_TableNameTooLongForAnIdentifier_UsesAHashOfIt()
    {
        var longTableName = new string('t', 128);
        var anotherLongTableName = new string('t', 127) + "x";

        var name = SqlOutboxInstaller.ConstraintName("CK_Outbox_NextRetryAt_IsUTC", longTableName);
        var nameOfTheOtherTable = SqlOutboxInstaller.ConstraintName("CK_Outbox_NextRetryAt_IsUTC", anotherLongTableName);

        name.Length.Should().BeLessOrEqualTo(128, "SQL Server identifiers are limited to 128 characters");
        name.Should().StartWith("CK_Outbox_NextRetryAt_IsUTC_");
        name.Should().NotBe(nameOfTheOtherTable);
    }
}
