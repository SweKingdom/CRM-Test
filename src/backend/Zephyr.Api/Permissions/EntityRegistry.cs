using System.Text.Json;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>
/// One entity as the API and the permission matrix know it: the plural route segment, the entity key (also the
/// <c>external_id.entity_type</c>, at most 50 characters) and the permission key.
/// </summary>
public sealed record EntityInfo(string Plural, string Entity, string Permission);

/// <summary>
/// The entities that have the generic by-external-id upsert and soft delete. A later story registers its entity by adding
/// one <see cref="EntityBinding{T}"/> here and mapping it in <c>Program.cs</c>; the upsert code does not change.
/// </summary>
public static class EntityRegistry
{
    public static readonly EntityBinding<Account> Accounts = new(
        new EntityInfo("accounts", "account", "account"),
        ApplyAccount,
        account => new { account.Id, account.Name, account.CountryCode, account.CreatedAt, account.UpdatedAt });

    /// <summary>PUT semantics: <c>name</c> is required, an absent <c>countryCode</c> clears it.</summary>
    private static void ApplyAccount(Account account, JsonElement body, ModelState state)
    {
        if (body.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
        {
            account.Name = name.GetString()!.Trim();
        }
        else
        {
            state.Add("name", "REQUIRED", "name is required and must be text.");
        }

        if (!body.TryGetProperty("countryCode", out var country) || country.ValueKind == JsonValueKind.Null)
        {
            account.CountryCode = null;
        }
        else if (country.ValueKind == JsonValueKind.String && country.GetString() is { Length: 2 } code && code.All(char.IsAsciiLetterUpper))
        {
            account.CountryCode = code;
        }
        else
        {
            state.Add("countryCode", "INVALID", "countryCode must be two capital letters, for example SE.");
        }
    }
}
