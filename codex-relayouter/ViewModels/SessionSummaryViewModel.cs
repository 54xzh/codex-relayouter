// SessionSummaryViewModel：用于 SessionsPage 列表展示的轻量 ViewModel。
using System;
using System.Linq;

namespace codex_bridge.ViewModels;

public sealed class SessionSummaryViewModel
{
    public SessionSummaryViewModel(string id, string title, DateTimeOffset createdAt, string? cwd, string? originator, string? cliVersion)
    {
        Id = id;
        Title = title;
        CreatedAt = createdAt;
        Cwd = cwd;
        Originator = originator;
        CliVersion = cliVersion;
    }

    public string Id { get; }

    public string Title { get; }

    public DateTimeOffset CreatedAt { get; }

    public string? Cwd { get; }

    public string? Originator { get; }

    public string? CliVersion { get; }

    public bool IsHidden { get; set; }

    public bool IsPinned { get; set; }

    public string Subtitle
    {
        get
        {
            var localTime = CreatedAt == default ? string.Empty : CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var meta = string.Join(
                "  ",
                new[]
                {
                    string.IsNullOrWhiteSpace(Cwd) ? null : Cwd.Trim(),
                    string.IsNullOrWhiteSpace(localTime) ? null : localTime,
                    string.IsNullOrWhiteSpace(Originator) ? null : Originator.Trim(),
                    string.IsNullOrWhiteSpace(CliVersion) ? null : CliVersion.Trim(),
                }.Where(static x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(meta))
            {
                return Id;
            }

            return meta;
        }
    }
}
