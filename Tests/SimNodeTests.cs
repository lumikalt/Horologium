using Orrery.Tree;

namespace Tests;

public class SimNodeTests {
    [Fact]
    public void BuildTree() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        var fetch = new SimNode("fetch", core);

        Assert.Equal(
            """
            top  [SimNode]
              cpu  [SimNode]
                core0  [SimNode]
                  fetch  [SimNode]
            """,
            root.DumpTree()
        );
        
        Assert.Equal("top.cpu.core0.fetch", fetch.Path);
        Assert.Equal("top", root.Path);
        Assert.Equal("top.cpu", cpu.Path);
        Assert.Equal("top.cpu.core0", core.Path);
        Assert.Equal("top.cpu.core0.fetch", fetch.Path);
        
        Assert.Equal("top.cpu.core0.fetch", root.Find("cpu.core0.fetch")?.Path);

        root.BeginFinalizing();
        
        Assert.Null(root.Find("cpu.core0.fetch"));
        
        Assert.Throws<InvalidOperationException>(() => new SimNode("late", core));
    }
}