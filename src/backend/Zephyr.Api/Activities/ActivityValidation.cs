using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Activities;

/// <summary>Field and regarding validation of <see cref="ActivityEndpoints"/> (the same class, split along this seam).</summary>
public static partial class ActivityEndpoints
{
    private static readonly string[] Types = ["call", "meeting", "note", "task", "email"];
    private static readonly string[] RegardingTypes = ["lead", "contact", "account", "opportunity", "project", "service_request"];
    private static readonly string[] Directions = ["inbound", "outbound"];
    private static readonly string[] ParticipantRoles = ["from", "to", "cc", "attendee"];

    // Fields the server owns: sending one is a 422, never silently ignored.
    private static readonly string[] ServerFields = ["accountId", "connected", "source", "id", "occurredAt", "createdAt", "updatedAt", "createdBy"];

    private const string SelectOutcome = "select counts_as_contact as \"Value\" from call_outcome where key = @key and active and deleted_at is null";

    private const string SelectUser = "select 1 as \"Value\" from app_user where id = @id and deleted_at is null";

    // The account an activity belongs to, by regarding type. A row means the record exists (and is not deleted); the value is
    // its account, which may be null. A type whose table does not exist yet is "not found" (see ResolveAsync).
    private static readonly Dictionary<string, string> AccountOf = new()
    {
        ["account"] = Lookup("account", "id"),
        ["contact"] = Lookup("contact", "account_id"),
        ["lead"] = Lookup("lead", "account_id"),
        ["opportunity"] = Lookup("opportunity", "account_id"),
        ["project"] = Lookup("project", "account_id"),
        ["service_request"] = Lookup("service_request", "account_id")
    };

    private sealed record Participant(string Role, Guid? ContactId, Guid? UserId, string? Email);

    private sealed record Found(Guid? AccountId);

