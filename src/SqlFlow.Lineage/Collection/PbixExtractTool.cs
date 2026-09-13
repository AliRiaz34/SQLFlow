using System.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Runs <c>tools/pbix-extract</c> over one Power BI report and reads back what it extracted.
/// <para>
/// Extraction lives in that standalone binary rather than here because a <c>.pbix</c> is
/// attacker-influenceable input: reaching its contents means running a decompressor and a SQLite reader over
/// bytes SQLFlow did not write. Keeping that parsing in a separate process, run where the report files live
/// rather than inside the control plane, keeps a hostile report away from the process holding catalog
/// credentials and warehouse reach. The only thing crossing back is YAML text.
/// </para>
/// <para>
/// It is also the ONE reader of a <c>.pbix</c> in the estate. The visual layer used to be parsed a second time
/// in C# here, which meant a fix to either reader had to be remembered twice; the tool now renders each
/// visual's question as SQL itself, so this type only locates it, runs it, and maps its output.
/// </para>
/// </summary>
internal static class PbixExtractTool
{
    /// <summary>Names the executable explicitly, for a machine that keeps it outside the search paths below.
    /// Matches the estate's other <c>SQLFLOW_*</c> settings.</summary>
    public const string PathVariable = "SQLFLOW_PBIX_EXTRACT";

    private const string ExecutableName = "pbix-extract";

    /// <summary>A report that cannot be decoded must not hang a sync, so the child is given a bounded life and
    /// killed if it outlives it. Generous enough for a large model on a slow disk.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Locates the extractor, in the order a machine is most likely to have deliberately placed it: an explicit
    /// setting, then alongside this assembly (where packaging puts it), then on PATH. Returns null when none of
    /// them holds it, which the caller reports as a warning rather than a failure.
    /// <para>
    /// Deliberately not read from the estate's own YAML: the binary is a property of the machine running the
    /// sync, not of the repository, and putting a local path in a committed document would make the estate
    /// non-portable.
    /// </para>
    /// </summary>
    public static string? Locate()
    {
        var declared = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return File.Exists(declared) ? declared : null;
        }

        var besideAssembly = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (File.Exists(besideAssembly))
        {
            return besideAssembly;
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, ExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts one report. <paramref name="reportFileLabel"/> is the name each page is tagged with, which is
    /// the report's path relative to the subscriber's declared directory so two reports built from one template
    /// (each with a "Page 1") stay distinguishable.
    /// </summary>
    /// <exception cref="PbixExtractException">The tool could not be run, failed, or produced output this cannot
    /// read. Always carries a message naming the report and what went wrong.</exception>
    public static PbixExtractResult Run(string executable, string pbixPath, string reportFileLabel)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Arguments go through ArgumentList rather than a concatenated string so a report path containing a
        // space or a quote is passed through exactly as written instead of being re-split by the runtime.
        startInfo.ArgumentList.Add(pbixPath);
        startInfo.ArgumentList.Add("--report-file");
        startInfo.ArgumentList.Add(reportFileLabel);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PbixExtractException($"'{executable}' could not be started ({ex.Message})", ex);
        }

        // Both pipes are drained concurrently: reading one to completion before the other deadlocks as soon as
        // the child fills the pipe it is not being read from, which a report producing many warnings will do.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new PbixExtractException(
                $"'{executable}' did not finish within {Timeout.TotalMinutes:0} minutes and was stopped");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var errors = standardError.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new PbixExtractException(
                $"'{executable}' failed with exit code {process.ExitCode}: {Summarize(errors)}");
        }

        return Parse(output, executable);
    }

    private static PbixExtractResult Parse(string yaml, string executable)
    {
        SpecDocument? document;
        try
        {
            document = Deserializer.Deserialize<SpecDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new PbixExtractException(
                $"'{executable}' produced output that is not valid YAML ({ex.Message})", ex);
        }

        // The tool emits one subscriber keyed by the report's name. Anything else means the contract between
        // the two has drifted, which is worth saying plainly rather than silently extracting nothing.
        if (document?.Subscribers is not { Count: 1 })
        {
            throw new PbixExtractException(
                $"'{executable}' produced no report specification; the report was not extracted");
        }

        var spec = document.Subscribers.Values.First();
        return new PbixExtractResult(spec.Report ?? [], spec.ReportWarnings ?? []);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // The child exited between the timeout and the kill, or the platform refused the signal. Either
            // way the timeout itself is what the caller is told about, so there is nothing to add here.
        }
    }

    /// <summary>Caps the tool's stderr so a runaway message cannot flood a warning.</summary>
    private static string Summarize(string errors)
    {
        var text = errors.Trim();
        if (text.Length == 0)
        {
            return "it wrote no diagnostics";
        }

        return text.Length <= 500 ? text : text[..500] + "...";
    }

    private sealed class SpecDocument
    {
        public Dictionary<string, SpecSubscriber>? Subscribers { get; set; }
    }

    private sealed class SpecSubscriber
    {
        public List<PbixPage>? Report { get; set; }

        public List<string>? ReportWarnings { get; set; }
    }
}

/// <summary>What extracting one report produced: its pages, and what the tool declined to extract.</summary>
internal sealed record PbixExtractResult(IReadOnlyList<PbixPage> Pages, IReadOnlyList<string> Warnings);

/// <summary>One page of a report, as the extractor emits it.</summary>
internal sealed class PbixPage
{
    /// <summary>The page's title as a person sees it on the tab.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>The page's internal identifier, which stays stable when the title is edited.</summary>
    public string? Name { get; set; }

    public int Ordinal { get; set; }

    /// <summary>The report file this page came from, which is part of the key a catalog row uses.</summary>
    public string? ReportFile { get; set; }

    public List<PbixVisual>? Visuals { get; set; }
}

/// <summary>One visual, with the question it asks already rendered as SQL.</summary>
internal sealed class PbixVisual
{
    public string VisualType { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string? Title { get; set; }

    /// <summary>The visual's question as one SELECT, with every filter that applies to it folded into its WHERE
    /// clause. Null only for a visual the tool could not render, which it does not emit.</summary>
    public string? Sql { get; set; }

    public List<PbixField>? Fields { get; set; }
}

/// <summary>One field a visual projects, and the role it plays in the question.</summary>
internal sealed class PbixField
{
    public string Role { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public bool IsMeasure { get; set; }
}

/// <summary>Raised when a report could not be extracted. The collector turns this into a warning, so one
/// unreadable report leaves the rest of the estate's lineage intact.</summary>
internal sealed class PbixExtractException : Exception
{
    public PbixExtractException(string message)
        : base(message)
    {
    }

    public PbixExtractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
