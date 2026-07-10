using LearnMcpTutorial.Cli;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// The CLI's flags are removed from the argument list wherever they appear, and
/// everything left over becomes the question. These tests pin that down.
/// </summary>
public class CliArgsTests
{
    [Fact]
    public void Parse_NoArguments_YieldsNoFlagsAndEmptyQuestion()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse([]);

        Assert.False(useLocal);
        Assert.False(listOnly);
        Assert.Equal("", question);
    }

    [Fact]
    public void Parse_Local_SetsUseLocal()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(["--local"]);

        Assert.True(useLocal);
        Assert.False(listOnly);
        Assert.Equal("", question);
    }

    [Fact]
    public void Parse_List_SetsListOnly()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(["--list"]);

        Assert.False(useLocal);
        Assert.True(listOnly);
        Assert.Equal("", question);
    }

    [Fact]
    public void Parse_BothFlags_SetsBoth()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(["--local", "--list"]);

        Assert.True(useLocal);
        Assert.True(listOnly);
        Assert.Equal("", question);
    }

    [Fact]
    public void Parse_JoinsRemainingArgumentsIntoQuestion()
    {
        var (_, _, question) = CliArgs.Parse(["What", "does", "HTTP", "404", "mean?"]);

        Assert.Equal("What does HTTP 404 mean?", question);
    }

    [Fact]
    public void Parse_KeepsQuestionWhenFlagsSurroundIt()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(["--local", "what", "is", "404"]);

        Assert.True(useLocal);
        Assert.False(listOnly);
        Assert.Equal("what is 404", question);
    }

    [Theory]
    [InlineData(new[] { "--local", "--list", "why" }, "why")]
    [InlineData(new[] { "--list", "--local", "why" }, "why")]
    [InlineData(new[] { "why", "--list", "--local" }, "why")]
    [InlineData(new[] { "--list", "why", "--local" }, "why")]
    public void Parse_IsIndependentOfFlagOrder(string[] args, string expectedQuestion)
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(args);

        Assert.True(useLocal);
        Assert.True(listOnly);
        Assert.Equal(expectedQuestion, question);
    }

    [Fact]
    public void Parse_TreatsUnknownFlagsAsPartOfTheQuestion()
    {
        var (useLocal, listOnly, question) = CliArgs.Parse(["--verbose", "hello"]);

        Assert.False(useLocal);
        Assert.False(listOnly);
        Assert.Equal("--verbose hello", question);
    }
}