    /// <summary>An activity as read back from the database (the same JSON the API returns).</summary>
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? Body { get; set; }
        public string? NextStep { get; set; }
        public string? Transcript { get; set; }
        public string? Location { get; set; }
        public string? MeetingUrl { get; set; }
        public string? PhoneNumber { get; set; }
        public string? RegardingType { get; set; }
        public Guid? RegardingId { get; set; }
        public Guid? AccountId { get; set; }
        public string? Direction { get; set; }
        public DateTimeOffset? Due { get; set; }
        public DateTimeOffset? StartAt { get; set; }
        public DateTimeOffset? EndAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? DurationMinutes { get; set; }
        public string? CallOutcomeKey { get; set; }
        public bool? Connected { get; set; }
        public string Source { get; set; } = "ui";
        public DateTimeOffset UpdatedAt { get; set; }
        public List<Participant> Participants { get; set; } = [];
    }

    private static string Lookup(string table, string column) =>
        $"select {column}::text as \"Value\" from {table} where id = @id and deleted_at is null";

    /// <summary>Applies a body to a row, one error per invalid field. Unknown and server-owned fields (accountId, connected, ...) are errors.</summary>
    private static void Apply(Row row, JsonElement body, ModelState state, bool creating)
    {
        foreach (var property in body.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            switch (name)
            {
                case "type" when !creating:
                    state.Add(name, ErrorCodes.NotPatchable, "type cannot be changed. Create a new activity instead.");
                    break;
                case "type":
                    row.Type = Choice(name, value, Types, state, nullable: false) ?? "";
                    break;
                case "subject":
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 and <= 250 } subject)
                    {
                        row.Subject = subject;
                    }
                    else
                    {
                        state.Add(name, ErrorCodes.Validation, "subject is required and can be at most 250 characters.");
                    }

                    break;
                case "body":
                    row.Body = Text(name, value, state);
                    break;
                case "nextStep":
                    row.NextStep = Text(name, value, state);
                    break;
                case "transcript":
                    row.Transcript = Text(name, value, state);
                    break;
                case "location":
                    row.Location = Text(name, value, state);
                    break;
                case "meetingUrl":
                    row.MeetingUrl = Text(name, value, state);
                    break;
                case "phoneNumber":
                    row.PhoneNumber = Text(name, value, state);
                    break;
                case "callOutcomeKey":
                    row.CallOutcomeKey = Text(name, value, state);
                    break;
                case "regardingType":
                    row.RegardingType = Choice(name, value, RegardingTypes, state, nullable: true);
                    break;
                case "regardingId":
                    row.RegardingId = NullableId(name, value, state);
                    break;
                case "direction":
                    row.Direction = Choice(name, value, Directions, state, nullable: true);
                    break;
                case "due":
                    row.Due = Moment(name, value, state);
                    break;
                case "startAt":
                    row.StartAt = Moment(name, value, state);
                    break;
                case "endAt":
                    row.EndAt = Moment(name, value, state);
                    break;
                case "completedAt":
                    row.CompletedAt = Moment(name, value, state);
                    break;
                case "durationMinutes":
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        row.DurationMinutes = null;
                    }
                    else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes) && minutes >= 0)
                    {
                        row.DurationMinutes = minutes;
                    }
                    else
                    {
                        state.Add(name, ErrorCodes.Validation, "durationMinutes must be a whole number of minutes, 0 or more.");
                    }

                    break;
                case "participants":
                    row.Participants = ParseParticipants(value, state);
                    break;
                default:
                    state.Add(
                        name,
                        ServerFields.Contains(name) ? ErrorCodes.NotPatchable : ErrorCodes.Validation,
                        ServerFields.Contains(name) ? $"{name} is set by the server and cannot be sent." : $"Unknown field '{name}'.");
                    break;
            }
        }

        if (creating && !body.TryGetProperty("type", out _))
        {
            state.Add("type", ErrorCodes.Validation, $"type is required: {string.Join(", ", Types)}.");
        }

        if (creating && !body.TryGetProperty("subject", out _))
        {
            state.Add("subject", ErrorCodes.Validation, "subject is required.");
        }
    }

    /// <summary>
    /// The checks that need the whole row or the database: both-or-neither regarding, end after start, the outcome rules
    /// (sets <c>connected</c>), the regarding record (derives <c>account_id</c>) and the participants.
    /// </summary>
    private static async Task CheckAsync(ZephyrDbContext db, Row row, bool regardingChanged, ModelState state, CancellationToken ct)
    {
        if ((row.RegardingType is null) != (row.RegardingId is null))
        {
            state.Add("regardingId", ErrorCodes.Validation, "Send regardingType and regardingId together, or leave both out.");
        }

        if (row.StartAt is not null && row.EndAt is not null && row.EndAt < row.StartAt)
        {
            state.Add("endAt", ErrorCodes.Validation, "endAt cannot be before startAt.");
        }

        if (row.CallOutcomeKey is not null && row.Type != "call")
        {
            state.Add("callOutcomeKey", ErrorCodes.Validation, "callOutcomeKey can only be set on a call.");
        }

        if (!state.IsValid)
        {
            return;
        }

        row.Connected = null;
        if (row.CallOutcomeKey is not null)
        {
            var connected = await db.Database.SqlQueryRaw<bool>(SelectOutcome, P("key", NpgsqlDbType.Text, row.CallOutcomeKey)).ToListAsync(ct);
            if (connected.Count == 0)
            {
                state.Add("callOutcomeKey", ErrorCodes.Validation, $"'{row.CallOutcomeKey}' is not a known call outcome.");
            }
            else
            {
                row.Connected = connected[0];
            }
        }

        if (regardingChanged)
        {
            row.AccountId = null;
            if (row.RegardingType is not null)
            {
                var found = await ResolveAsync(db, row.RegardingType, row.RegardingId!.Value, ct);
                if (found is null)
                {
                    state.Add("regardingId", ErrorCodes.Validation,
                        $"No {row.RegardingType.Replace('_', ' ')} with this id was found. Check regardingType and regardingId.");
                }
                else
                {
                    row.AccountId = found.AccountId;
                }
            }
        }

        for (var i = 0; i < row.Participants.Count; i++)
        {
            var participant = row.Participants[i];
            if (participant.ContactId is { } contact && await ResolveAsync(db, "contact", contact, ct) is null)
            {
                state.Add($"participants[{i}].contactId", ErrorCodes.Validation, "No contact with this id was found.");
            }

            if (participant.UserId is { } userId
                && (await db.Database.SqlQueryRaw<int>(SelectUser, P("id", NpgsqlDbType.Uuid, userId)).ToListAsync(ct)).Count == 0)
            {
                state.Add($"participants[{i}].userId", ErrorCodes.Validation, "No user with this id was found.");
            }
        }
    }

    /// <summary>
    /// Finds the regarding record and its account; null when it does not exist. A regarding type whose table has not been
    /// created yet (its feature is not built) cannot hold a record, so it is "not found" too, not a server error.
    /// </summary>
    private static async Task<Found?> ResolveAsync(ZephyrDbContext db, string type, Guid id, CancellationToken ct)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<string>(AccountOf[type], P("id", NpgsqlDbType.Uuid, id)).ToListAsync(ct);
            if (rows.Count == 0)
            {
                return null;
            }

            return new Found(rows[0] is { } account ? Guid.Parse(account) : null);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return null;
        }
    }

    private static string? Choice(string field, JsonElement value, string[] allowed, ModelState state, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && allowed.Contains(text))
        {
            return text;
        }

        state.Add(field, ErrorCodes.Validation, $"{field} must be one of: {string.Join(", ", allowed)}{(nullable ? ", or null" : "")}.");
        return null;
    }

    private static string? Text(string field, JsonElement value, ModelState state)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        state.Add(field, ErrorCodes.Validation, $"{field} must be text or null.");
        return null;
    }

    private static Guid? NullableId(string field, JsonElement value, ModelState state)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id))
        {
            return id;
        }

        state.Add(field, ErrorCodes.Validation, $"{field} must be an id or null.");
        return null;
    }

    private static DateTimeOffset? Moment(string field, JsonElement value, ModelState state)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var moment))
        {
            return moment;
        }

        state.Add(field, ErrorCodes.Validation, $"{field} must be a UTC date and time like 2026-11-03T14:30:00Z, or null.");
        return null;
    }

    private static List<Participant> ParseParticipants(JsonElement value, ModelState state)
    {
        var list = new List<Participant>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            state.Add("participants", ErrorCodes.Validation, "participants must be a list.");
            return list;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var at = $"participants[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                state.Add(at, ErrorCodes.Validation, "Each participant must be an object with a role and one of contactId, userId or email.");
                continue;
            }

            string? role = null;
            Guid? contactId = null;
            Guid? userId = null;
            string? email = null;
            foreach (var property in item.EnumerateObject())
            {
                var field = $"{at}.{property.Name}";
                switch (property.Name)
                {
                    case "role":
                        role = Choice(field, property.Value, ParticipantRoles, state, nullable: false);
                        break;
                    case "contactId":
                        contactId = NullableId(field, property.Value, state);
                        break;
                    case "userId":
                        userId = NullableId(field, property.Value, state);
                        break;
                    case "email":
                        email = Text(field, property.Value, state)?.Trim();
                        if (email is not null && (email.Length > 320 || email.IndexOf('@') <= 0 || email.EndsWith('@')))
                        {
                            state.Add(field, ErrorCodes.Validation, "email must be an email address.");
                        }

                        break;
                    default:
                        state.Add(field, ErrorCodes.Validation, $"Unknown participant field '{property.Name}'.");
                        break;
                }
            }

            if (role is null)
            {
                state.Add($"{at}.role", ErrorCodes.Validation, $"role is required: {string.Join(", ", ParticipantRoles)}.");
            }

            if ((contactId is null ? 0 : 1) + (userId is null ? 0 : 1) + (email is null ? 0 : 1) != 1)
            {
                state.Add(at, ErrorCodes.Validation, "Each participant needs exactly one of contactId, userId or email.");
            }

            list.Add(new Participant(role ?? "", contactId, userId, email));
        }

        return list;
    }
}
