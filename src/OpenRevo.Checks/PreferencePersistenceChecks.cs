using System.Diagnostics;
using System.Text.Json;
using OpenRevo.Core;

internal static class PreferencePersistenceChecks
{
    internal static void Run(string output)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "preference-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        void Check(string name, Action test)
        {
            try { test(); checks.Add(new { name, passed = true }); }
            catch (Exception error) { checks.Add(new { name, passed = false, error = error.ToString() }); Environment.ExitCode = 1; }
        }
        static void Assert(bool valid, string reason) { if (!valid) throw new IOException(reason); }
        foreach (bool system in new[] { false, true })
        {
            string directory = Path.Combine(root, system ? "system" : "app");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, system ? "system-settings.json" : "preferences.json");
            void Save(int number)
            {
                if (system) new SystemPreferences(LogFilter: "fixture-" + number).Save(directory);
                else AppData.Save(new AppPreferences(Theme: number % 3, GpuSavedAtUnix: number), directory);
            }
            bool Matches(int number) => system
                ? SystemPreferences.Load(directory).LogFilter == "fixture-" + number
                : AppData.Load(directory).GpuSavedAtUnix == number;
            string label = system ? "System" : "App";
            Check(label + " save recovers when a reader releases the old file", () =>
            {
                Save(1);
                using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = Task.Run(() => { Thread.Sleep(80); hold.Dispose(); });
                try { Save(2); }
                finally { release.GetAwaiter().GetResult(); }
                Assert(Matches(2), "Released reader prevented save");
                Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary file leaked");
            });
            Check(label + " persistent lock fails within a bound and preserves the old file", () =>
            {
                Save(3); string before = File.ReadAllText(path);
                using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var timer = Stopwatch.StartNew(); bool rejected = false;
                try { Save(4); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Assert(rejected && timer.Elapsed < TimeSpan.FromSeconds(3), "Unbounded wait or hidden save failure");
                Assert(File.ReadAllText(path) == before, "Old config changed after failure");
                Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary file leaked");
            });
            Check(label + " read-only destination reports failure and keeps its data", () =>
            {
                Save(5); string before = File.ReadAllText(path); var attributes = File.GetAttributes(path);
                try
                {
                    File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                    bool rejected = false;
                    try { Save(6); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                    Assert(rejected && File.ReadAllText(path) == before, "Permission failure was hidden or data changed");
                }
                finally { File.SetAttributes(path, attributes); }
                Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary file leaked");
            });
            Check(label + " concurrent writers retain complete JSON and clean temporary files", () =>
            {
                var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++) Save(worker * 50 + i + 10);
                })).ToArray();
                Task.WaitAll(writers);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Assert(json.RootElement.ValueKind == JsonValueKind.Object, "Incomplete JSON");
                Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary file leaked");
            });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { allPassed = Environment.ExitCode == 0,
            hardwareWrites = 0, userDataModified = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
