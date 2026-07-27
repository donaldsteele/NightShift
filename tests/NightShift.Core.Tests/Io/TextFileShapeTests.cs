using System.Text;
using NightShift.Core.Io;

namespace NightShift.Core.Tests.Io;

/// <summary>
/// Byte-preservation. Every one of these guards the same failure: a two-line edit that turns into
/// a whole-file diff in the user's next commit.
/// </summary>
public sealed class TextFileShapeTests
{
    static async Task<byte[]> RoundTripAsync(byte[] original)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plan.md");
        await File.WriteAllBytesAsync(path, original);

        var (text, shape) = await TextFileShape.ReadAsync(path);
        await shape.WriteAsync(path, text);

        return await File.ReadAllBytesAsync(path);
    }

    static byte[] Bytes(string text, bool bom = false)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    [Theory]
    [InlineData("# Title\r\n\r\n- [x] done\r\n- [ ] left\r\n")]
    [InlineData("# Title\n\n- [x] done\n- [ ] left\n")]
    [InlineData("no trailing newline")]
    [InlineData("")]
    [InlineData("single line\n")]
    public async Task An_unedited_round_trip_is_byte_identical(string content)
    {
        var original = Bytes(content);

        Assert.Equal(original, await RoundTripAsync(original));
    }

    [Fact]
    public async Task A_byte_order_mark_survives_a_round_trip()
    {
        // File.ReadAllTextAsync strips a BOM silently and the default write path never puts one
        // back, so without the encoding capture this loses three bytes on the first save.
        var original = Bytes("# Title\r\n", bom: true);

        var result = await RoundTripAsync(original);

        Assert.Equal(original, result);
        Assert.Equal([0xEF, 0xBB, 0xBF], result[..3]);
    }

    [Fact]
    public async Task A_file_with_no_mark_does_not_gain_one()
    {
        var result = await RoundTripAsync(Bytes("# Title\n"));

        Assert.NotEqual(0xEF, result[0]);
    }

    [Fact]
    public async Task Editing_one_line_of_a_crlf_file_changes_only_that_line()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plan.md");
        await File.WriteAllBytesAsync(path, Bytes("- [ ] first\r\n- [ ] second\r\n- [ ] third\r\n"));

        var (text, shape) = await TextFileShape.ReadAsync(path);
        await shape.WriteAsync(path, text.Replace("- [ ] second", "- [x] second", StringComparison.Ordinal));

        var written = await File.ReadAllTextAsync(path);

        Assert.Equal("- [ ] first\r\n- [x] second\r\n- [ ] third\r\n", written);
    }

    [Fact]
    public async Task Read_hands_back_lf_text_whatever_the_file_used()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plan.md");
        await File.WriteAllBytesAsync(path, Bytes("a\r\nb\r\n"));

        var (text, _) = await TextFileShape.ReadAsync(path);

        Assert.Equal("a\nb\n", text);
    }

    [Fact]
    public async Task A_mixed_file_is_rewritten_in_whichever_ending_it_had_more_of()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plan.md");
        await File.WriteAllBytesAsync(path, Bytes("a\r\nb\r\nc\n"));

        var (text, shape) = await TextFileShape.ReadAsync(path);
        await shape.WriteAsync(path, text);

        Assert.Equal("a\r\nb\r\nc\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Apply_adds_a_trailing_newline_only_when_the_file_had_one()
    {
        var kept = TextFileShape.Default with { EndsWithNewline = true };
        var dropped = TextFileShape.Default with { EndsWithNewline = false };

        Assert.Equal("x\n", kept.Apply("x"));
        Assert.Equal("x", dropped.Apply("x\n"));
    }

    [Fact]
    public async Task A_save_leaves_no_temp_file_behind()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plan.md");
        await File.WriteAllTextAsync(path, "# Title\n");

        var (text, shape) = await TextFileShape.ReadAsync(path);
        await shape.WriteAsync(path, text + "more\n");

        Assert.Equal(["plan.md"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }
}
