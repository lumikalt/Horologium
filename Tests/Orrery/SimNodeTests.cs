#region

using Orrery.Tree;

#endregion

namespace Tests.Orrery;

public class SimNodeTests {
    // ── Tree structure ────────────────────────────────────────────────────────

    [Fact]
    public void BuildTree_PathsAreCorrect() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        var fetch = new SimNode("fetch", core);

        Assert.Equal("top", root.Path);
        Assert.Equal("top.cpu", cpu.Path);
        Assert.Equal("top.cpu.core0", core.Path);
        Assert.Equal("top.cpu.core0.fetch", fetch.Path);
    }

    [Fact]
    public void BuildTree_DumpIsCorrect() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        _ = new SimNode("fetch", core);

        Assert.Equal(
            """
            top  [SimNode]
              cpu  [SimNode]
                core0  [SimNode]
                  fetch  [SimNode]

            """,
            root.DumpTree()
        );
    }

    [Fact]
    public void BuildTree_ChildrenAreRegistered() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);

        Assert.Single(root.Children);
        Assert.Single(cpu.Children);
        Assert.Empty(core.Children);
        Assert.Same(cpu, root.Children[0]);
        Assert.Same(core, cpu.Children[0]);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [Fact]
    public void Find_ReturnsCorrectNode() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        var fetch = new SimNode("fetch", core);

        Assert.Same(fetch, root.Find("cpu.core0.fetch"));
        Assert.Same(core, root.Find("cpu.core0"));
        Assert.Same(cpu, root.Find("cpu"));
    }

    [Fact]
    public void Find_ReturnsNullForMissingNode() {
        var root = new SimNode("top");
        _ = new SimNode("cpu", root);

        Assert.Null(root.Find("cpu.core0.fetch")); // core0 doesn't exist
        Assert.Null(root.Find("gpu"));             // gpu doesn't exist
        Assert.Null(root.Find(""));                // empty path
    }

    [Fact]
    public void Require_ThrowsForMissingNode() {
        var root = new SimNode("top");

        Assert.Throws<KeyNotFoundException>(() => root.Require("cpu"));
    }

    [Fact]
    public void Find_StillWorksAfterFinalizing() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        var fetch = new SimNode("fetch", core);

        root.BeginFinalizing();

        // Navigation is unaffected by lifecycle
        Assert.Same(fetch, root.Find("cpu.core0.fetch"));
    }

    // ── Descendants ───────────────────────────────────────────────────────────

    [Fact]
    public void Descendants_IncludesSelfAndAllChildren() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var core = new SimNode("core0", cpu);
        var fetch = new SimNode("fetch", core);

        List<SimNode> all = root.Descendants().ToList();

        Assert.Equal(4, all.Count);
        Assert.Contains(root, all);
        Assert.Contains(cpu, all);
        Assert.Contains(core, all);
        Assert.Contains(fetch, all);
    }

    // ── Lifecycle enforcement ─────────────────────────────────────────────────

    [Fact]
    public void AddChild_ThrowsAfterFinalizing() {
        var root = new SimNode("top");
        var core = new SimNode("core0", root);

        root.BeginFinalizing();

        Assert.Throws<InvalidOperationException>(() => new SimNode("late", core));
    }

    [Fact]
    public void LifecycleTransitions_MustBeInOrder() {
        var root = new SimNode("top");

        // Can't skip Building → Finalizing → Running
        Assert.Throws<InvalidOperationException>(root.BeginRunning);
        Assert.Throws<InvalidOperationException>(root.BeginFinished);

        root.BeginFinalizing();

        Assert.Throws<InvalidOperationException>(root.BeginFinalizing); // can't repeat
        Assert.Throws<InvalidOperationException>(root.BeginFinished);   // can't skip

        root.BeginRunning();
        root.BeginFinished();
    }

    [Fact]
    public void LifecycleTransition_MustBeCalledOnRoot() {
        var root = new SimNode("top");
        var child = new SimNode("cpu", root);

        Assert.Throws<InvalidOperationException>(child.BeginFinalizing);
    }

    [Fact]
    public void Lifecycle_ReflectsRootState_OnAllNodes() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var fetch = new SimNode("fetch", cpu);

        Assert.Equal(SimLifecycle.Building, fetch.Lifecycle);

        root.BeginFinalizing();
        Assert.Equal(SimLifecycle.Finalizing, fetch.Lifecycle);

        root.BeginRunning();
        Assert.Equal(SimLifecycle.Running, fetch.Lifecycle);

        root.BeginFinished();
        Assert.Equal(SimLifecycle.Finished, fetch.Lifecycle);
    }

    // ── Error conditions ──────────────────────────────────────────────────────

    [Fact]
    public void AddChild_ThrowsIfNodeAlreadyHasParent() {
        var root = new SimNode("top");
        var other = new SimNode("other");
        _ = new SimNode("cpu", root);

        // "cpu" already belongs to root — can't add to other
        Assert.Throws<InvalidOperationException>(() => other.AddChild(root.Children[0]));
    }

    [Fact]
    public void AddChild_ThrowsOnDuplicateName() {
        var root = new SimNode("top");
        _ = new SimNode("cpu", root);

        Assert.Throws<InvalidOperationException>(() => new SimNode("cpu", root));
    }

    [Fact]
    public void NodeName_ThrowsIfEmpty() {
        Assert.Throws<ArgumentException>(() => new SimNode(""));
        Assert.Throws<ArgumentException>(() => new SimNode("   "));
    }

    [Fact]
    public void NodeName_ThrowsIfContainsDot() { Assert.Throws<ArgumentException>(() => new SimNode("cpu.core0")); }
}