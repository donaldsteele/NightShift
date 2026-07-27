using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class RepositoryNameTests : IDisposable
{
    readonly TempDirectory _temp = new();

    string CreateRepo(string name, string? originUrl)
    {
        var directory = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(Path.Combine(directory, ".git"));

        if (originUrl is not null)
        {
            File.WriteAllText(
                Path.Combine(directory, ".git", "config"),
                $"""
                 [core]
                 	repositoryformatversion = 0
                 [remote "origin"]
                 	url = {originUrl}
                 	fetch = +refs/heads/*:refs/remotes/origin/*
                 [branch "main"]
                 	remote = origin
                 """);
        }

        return directory;
    }

    [Theory]
    [InlineData("https://github.com/donaldsteele/NightShift.git", "NightShift")]
    [InlineData("https://github.com/donaldsteele/NightShift", "NightShift")]
    [InlineData("git@github.com:donaldsteele/NightShift.git", "NightShift")]
    [InlineData("ssh://git@github.com/owner/some-repo.git", "some-repo")]
    [InlineData("https://gitlab.com/group/sub/deep-repo.git", "deep-repo")]
    [InlineData("C:\\srv\\bare\\local-repo.git", "local-repo")]
    [InlineData("https://github.com/owner/trailing/", "trailing")]
    public void The_origin_remote_name_wins(string url, string expected) =>
        Assert.Equal(expected, RepositoryName.Resolve(CreateRepo("dir-name-ignored", url)));

    [Fact]
    public void It_falls_back_to_the_directory_name_without_a_remote() =>
        Assert.Equal("my-project", RepositoryName.Resolve(CreateRepo("my-project", originUrl: null)));

    [Fact]
    public void It_falls_back_to_the_directory_name_outside_a_repository()
    {
        var directory = Path.Combine(_temp.Path, "not-a-repo");
        Directory.CreateDirectory(directory);

        Assert.Equal("not-a-repo", RepositoryName.Resolve(directory));
    }

    [Fact]
    public void A_directory_name_with_spaces_becomes_a_safe_token() =>
        // Spaces are ordinary on Windows and would otherwise need quoting on the command line.
        Assert.Equal("Weather-on-the-8-s", RepositoryName.Resolve(CreateRepo("Weather on the 8's", null)));

    [Fact]
    public void A_config_naming_another_remote_first_still_finds_origin()
    {
        var directory = Path.Combine(_temp.Path, "multi-remote");
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        File.WriteAllText(
            Path.Combine(directory, ".git", "config"),
            """
            [remote "upstream"]
            	url = https://github.com/someone-else/upstream-repo.git
            [remote "origin"]
            	url = https://github.com/me/my-fork.git
            """);

        Assert.Equal("my-fork", RepositoryName.Resolve(directory));
    }

    [Fact]
    public void A_config_with_no_origin_falls_back_to_the_directory()
    {
        var directory = Path.Combine(_temp.Path, "only-upstream");
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        File.WriteAllText(
            Path.Combine(directory, ".git", "config"),
            """
            [remote "upstream"]
            	url = https://github.com/someone-else/upstream-repo.git
            """);

        Assert.Equal("only-upstream", RepositoryName.Resolve(directory));
    }

    [Fact]
    public void An_unreadable_config_is_not_fatal()
    {
        var directory = CreateRepo("locked", "https://github.com/o/r.git");
        var config = Path.Combine(directory, ".git", "config");

        using var hold = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None);

        // Falls back rather than throwing; a name is a nicety, not a precondition for running.
        Assert.Equal("locked", RepositoryName.Resolve(directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_gives_nothing_out(string? directory) =>
        Assert.Null(RepositoryName.Resolve(directory));

    [Fact]
    public void The_name_is_capped()
    {
        var name = RepositoryName.Resolve(CreateRepo(new string('a', 200), null));

        Assert.NotNull(name);
        Assert.True(name.Length <= RepositoryName.MaxLength);
    }

    public void Dispose() => _temp.Dispose();
}
