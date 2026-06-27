using FluentAssertions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Categorization;

/// <summary>
/// Tests for <see cref="DownloadOrganizer"/> (TASK-046, US-8 AC3): the toggle controls whether the file
/// moves (AC0), an enabled organize moves it into the category folder (AC1), and renaming a category's
/// folder via the editable rules changes where it lands (AC2).
/// </summary>
public sealed class DownloadOrganizerTests : IDisposable
{
    private readonly string _tempDir;

    public DownloadOrganizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-organize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string CreateFile(string relativePath)
    {
        string full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "payload");
        return full;
    }

    private static ISettingsService SettingsWith(bool organize, string? root)
    {
        var service = Substitute.For<ISettingsService>();
        service.Current.Returns(new AppSettings { OrganizeByCategory = organize, OrganizedRootDirectory = root });
        return service;
    }

    [Fact]
    public async Task Disabled_LeavesFileInPlace()
    {
        // AC0: toggle off → no move, original path returned.
        string file = CreateFile("movie.mp4");
        var organizer = new DownloadOrganizer(SettingsWith(organize: false, root: _tempDir), CategoryFolderRules.CreateDefault());

        string result = await organizer.OrganizeAsync(file, FileCategory.Video);

        result.Should().Be(file);
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task Enabled_MovesFileIntoCategoryFolder()
    {
        // AC1: file moved under <root>/<categoryFolder>/.
        string file = CreateFile("movie.mp4");
        var organizer = new DownloadOrganizer(SettingsWith(organize: true, root: _tempDir), CategoryFolderRules.CreateDefault());

        string result = await organizer.OrganizeAsync(file, FileCategory.Video);

        result.Should().Be(Path.Combine(_tempDir, "Video", "movie.mp4"));
        File.Exists(result).Should().BeTrue();
        File.Exists(file).Should().BeFalse("the original should have been moved");
    }

    [Fact]
    public async Task EditableRules_ChangeTheTargetFolder()
    {
        // AC2: renaming a category's folder changes where the file lands.
        string file = CreateFile("clip.mp4");
        var folders = CategoryFolderRules.CreateDefault().SetFolderName(FileCategory.Video, "Films");
        var organizer = new DownloadOrganizer(SettingsWith(organize: true, root: _tempDir), folders);

        string result = await organizer.OrganizeAsync(file, FileCategory.Video);

        result.Should().Be(Path.Combine(_tempDir, "Films", "clip.mp4"));
        File.Exists(result).Should().BeTrue();
    }

    [Fact]
    public async Task NullRoot_OrganizesInPlace_UnderTheFilesOwnDirectory()
    {
        string file = CreateFile(Path.Combine("downloads", "report.pdf"));
        var organizer = new DownloadOrganizer(SettingsWith(organize: true, root: null), CategoryFolderRules.CreateDefault());

        string result = await organizer.OrganizeAsync(file, FileCategory.Document);

        result.Should().Be(Path.Combine(_tempDir, "downloads", "Documents", "report.pdf"));
        File.Exists(result).Should().BeTrue();
    }

    [Fact]
    public async Task Collision_DeduplicatesTheName()
    {
        string file = CreateFile("song.mp3");
        // Pre-create the would-be target so the move must dedupe.
        string targetDir = Path.Combine(_tempDir, "Audio");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "song.mp3"), "existing");

        var organizer = new DownloadOrganizer(SettingsWith(organize: true, root: _tempDir), CategoryFolderRules.CreateDefault());
        string result = await organizer.OrganizeAsync(file, FileCategory.Audio);

        result.Should().Be(Path.Combine(targetDir, "song (1).mp3"));
        File.Exists(result).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "song.mp3")).Should().BeTrue("the pre-existing file is untouched");
    }

    [Fact]
    public async Task AlreadyInCategoryFolder_IsANoOp()
    {
        // File already sits in <root>/Video → returned unchanged, no nested Video/Video.
        string file = CreateFile(Path.Combine("Video", "movie.mp4"));
        var organizer = new DownloadOrganizer(SettingsWith(organize: true, root: _tempDir), CategoryFolderRules.CreateDefault());

        string result = await organizer.OrganizeAsync(file, FileCategory.Video);

        result.Should().Be(file);
        Directory.Exists(Path.Combine(_tempDir, "Video", "Video")).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
