using Microsoft.Data.Sqlite;
using TelegramAutomation.Abstractions;

namespace TelegramAutomation.Persistence.SQLite;

public class SqliteAutomationStore : IIdempotencyStore, IConversationStateStore, IOutboxStore, IScheduleStore, IRateLimitStore, ICheckpointStore
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteAutomationStore(string connectionString, IClock? clock = null)
    {
        _connectionString = connectionString;
        _clock = clock ?? new SystemClock();
        Initialize();
    }

    public async Task<bool> TryBeginAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await CleanupExpiredAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "insert or ignore into idempotency(key, expires_at) values ($key, $expires)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$expires", _clock.UtcNow.Add(ttl).ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ConversationState?> GetAsync(long chatId, long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select state,data_json,expires_at from conversation_state where chat_id=$chat and user_id=$user";
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        if (expires <= _clock.UtcNow)
        {
            await ClearAsync(chatId, userId, cancellationToken);
            return null;
        }
        return new ConversationState(chatId, userId, reader.GetString(0), reader.GetString(1), expires);
    }

    public async Task SetAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            insert into conversation_state(chat_id,user_id,state,data_json,expires_at)
            values($chat,$user,$state,$data,$expires)
            on conflict(chat_id,user_id) do update set state=$state,data_json=$data,expires_at=$expires
            """;
        command.Parameters.AddWithValue("$chat", state.ChatId);
        command.Parameters.AddWithValue("$user", state.UserId);
        command.Parameters.AddWithValue("$state", state.State);
        command.Parameters.AddWithValue("$data", state.DataJson);
        command.Parameters.AddWithValue("$expires", state.ExpiresAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(long chatId, long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "delete from conversation_state where chat_id=$chat and user_id=$user";
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            insert into outbox(id,chat_id,text,status,attempts,due_at,idempotency_key,last_error)
            values($id,$chat,$text,$status,$attempts,$due,$idem,$error)
            on conflict(id) do update set chat_id=$chat,text=$text,status=$status,attempts=$attempts,due_at=$due,idempotency_key=$idem,last_error=$error
            """;
        BindOutbox(command, message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> DueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select id,chat_id,text,status,attempts,due_at,idempotency_key,last_error from outbox where status='pending' and due_at <= $now order by due_at limit $take";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$take", take);
        return await ReadOutboxAsync(command, cancellationToken);
    }

    async Task<IReadOnlyList<OutboxMessage>> IOutboxStore.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select id,chat_id,text,status,attempts,due_at,idempotency_key,last_error from outbox order by due_at,id";
        return await ReadOutboxAsync(command, cancellationToken);
    }

    public async Task MarkAsync(string id, string status, int attempts, string? lastError, DateTimeOffset? nextDueAt, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "update outbox set status=$status,attempts=$attempts,last_error=$error,due_at=coalesce($due,due_at) where id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$due", nextDueAt?.ToUnixTimeMilliseconds() is { } due ? due : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAsync(ScheduledJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            insert into schedules(id,chat_id,text,due_at,recurrence_seconds,cancelled,completed_at)
            values($id,$chat,$text,$due,$recurrence,$cancelled,null)
            on conflict(id) do update set chat_id=$chat,text=$text,due_at=$due,recurrence_seconds=$recurrence,cancelled=$cancelled
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$chat", job.ChatId);
        command.Parameters.AddWithValue("$text", job.Text);
        command.Parameters.AddWithValue("$due", job.DueAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$recurrence", job.Recurrence?.TotalSeconds is { } seconds ? Convert.ToInt64(seconds) : DBNull.Value);
        command.Parameters.AddWithValue("$cancelled", job.Cancelled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledJob>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select id,chat_id,text,due_at,recurrence_seconds,cancelled from schedules where cancelled=0 and due_at <= $now order by due_at,id";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return await ReadSchedulesAsync(command, cancellationToken);
    }

    async Task<IReadOnlyList<ScheduledJob>> IScheduleStore.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select id,chat_id,text,due_at,recurrence_seconds,cancelled from schedules order by due_at,id";
        return await ReadSchedulesAsync(command, cancellationToken);
    }

    public async Task CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "update schedules set cancelled=1,completed_at=$completed where id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$completed", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RateLimitDecision> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var cutoff = _clock.UtcNow.Subtract(window).ToUnixTimeMilliseconds();
        var delete = connection.CreateCommand();
        delete.CommandText = "delete from rate_limits where key=$key and observed_at <= $cutoff";
        delete.Parameters.AddWithValue("$key", key);
        delete.Parameters.AddWithValue("$cutoff", cutoff);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        var count = connection.CreateCommand();
        count.CommandText = "select count(*), min(observed_at) from rate_limits where key=$key";
        count.Parameters.AddWithValue("$key", key);
        await using var reader = await count.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var existing = reader.GetInt32(0);
        if (existing >= limit)
        {
            var first = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            return new RateLimitDecision(false, first.Add(window) - _clock.UtcNow);
        }

        var insert = connection.CreateCommand();
        insert.CommandText = "insert into rate_limits(key,observed_at) values($key,$now)";
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$now", _clock.UtcNow.ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return RateLimitDecision.AllowedNow;
    }

    public async Task<long?> GetOffsetAsync(string consumer, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select offset from checkpoints where consumer=$consumer";
        command.Parameters.AddWithValue("$consumer", consumer);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    public async Task SaveOffsetAsync(string consumer, long offset, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "insert into checkpoints(consumer,offset,updated_at) values($consumer,$offset,$updated) on conflict(consumer) do update set offset=$offset,updated_at=$updated";
        command.Parameters.AddWithValue("$consumer", consumer);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$updated", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists schema_version(version integer primary key, applied_at integer not null);
            insert or ignore into schema_version(version, applied_at) values(1, unixepoch() * 1000);
            create table if not exists idempotency(key text primary key, expires_at integer not null);
            create table if not exists conversation_state(chat_id integer not null, user_id integer not null, state text not null, data_json text not null, expires_at integer not null, primary key(chat_id,user_id));
            create table if not exists outbox(id text primary key, chat_id integer not null, text text not null, status text not null, attempts integer not null, due_at integer not null, idempotency_key text, last_error text);
            create table if not exists schedules(id text primary key, chat_id integer not null, text text not null, due_at integer not null, recurrence_seconds integer, cancelled integer not null, completed_at integer);
            create table if not exists checkpoints(consumer text primary key, offset integer not null, updated_at integer not null);
            create table if not exists rate_limits(key text not null, observed_at integer not null);
            create index if not exists ix_outbox_due on outbox(status,due_at);
            create index if not exists ix_schedules_due on schedules(cancelled,due_at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private async Task CleanupExpiredAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "delete from idempotency where expires_at <= $now";
        command.Parameters.AddWithValue("$now", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindOutbox(SqliteCommand command, OutboxMessage message)
    {
        command.Parameters.AddWithValue("$id", message.Id);
        command.Parameters.AddWithValue("$chat", message.ChatId);
        command.Parameters.AddWithValue("$text", message.Text);
        command.Parameters.AddWithValue("$status", message.Status);
        command.Parameters.AddWithValue("$attempts", message.Attempts);
        command.Parameters.AddWithValue("$due", message.DueAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$idem", (object?)message.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)message.LastError ?? DBNull.Value);
    }

    private static async Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OutboxMessage(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ScheduledJob>> ReadSchedulesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<ScheduledJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            TimeSpan? recurrence = reader.IsDBNull(4) ? null : TimeSpan.FromSeconds(reader.GetInt64(4));
            result.Add(new ScheduledJob(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), recurrence, reader.GetInt32(5) == 1));
        }
        return result;
    }
}


