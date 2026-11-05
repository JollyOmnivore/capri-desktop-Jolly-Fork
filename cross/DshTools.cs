using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CapriDesktop
{
    internal sealed class DshTools
    {
        private static readonly HashSet<string> Bridged = new HashSet<string>(new[] { "web_search", "open_website", "look_at_screen", "open_app", "media", "play_music", "app_window", "shortcut", "remember", "remind", "nap", "move_yourself", "animate", "mainframe", "mini_game", "capri_settings", "list_windows", "check_app", "app_volume", "pc_status", "internet_status" }, StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> offered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        private readonly Func<string, ToolArguments, string> action;
        private readonly Func<bool> current;
        private readonly List<string> results = new List<string>();
        private bool opened;
        private bool openAttempted;
        internal readonly string Request;
        internal string Mode { get; private set; } = "all";
        internal JsonElement[] Schemas { get { return offered.Values.ToArray(); } }

        internal DshTools(string request, Func<string, ToolArguments, string> action, Func<bool> current = null, bool computer = false)
        {
            Request = request;
            this.action = action;
            this.current = current;
            string tool; ToolArguments arguments;
            if (Hands.SearchOpening(request)) Mode = "search-open";
            else if (Hands.Direct(request, out tool, out arguments) && tool == "web_search") Mode = "search";
            using JsonDocument catalog = JsonDocument.Parse(Hands.KitFor().Tools);
            foreach (JsonElement entry in catalog.RootElement.EnumerateArray())
            {
                JsonElement definition = entry.GetProperty("function");
                string name = definition.GetProperty("name").GetString();
                if (computer && (name == "list_windows" || name == "shortcut" || name == "app_window")) continue;
                if (Bridged.Contains(name)) offered.Add(name, definition.Clone());
            }
        }

        internal async Task<string> Invoke(string name, string json, string workspace, Func<string, string, string, CancellationToken, Task<bool>> approve, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            if (current != null && !current()) throw new OperationCanceledException();
            if (action == null || !offered.TryGetValue(name, out JsonElement definition)) return "Couldn't run that tool: it is not bridged into DSH.";
            if (json == null || json.Length > 65536) return "Couldn't run that tool: its arguments are too large.";
            using JsonDocument parsed = JsonDocument.Parse(json);
            if (!Valid(definition.GetProperty("parameters"), parsed.RootElement)) return "Couldn't run that tool: invalid arguments.";
            ToolArguments args = Hands.Read(json);
            if (args == null) return "Couldn't run that tool: invalid arguments.";
            args.Asked = Request;
            string unable = Hands.Unable(name, args);
            if (unable != null) return unable;
            if (Mode != "all" && name != "web_search" && (Mode != "search-open" || name != "open_website")) return "Couldn't run that tool: this turn is limited to the requested web search.";
            if (name == "open_website" && Mode != "all" && (Mode != "search-open" || openAttempted || results.Count == 0)) return "Couldn't open a page: this search did not authorize another browser action.";
            if (name == "open_website" && results.Count > 0)
            {
                string link = string.IsNullOrEmpty(args.Search) ? Hands.ReturnedLink(args.Site, results) : null;
                if (link == null) return "Couldn't open a search result: select one exact URL returned by the search.";
                args.Site = link;
            }
            if (approve == null || !await approve(name, json, workspace, stop)) return "Couldn't run that tool: permission was declined.";
            stop.ThrowIfCancellationRequested();
            if (current != null && !current()) throw new OperationCanceledException();
            string result = await Task.Run(() =>
            {
                stop.ThrowIfCancellationRequested();
                if (current != null && !current()) throw new OperationCanceledException();
                return action(name, args) ?? "Couldn't confirm that action.";
            });
            if (name == "web_search" && !Hands.Failed(result) && !Security.IsHold(result)) results.Add(result);
            if (name == "open_website") { openAttempted = true; opened = !Hands.Failed(result) && !Security.IsHold(result); }
            return result;
        }

        internal void VerifyCompletion()
        {
            if (Mode == "search-open" && !opened) throw new InvalidOperationException("Search finished, but opening a page was not confirmed. No browser action was retried.");
        }

        private static bool Valid(JsonElement schema, JsonElement value)
        {
            if (schema.TryGetProperty("enum", out JsonElement choices) && !choices.EnumerateArray().Any(option => option.GetRawText() == value.GetRawText())) return false;
            if (!schema.TryGetProperty("type", out JsonElement type)) return false;
            switch (type.GetString())
            {
                case "string": return value.ValueKind == JsonValueKind.String;
                case "boolean": return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
                case "number":
                case "integer":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number) || type.GetString() == "integer" && !value.TryGetInt64(out _)) return false;
                    return (!schema.TryGetProperty("minimum", out JsonElement min) || number >= min.GetDouble()) && (!schema.TryGetProperty("maximum", out JsonElement max) || number <= max.GetDouble());
                case "array": return value.ValueKind == JsonValueKind.Array && (!schema.TryGetProperty("items", out JsonElement item) || value.EnumerateArray().All(element => Valid(item, element)));
                case "object":
                    if (value.ValueKind != JsonValueKind.Object) return false;
                    if (schema.TryGetProperty("required", out JsonElement required) && required.EnumerateArray().Any(key => !value.TryGetProperty(key.GetString(), out _))) return false;
                    if (!schema.TryGetProperty("properties", out JsonElement properties)) return !value.EnumerateObject().Any();
                    HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonProperty field in value.EnumerateObject())
                        if (!names.Add(field.Name) || !properties.TryGetProperty(field.Name, out JsonElement fieldSchema) || !Valid(fieldSchema, field.Value)) return false;
                    return true;
                default: return false;
            }
        }
    }
}
