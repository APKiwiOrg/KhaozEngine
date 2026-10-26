using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkflowTextTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Workflow_jobs_and_embedded_commands_parse_on_every_checkout(string newline)
    {
        const string source = "jobs:\n  legs:\n    steps:\n      - name: Select\n        run: |\n          echo selected\n\n  renderer-bench:\n    env:\n      SDK: exact\n    steps:\n      - name: Check\n        run: |\n          echo checked\n";
        var workflow = new RendererPerfWorkflowText(source.Replace("\n", newline));
        Assert.Equal("    env:\n      SDK: exact\n    steps:\n      - name: Check\n        run: |\n          echo checked\n", workflow.Job("renderer-bench"));
        Assert.Equal("    steps:\n      - name: Select\n        run: |\n          echo selected\n", workflow.Job("legs").Split("\n  renderer-bench:", 2)[0]);
    }
}
