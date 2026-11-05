using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ChameLab.Cli;

public sealed record OptionSpec(string[] Names, string Dest, bool Required, string Action,
    JsonElement Nargs, string? Type, JsonElement Default, JsonElement Const, JsonElement Choices, string? Help)
{
    public bool IsFlag => Action is "_StoreTrueAction" or "_StoreConstAction";
    public bool IsMany => Nargs.ValueKind == JsonValueKind.String && Nargs.GetString() == "*";
    public string Label => Names.Length == 0 ? Dest : string.Join(", ", Names);
}
public sealed record OptionGroup(bool Required, int[] Members);
public sealed record CommandSpec(string Name, string Description, OptionSpec[] Options,
    OptionGroup[] Groups, bool Reader, bool Device, bool ScopedSlot);

public static class CommandCatalog
{
    private static T Read<T>(string name)
    {
        using var stream = typeof(CommandCatalog).Assembly.GetManifestResourceStream("ChameLab.Cli." + name)
            ?? throw new InvalidOperationException("Missing command resource: " + name);
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static IReadOnlyList<CommandSpec> Commands { get; } = Read<CommandSpec[]>("CommandCatalog.json");
    private static readonly Dictionary<string, Dictionary<string, int>> Enums = Read<Dictionary<string, Dictionary<string, int>>>("EnumCatalog.json");

    public static int EnumValue(string type, string value) => Enums[type].TryGetValue(value, out var n) ? n
        : throw new ArgumentException($"Unknown {type}: {value}");

    public static (CommandSpec? Command, string[] Arguments) Find(string[] words)
    {
        for (var i = words.Length; i > 0; i--)
        {
            var path = string.Join(' ', words.Take(i));
            if (Commands.FirstOrDefault(c => c.Name == path) is { } c) return (c, words[i..]);
        }
        return (null, words);
    }

    public static void Help(TextWriter output, CommandSpec? command = null, string prefix = "")
    {
        if (command is null)
        {
            foreach (var c in Commands.Where(c => prefix.Length == 0 || c.Name.StartsWith(prefix + " ", StringComparison.Ordinal)))
                output.WriteLine($"{c.Name,-25} {c.Description.Split('\n')[0]}");
            return;
        }
        output.WriteLine($"{command.Name}\n{command.Description}\n");
        output.WriteLine("  -h, --help              Show this help");
        foreach (var o in command.Options)
            output.WriteLine($"  {o.Label,-25} {o.Help}{(o.Required ? " (required)" : "")}");
    }
}

public sealed class CliArguments
{
    private readonly Dictionary<string, string[]> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _provided = new(StringComparer.Ordinal);
    public string? Get(string name) => _values.GetValueOrDefault(name)?.FirstOrDefault();
    public string Text(string name, string fallback = "") => Get(name) ?? fallback;
    public string[] Many(string name) => _values.GetValueOrDefault(name) ?? [];
    public bool Has(string name) => _provided.Contains(name);
    public bool Flag(string name) => string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);
    public int Int(string name, int fallback = 0) => Get(name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;
    public long Long(string name, long fallback = 0) => Get(name) is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : fallback;
    public byte[] Hex(string name, int? length = null)
    {
        byte[] bytes;
        try { bytes = Convert.FromHexString(string.Concat(Text(name).Where(c => !char.IsWhiteSpace(c)))); }
        catch (FormatException) { throw new ArgumentException($"{name} must contain hexadecimal bytes."); }
        if (length is { } n && bytes.Length != n) throw new ArgumentException($"{name} must contain {n} bytes.");
        return bytes;
    }

    private static string? Value(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

    public static CliArguments Parse(CommandSpec spec, string[] words)
    {
        var result = new CliArguments();
        foreach (var o in spec.Options)
            if (Value(o.Default) is { } d) result._values[o.Dest] = [d];
        var seen = new HashSet<int>();
        var positional = new List<string>();
        var onlyPositional = false;
        var tokens = new List<string>(words);
        for (var i = 0; i < tokens.Count; i++)
        {
            var word = tokens[i];
            if (word == "--" && !onlyPositional) { onlyPositional = true; continue; }
            if (onlyPositional || !word.StartsWith('-') || word == "-") { positional.Add(word); continue; }
            var equal = word.IndexOf('=');
            var name = equal < 0 ? word : word[..equal];
            var index = Array.FindIndex(spec.Options, o => o.Names.Contains(name));
            if (index < 0 && word.Length > 2 && word[1] != '-')
            {
                // argparse supports combined short flags, e.g. hf 14a raw -sc.
                var shortOptions = word[1..].Select(c => Array.FindIndex(spec.Options, o => o.Names.Contains("-" + c))).ToArray();
                if (shortOptions.All(n => n >= 0 && spec.Options[n].IsFlag))
                {
                    tokens.RemoveAt(i); tokens.InsertRange(i, word[1..].Select(c => "-" + c)); i--; continue;
                }
            }
            if (index < 0) throw new ArgumentException($"Unknown option: {name}");
            var option = spec.Options[index];
            string value;
            if (option.IsFlag)
            {
                if (equal >= 0) throw new ArgumentException($"{name} does not take a value.");
                value = Value(option.Const) ?? "true";
            }
            else if (equal >= 0) value = word[(equal + 1)..];
            else if (++i < tokens.Count) value = tokens[i];
            else throw new ArgumentException($"{name} requires a value.");
            Validate(option, value);
            seen.Add(index); result._provided.Add(option.Dest); result._values[option.Dest] = [value];
        }
        var pos = Array.FindIndex(spec.Options, o => o.Names.Length == 0);
        if (positional.Count != 0 && pos < 0) throw new ArgumentException("Unexpected argument: " + positional[0]);
        if (pos >= 0)
        {
            var o = spec.Options[pos];
            if (!o.IsMany && positional.Count > 1) throw new ArgumentException("Too many positional arguments.");
            foreach (var v in positional) Validate(o, v);
            result._values[o.Dest] = positional.ToArray();
            if (positional.Count > 0) { seen.Add(pos); result._provided.Add(o.Dest); }
        }
        for (var i = 0; i < spec.Options.Length; i++)
            if (spec.Options[i].Required && !spec.Options[i].IsMany && !seen.Contains(i))
                throw new ArgumentException("Required argument: " + spec.Options[i].Label);
        foreach (var g in spec.Groups)
        {
            var count = g.Members.Count(seen.Contains);
            if (count > 1 || (g.Required && count == 0))
                throw new ArgumentException("Choose " + (g.Required ? "exactly" : "at most") + " one: " +
                    string.Join(" / ", g.Members.Select(n => spec.Options[n].Label)));
        }
        return result;
    }

    private static void Validate(OptionSpec o, string value)
    {
        if (o.Type == "bool" && !bool.TryParse(value, out _)) throw new ArgumentException($"{o.Label} requires true or false.");
        if (o.Type == "int" && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"{o.Label} requires an integer.");
        if (o.Choices.ValueKind == JsonValueKind.Array && !o.Choices.EnumerateArray().Any(c => Value(c) == value))
            throw new ArgumentException($"Invalid {o.Label}: {value}. Choose {string.Join(", ", o.Choices.EnumerateArray().Select(Value))}.");
        if (o.Type is "fromhex" or "key_parser" or "key16")
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(string.Concat(value.Where(c => !char.IsWhiteSpace(c)))); }
            catch (FormatException) { throw new ArgumentException($"{o.Label} requires hexadecimal bytes."); }
            if (o.Type == "key16" && bytes.Length != 16 || o.Type == "key_parser" && bytes.Length is not (4 or 16))
                throw new ArgumentException($"Invalid key length for {o.Label}.");
        }
    }
}

public static class CommandLine
{
    public static string[] Split(string text)
    {
        var words = new List<string>(); var word = new StringBuilder(); char quote = '\0'; var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == quote && quote != '\0'))
            { word.Append(text[++i]); started = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); started = true; continue; }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c)) { if (started) { words.Add(word.ToString()); word.Clear(); started = false; } }
            else { word.Append(c); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("Unclosed quote.");
        if (started) words.Add(word.ToString());
        return words.ToArray();
    }
}
