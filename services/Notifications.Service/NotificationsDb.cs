using Microsoft.Data.Sqlite;

/// <summary>
/// Сховище Notifications.Service. Таблиця notifications одночасно є журналом відправлених
/// сповіщень і переліком оброблених подій: первинний ключ event_id не дає обробити подію двічі.
/// </summary>
public sealed class NotificationsDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    // true, поки міграцію скорочення не застосовано: тоді код пише в обидві колонки
    private bool _hasBuyerEmail = true;

    public NotificationsDb(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
    }

    public void Migrate(int targetVersion = int.MaxValue)
    {
        lock (_gate)
        {
            Migrations.ApplyAll(_conn, targetVersion);
            _hasBuyerEmail = HasColumn("buyer_email");
        }
    }

    private bool HasColumn(string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('notifications') WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Ідемпотентний запис. Повертає true, якщо подію записано вперше,
    /// і false, якщо така подія вже оброблена (повторна доставка).
    /// </summary>
    public bool TryRecord(TicketsIssuedEvent evt)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = _hasBuyerEmail
                // Етап розширення: паралельний запис у стару і нову колонку
                ? """
                  INSERT OR IGNORE INTO notifications
                      (event_id, order_id, buyer_email, recipient_email, ticket_count, sent_at)
                  VALUES ($id, $order, $email, $email, $count, $sent);
                  """
                // Етап скорочення: лише нова колонка
                : """
                  INSERT OR IGNORE INTO notifications
                      (event_id, order_id, recipient_email, ticket_count, sent_at)
                  VALUES ($id, $order, $email, $count, $sent);
                  """;
            cmd.Parameters.AddWithValue("$id", evt.EventId.ToString());
            cmd.Parameters.AddWithValue("$order", evt.Payload.OrderId.ToString());
            cmd.Parameters.AddWithValue("$email", evt.Payload.BuyerEmail);
            cmd.Parameters.AddWithValue("$count", evt.Payload.TicketCodes.Count);
            cmd.Parameters.AddWithValue("$sent", DateTimeOffset.UtcNow.ToString("o"));
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public (int Count, List<object> Items) List()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            // Етап розширення: читаємо нову колонку, а для старих рядків – стару (до backfill)
            var recipient = _hasBuyerEmail ? "COALESCE(recipient_email, buyer_email)" : "recipient_email";
            cmd.CommandText = $"""
                SELECT event_id, order_id, {recipient}, ticket_count, sent_at
                FROM notifications ORDER BY sent_at;
                """;
            var items = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new
                {
                    eventId = reader.GetString(0),
                    orderId = reader.GetString(1),
                    to = reader.GetString(2),
                    subject = "Ваші електронні квитки",
                    ticketCount = reader.GetInt32(3),
                    sentAt = reader.GetString(4)
                });
            }
            return (items.Count, items);
        }
    }

    public void Dispose() => _conn.Dispose();
}
