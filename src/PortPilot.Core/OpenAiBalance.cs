using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortPilot.Core;

public sealed record BalanceMessage(int Schema, string State, string? Amount = null, string? Currency = null)
{
    public static BalanceMessage Parse(ReadOnlySpan<byte> json)
    {
        var value = JsonSerializer.Deserialize<BalanceMessage>(json, BalanceStore.JsonOptions) ?? throw new InvalidDataException("Tomt saldomeddelande.");
        if (value.Schema != 1 || value.State is not ("ok" or "unreadable" or "login_required" or "page_closed" or "paused"))
            throw new InvalidDataException("Okänt saldomeddelande.");
        if (value.State == "ok")
        {
            if (value.Currency != "USD" || value.Amount is null || !Regex.IsMatch(value.Amount, @"^-?\d{1,9}(\.\d{1,2})?$", RegexOptions.CultureInvariant)
                || !decimal.TryParse(value.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("Ogiltigt USD-saldo.");
        }
        else if (value.Amount is not null || value.Currency is not null) throw new InvalidDataException("Felstatus får inte innehålla ett nytt saldo.");
        return value;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal? DollarAmount => State == "ok" ? decimal.Parse(Amount!, CultureInfo.InvariantCulture) : null;
}

public sealed record BalanceSnapshot(decimal? Amount, DateTime? ObservedUtc, DateTime CheckedUtc, string State)
{
    public bool IsFresh(DateTime now) => State == "ok" && Amount is not null && ObservedUtc is { } observed
        && now >= observed && now - observed <= TimeSpan.FromMinutes(12);
    public string Label(DateTime now) => Amount is { } amount
        ? "OpenAI · $" + amount.ToString("0.00", CultureInfo.InvariantCulture) + (IsFresh(now) ? "" : " · äldre")
        : State == "login_required" ? "OpenAI · logga in" : "OpenAI · väntar";
    public string Hint(DateTime now)
    {
        var latest = ObservedUtc is { } time ? $"Senast avläst {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}.\n" : "Inget saldo har lästs av ännu.\n";
        var state = State switch
        {
            "login_required" => "Logga in på OpenAI Billing i den anslutna webbläsaren.",
            "page_closed" => "Billing-fliken är stängd. Anslut en flik igen i tillägget.",
            "paused" => "Billing-fliken har lämnat översikten. Öppna översikten igen.",
            "unreadable" => "Saldofältet kunde inte läsas. Öppna Billing och kontrollera sidan.",
            _ => IsFresh(now) ? "USD-saldo från fältet API credit balance i Billing." : "Avläsningen är äldre. Öppna Billing för en ny avläsning."
        };
        return latest + state + "\nKlicka på OpenAI-loggan för koppling och inställningar. Dra i textytan för att flytta fönstret.";
    }
}

public sealed class BalanceStore(string directory)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public string FilePath { get; } = Path.Combine(directory, "openai-balance.json");
    public BalanceSnapshot? Read()
    {
        if (!File.Exists(FilePath)) return null;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4096) throw new InvalidDataException("Saldofilen är för stor.");
        var value = JsonSerializer.Deserialize<BalanceSnapshot>(stream, JsonOptions) ?? throw new InvalidDataException("Saldofilen är tom.");
        if (value.State is not ("ok" or "unreadable" or "login_required" or "page_closed" or "paused")
            || value.CheckedUtc.Kind != DateTimeKind.Utc || value.ObservedUtc is { Kind: not DateTimeKind.Utc }
            || value.Amount is not null && value.ObservedUtc is null || value.State == "ok" && value.Amount is null
            || value.Amount is { } amount && (Math.Abs(amount) >= 1_000_000_000 || decimal.Round(amount, 2) != amount)
            || value.ObservedUtc > value.CheckedUtc || value.CheckedUtc == DateTime.MinValue)
            throw new InvalidDataException("Ogiltig saldofil.");
        return value;
    }
    public BalanceSnapshot Apply(BalanceMessage message, DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc) throw new InvalidDataException("Avläsningstiden måste vara UTC.");
        // Validate even when called directly rather than through the native message parser.
        message = BalanceMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions));
        var old = Read();
        var snapshot = message.State == "ok" ? new BalanceSnapshot(message.DollarAmount, now, now, "ok")
            : new BalanceSnapshot(old?.Amount, old?.ObservedUtc, now, message.State);
        Directory.CreateDirectory(directory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return snapshot;
    }
}
