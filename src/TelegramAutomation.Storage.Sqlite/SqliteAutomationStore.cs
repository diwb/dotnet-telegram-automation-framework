using TelegramAutomation.Abstractions;

namespace TelegramAutomation.Storage.Sqlite;

public sealed class SqliteAutomationStore : TelegramAutomation.Persistence.SQLite.SqliteAutomationStore
{
    public SqliteAutomationStore(string connectionString, IClock? clock = null) : base(connectionString, clock)
    {
    }
}
