using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zephyr.Api.Common;

namespace Zephyr.Api.Finance;

/// <summary>The money a client sends: only <c>amount</c> and <c>currency</c>. SEK amount and rate date are computed server-side.</summary>
public sealed record MoneyInput(decimal Amount, string Currency)
{
    private static readonly Regex AmountText = new("^-?[0-9]{1,16}(\\.[0-9]{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyText = new("^[A-Z]{3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <c>{"amount":"100.00","currency":"USD"}</c>. A wrong amount (not a string, more than 2 decimals, more than 16
    /// integer digits) or currency adds one error per field to <paramref name="state"/> and returns null.
    /// <c>amountSEK</c> and <c>rateDate</c> in the input are ignored.
    /// </summary>
    public static MoneyInput? Read(JsonElement element, string field, ModelState state)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            state.Add(field, ErrorCodes.Validation, $"{field} must be an object like {{\"amount\":\"100.00\",\"currency\":\"SEK\"}}.");
            return null;
        }

        var amount = 0m;
        var amountOk = element.TryGetProperty("amount", out var amountValue)
            && amountValue.ValueKind == JsonValueKind.String
            && AmountText.IsMatch(amountValue.GetString()!)
            && decimal.TryParse(amountValue.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
        if (!amountOk)
        {
            state.Add($"{field}.amount", ErrorCodes.Validation, "amount must be a string with at most 2 decimals, for example \"300000.00\".");
        }

        var currency = element.TryGetProperty("currency", out var currencyValue) && currencyValue.ValueKind == JsonValueKind.String
            ? currencyValue.GetString()!
            : "";
        var currencyOk = CurrencyText.IsMatch(currency);
        if (!currencyOk)
        {
            state.Add($"{field}.currency", ErrorCodes.Validation, "currency must be a three-letter code in capitals, for example \"SEK\".");
        }

        return amountOk && currencyOk ? new MoneyInput(amount, currency) : null;
    }
}

/// <summary>An amount in its own currency plus the SEK amount, stamped with the rate of the record's business month.</summary>
[JsonConverter(typeof(MoneyJsonConverter))]
public sealed record Money(decimal Amount, string Currency, decimal AmountSek, DateOnly RateDate)
{
    /// <summary>numeric(18,2) holds 16 integer digits.</summary>
    private const decimal MaxAmount = 9_999_999_999_999_999.99m;

    /// <summary>amount_sek = amount x rate, rounded half-up (away from zero) to 2 decimals. Only the final amount is rounded.</summary>
    public static Money Convert(MoneyInput input, decimal rateToSek, DateOnly rateDate)
    {
        var sek = Math.Round(input.Amount * rateToSek, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(sek) > MaxAmount)
        {
            throw Problems.Validation([new FieldError("amount", ErrorCodes.Validation, "The amount is too large to convert to SEK.")]);
        }

        return new Money(input.Amount, input.Currency, sek, rateDate);
    }
}

/// <summary>Writes <c>{"amount":"300000.00","currency":"SEK","amountSEK":"300000.00","rateDate":"2026-11-01"}</c>. Output only: input is read with <see cref="MoneyInput.Read"/>.</summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Money is output only. Read input with MoneyInput.Read.");

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("amount", value.Amount.ToString("F2", CultureInfo.InvariantCulture));
        writer.WriteString("currency", value.Currency);
        writer.WriteString("amountSEK", value.AmountSek.ToString("F2", CultureInfo.InvariantCulture));
        writer.WriteString("rateDate", value.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
}
