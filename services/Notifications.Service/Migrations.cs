using Microsoft.Data.Sqlite;

/// <summary>
/// Міграції схеми за шаблоном «розширення – скорочення» (ЛР5).
/// Кожна міграція застосовується один раз; номер версії зберігається в schema_migrations.
/// Етап 1 (версія 2) додає нову колонку поруч зі старою; етап 2 (версія 3) видаляє стару
/// після того, як код переведено на читання і запис нової колонки.
/// </summary>
public static class Migrations
{
    public static readonly (int Version, string Name, string Sql)[] All =
    {
        (1, "create_notifications", """
            CREATE TABLE notifications (
                event_id     TEXT PRIMARY KEY,
                order_id     TEXT NOT NULL,
                buyer_email  TEXT NOT NULL,
                ticket_count INTEGER NOT NULL,
                sent_at      TEXT NOT NULL
            );
            """),

        // Розширення: нова колонка, backfill існуючих рядків. Попередня версія коду
        // (пише лише buyer_email) продовжує працювати.
        (2, "expand_recipient_email", """
            ALTER TABLE notifications ADD COLUMN recipient_email TEXT;
            UPDATE notifications SET recipient_email = buyer_email WHERE recipient_email IS NULL;
            """),

        // Скорочення: стару колонку вилучено після переходу коду на recipient_email.
        (3, "contract_drop_buyer_email", """
            ALTER TABLE notifications DROP COLUMN buyer_email;
            """),
    };

    public static int Latest => All[^1].Version;

    /// <summary>Застосовує міграції до версії target включно (за замовчуванням – усі).</summary>
    public static void ApplyAll(SqliteConnection conn, int target = int.MaxValue)
    {
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        long current;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            current = (long)read.ExecuteScalar()!;
        }

        foreach (var (version, name, sql) in All.Where(m => m.Version > current && m.Version <= target))
        {
            using var tx = conn.BeginTransaction();
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($v, $n, $t);";
                mark.Parameters.AddWithValue("$v", version);
                mark.Parameters.AddWithValue("$n", name);
                mark.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
                mark.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
