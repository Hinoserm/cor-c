using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;

namespace Corsac.Build;

public sealed class TestReport
{
    private readonly ConcurrentBag<TestResult> results = new();
    public void Add(TestResult result) => results.Add(result);

    public void Write(string path)
    {
        if (results.IsEmpty) return;
        TestResult[] ordered = results.OrderBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Name).ToArray();
        XElement suite = new("testsuite", new XAttribute("name", "corsac.build"),
            new XAttribute("tests", ordered.Length), new XAttribute("failures", ordered.Count(r => r.Status is "failed" or "timed-out")),
            new XAttribute("errors", ordered.Count(r => r.Status == "error")));
        foreach (TestResult result in ordered)
        {
            XElement test = new("testcase", new XAttribute("classname", result.Target),
                new XAttribute("name", result.Name), new XAttribute("time", (result.Process?.Elapsed.TotalSeconds ?? 0).ToString("R", CultureInfo.InvariantCulture)));
            if (result.Status != "passed")
                test.Add(new XElement(result.Status == "error" ? "error" : "failure",
                    new XAttribute("type", result.Status), result.Detail ?? ""));
            if (result.Process is not null)
                test.Add(new XElement("system-out", "Logs: " + result.Process.LogPrefix + ".{out,err}.log"));
            suite.Add(test);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new XDocument(suite).Save(path);
        Console.WriteLine("tests: " + ordered.Count(r => r.Status == "passed") + " passed, "
            + ordered.Count(r => r.Status != "passed") + " failed; " + path);
    }
}
