namespace JustDownload.App.ViewModels;

/// <summary>
/// One entry in the command palette (TASK-056, PRD §2.4.2): a <see cref="Title"/>, a <see cref="Group"/> it
/// is listed under, the <see cref="Keywords"/> it also matches on, and the <see cref="Action"/> it runs. The
/// palette filters these by a fuzzy-ish substring match over the title and keywords.
/// </summary>
public sealed class PaletteCommand
{
    private readonly Action _run;

    /// <summary>Creates a palette command.</summary>
    public PaletteCommand(string title, string group, Action run, params string[] keywords)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(keywords);
        Title = title;
        Group = group;
        _run = run;
        Keywords = keywords;
    }

    /// <summary>The command label shown in the palette.</summary>
    public string Title { get; }

    /// <summary>The section the command is grouped under (e.g. "Actions", "Jump to").</summary>
    public string Group { get; }

    /// <summary>Extra terms the command matches on beyond its title.</summary>
    public IReadOnlyList<string> Keywords { get; }

    /// <summary>Runs the command's action.</summary>
    public void Run() => _run();

    /// <summary>
    /// Whether the command matches <paramref name="query"/>: an empty query matches everything; otherwise the
    /// query must appear (case-insensitively) in the title or any keyword.
    /// </summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string q = query.Trim();
        if (Title.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string keyword in Keywords)
        {
            if (keyword.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
