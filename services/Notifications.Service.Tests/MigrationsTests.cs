using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Notifications.Service.Tests;

/// <summary>ЛР5: міграції «розширення – скорочення» і стадії переходу коду.</summary>
[TestFixture]
public class MigrationsTests
{
    private SqliteConnection _conn = null!;
    private string _dbName = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = $"mig-{Guid.NewGuid():N}";
        _conn = Open(_dbName);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private static SqliteConnection Open(string name)
    {
        var c = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
        c.Open();
        return c;
    }

    private static TicketsIssuedEvent Event(string email) => new(
        Guid.NewGuid(), "TicketsIssued", DateTimeOffset.UtcNow,
        new TicketsIssuedPayload(Guid.NewGuid(), Guid.NewGuid(), email, new List<string> { "ABC123" }));

    private string Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private List<string> Columns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('notifications');";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private void Run(string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void Fresh_database_ends_with_recipient_email_and_without_buyer_email()
    {
        Migrations.ApplyAll(_conn);

        Assert.That(Columns(), Does.Contain("recipient_email"));
        Assert.That(Columns(), Does.Not.Contain("buyer_email"));
    }

    [Test]
    public void Applying_migrations_twice_changes_nothing()
    {
        Migrations.ApplyAll(_conn);
        var before = Columns();

        Migrations.ApplyAll(_conn);

        Assert.That(Columns(), Is.EqualTo(before));
        Assert.That(Scalar("SELECT COUNT(*) FROM schema_migrations;"), Is.EqualTo(Migrations.All.Length.ToString()));
    }

    [Test]
    public void Expand_stage_writes_both_columns()
    {
        using var db = new NotificationsDb($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        db.Migrate(2);

        db.TryRecord(Event("buyer@example.com"));

        Assert.That(Scalar("SELECT buyer_email || '|' || recipient_email FROM notifications;"),
            Is.EqualTo("buyer@example.com|buyer@example.com"));
    }

    [Test]
    public void Expand_stage_backfills_rows_written_by_old_code()
    {
        // Старий код (до розширення) пише лише buyer_email
        Migrations.ApplyAll(_conn, 1);
        Run("INSERT INTO notifications VALUES ('e1', 'o1', 'old@example.com', 2, '2026-10-01T00:00:00Z');");

        using var db = new NotificationsDb($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        db.Migrate(2);

        Assert.That(Scalar("SELECT recipient_email FROM notifications WHERE event_id = 'e1';"),
            Is.EqualTo("old@example.com"));
        Assert.That(db.List().Count, Is.EqualTo(1));
    }

    [Test]
    public void Contract_stage_drops_buyer_email_keeps_data_and_writes_new_column_only()
    {
        using var db = new NotificationsDb($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        db.Migrate(2);
        db.TryRecord(Event("first@example.com"));

        db.Migrate(3);
        db.TryRecord(Event("second@example.com"));

        Assert.That(Columns(), Does.Not.Contain("buyer_email"));
        Assert.That(Scalar("SELECT recipient_email FROM notifications WHERE recipient_email = 'first@example.com';"),
            Is.EqualTo("first@example.com"));
        Assert.That(db.List().Count, Is.EqualTo(2));
    }
}
