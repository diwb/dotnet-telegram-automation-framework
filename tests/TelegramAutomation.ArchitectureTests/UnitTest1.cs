namespace TelegramAutomation.ArchitectureTests;

public class ArchitectureBoundaryTests
{
    [Fact]
    public void Core_project_does_not_reference_telegram_bot_package()
    {
        var core = File.ReadAllText(Path.Combine(FindRoot(), "src", "TelegramAutomation.Core", "TelegramAutomation.Core.csproj"));
        Assert.DoesNotContain("Telegram.Bot", core, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fake_api_is_not_referenced_by_core_abstractions_polling_or_webhooks()
    {
        var root = FindRoot();
        var projects = new[] { "TelegramAutomation.Core", "TelegramAutomation.Abstractions", "TelegramAutomation.Polling", "TelegramAutomation.Webhooks" };
        foreach (var project in projects)
        {
            var file = Path.Combine(root, "src", project, project + ".csproj");
            Assert.DoesNotContain("FakeTelegramApi", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Cli_does_not_reference_fake_api_or_storage_internals()
    {
        var cli = File.ReadAllText(Path.Combine(FindRoot(), "src", "TelegramAutomation.Cli", "TelegramAutomation.Cli.csproj"));
        Assert.DoesNotContain("FakeTelegramApi", cli, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InternalsVisibleTo", cli, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TelegramAutomation.Abstractions")]
    [InlineData("TelegramAutomation.Core")]
    [InlineData("TelegramAutomation.Telegram")]
    [InlineData("TelegramAutomation.Polling")]
    [InlineData("TelegramAutomation.Webhooks")]
    [InlineData("TelegramAutomation.Persistence.SQLite")]
    [InlineData("TelegramAutomation.Storage.InMemory")]
    [InlineData("TelegramAutomation.Storage.Sqlite")]
    [InlineData("TelegramAutomation.Scheduling")]
    [InlineData("TelegramAutomation.Observability")]
    [InlineData("TelegramAutomation.FakeTelegramApi")]
    [InlineData("TelegramAutomation.Cli")]
    [InlineData("TelegramAutomation.Samples.EchoBot")]
    [InlineData("TelegramAutomation.Samples.WorkflowBot")]
    [InlineData("TelegramAutomation.Samples.WebhookHost")]
    public void Expected_project_exists(string project)
    {
        var root = FindRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "src", project)), project);
    }

    [Fact]
    public void Gitignore_blocks_generated_outputs_and_secrets()
    {
        var gitignore = File.ReadAllText(Path.Combine(FindRoot(), ".gitignore"));
        foreach (var pattern in new[] { "bin/", "obj/", "TestResults/", "artifacts/", ".env" }) Assert.Contains(pattern, gitignore);
    }

    private static string FindRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "TelegramAutomation.slnx"))) return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
