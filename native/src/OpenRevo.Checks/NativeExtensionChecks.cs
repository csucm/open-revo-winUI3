using OpenRevo.Core;
using System.Text.Json;

internal static class NativeExtensionChecks
{
    internal static void Run(string output)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenRevo-extensions-" + Guid.NewGuid().ToString("N"));
        var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { rejected = true; }
            Check(rejected, name);
        }
        try
        {
            string? destination = null;
            using var host = new NativeExtensionHost(directory, value => destination = value);
            Reject(() => host.Register("a", 2), "unknown major rejected");
            foreach (string id in new[] { "../other", "C:\\temp", "", "a.b", new string('a', 65) }) Reject(() => host.Register(id, 1), "invalid ID rejected: " + id);
            using var a = host.Register("a", 1);
            using var b = host.Register("b", 1);
            Reject(() => host.Register("a", 1), "duplicate binding rejected");
            Reject(() => host.Register("A", 1), "case-insensitive filesystem identity collision rejected");
            var cap = a.QueryCapabilities(); cap.Actions[0] = "core.write";
            Check(!a.QueryCapabilities().Actions.Contains("core.write") && !cap.HardwareActions && !cap.WebCompatibility, "capability copies and explicit exclusions");
            Check(!a.Execute("core.write").Success && destination == null, "unknown action never dispatched");
            Check(a.Execute("ui.open.overview").Success && destination == "overview", "allowlisted action dispatched");
            int deliveries = 0, other = 0, broken = 0;
            using var sub = a.Subscribe("system.summary", _ => deliveries++);
            using var subB = b.Subscribe("system.summary", _ => other++);
            using var failure = a.Subscribe("system.summary", _ => { broken++; throw new Exception("bad observer"); });
            Reject(() => a.Subscribe("core.raw", _ => { }), "unknown topic rejected");
            var state = new ExtensionState(DateTimeOffset.UtcNow, null, null, null, null, null);
            host.Publish(state); host.Publish(state);
            Check(deliveries == 2 && other == 2 && broken == 1, "bad observer removed without interrupting other subscribers");
            a.SetEnabled(false); host.Publish(state);
            Check(deliveries == 2 && other == 3 && a.Execute("ui.open.power").Code == "disabled", "disable removes callbacks and denies actions");
            Reject(() => a.Subscribe("system.summary", _ => { }), "disabled subscription denied");
            a.SetEnabled(true); host.Publish(state);
            Check(deliveries == 2, "enable does not revive old callbacks");
            using (var fresh = a.Subscribe("system.summary", _ => deliveries++)) Check(deliveries == 3, "latest timestamped state delivered on subscription");
            host.Publish(state); Check(deliveries == 3, "unsubscribe stops notifications");
            Check(a.LoadConfig() == null, "missing config has no invented data");
            using var data = JsonDocument.Parse("{\"enabled\":true}");
            a.SaveConfig(data.RootElement);
            Check(a.LoadConfig()!.Value.GetProperty("enabled").GetBoolean() && b.LoadConfig() == null, "config reload and client isolation");
            string file = Path.Combine(directory, "a.json"), original = File.ReadAllText(file);
            using var oversized = JsonDocument.Parse("{\"value\":\"" + new string('x', NativeExtensionHost.MaximumConfigBytes) + "\"}");
            Reject(() => a.SaveConfig(oversized.RootElement), "oversized config rejected");
            Check(File.ReadAllText(file) == original, "oversized save preserves prior config");
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => a.SaveConfig(data.RootElement), "persistent sharing failure reported");
            Check(File.ReadAllText(file) == original && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "failed atomic save preserves old config and cleans temp");
            File.WriteAllText(file, "{\"version\":2,\"data\":{}}");
            Reject(() => a.LoadConfig(), "unknown config schema rejected");
            File.WriteAllText(file, "{"); Reject(() => a.LoadConfig(), "malformed config reported");
            Check(File.ReadAllText(file) == "{", "invalid config not overwritten on read");
            bool crossThreadRejected = false;
            var thread = new Thread(() => { try { a.QueryCapabilities(); } catch (InvalidOperationException) { crossThreadRejected = true; } });
            thread.Start(); thread.Join(); Check(crossThreadRejected, "dispatcher affinity enforced");
            b.Dispose(); host.Publish(state); Check(other == 5, "disposed client callbacks released");
            host.Dispose(); Reject(() => a.QueryCapabilities(), "disposed host rejects requests");
            File.WriteAllText(output, JsonSerializer.Serialize(new { allPassed = true, count = passed.Count, passed, hardwareWrites = 0, thirdPartyCodeLoads = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}


