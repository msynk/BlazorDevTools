using BlazorDevTools.Inspection;

namespace BlazorDevTools.Tests;

public class ObjectInspectorTests
{
    private static ObjectInspector Create(Action<DevToolsOptions>? configure = null)
    {
        var options = new DevToolsOptions();
        configure?.Invoke(options);
        return new ObjectInspector(options, new Redactor(options.SensitiveNamePatterns));
    }

    private sealed class Node
    {
        public string Name { get; set; } = "root";

        public Node? Next { get; set; }

        public string Password { get; set; } = "hunter2";

        [DevToolsSensitive]
        public string Handle { get; set; } = "secret-handle";

        public List<int> Numbers { get; } = [1, 2, 3];

        public Dictionary<string, string> Map { get; } = new() { ["a"] = "x" };

        public string Throws => throw new InvalidOperationException("nope");

        public Action Callback { get; set; } = () => { };
    }

    [Fact]
    public void Redacts_by_name_and_by_attribute()
    {
        var inspector = Create();
        var children = inspector.Children(new Node(), "");
        Assert.Equal(Redactor.RedactedValue, children.Single(c => c.Name == "Password").Display);
        Assert.Equal(NodeKind.Redacted, children.Single(c => c.Name == "Handle").Kind);
        Assert.Equal("\"root\"", children.Single(c => c.Name == "Name").Display);
    }

    [Fact]
    public void Detects_cycles_along_the_expansion_path()
    {
        var inspector = Create();
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b", Next = a };
        a.Next = b;

        var level1 = inspector.Children(a, "Next");
        var next = level1.Single(c => c.Name == "Next");
        Assert.Equal(NodeKind.Circular, next.Kind);
        Assert.False(next.IsExpandable);
    }

    [Fact]
    public void Throwing_getters_are_reported_not_propagated()
    {
        var inspector = Create();
        var throws = inspector.Children(new Node(), "").Single(c => c.Name == "Throws");
        Assert.Equal(NodeKind.Error, throws.Kind);
        Assert.Contains("InvalidOperationException", throws.Display);
    }

    [Fact]
    public void Collections_and_dictionaries_are_lazy_and_capped()
    {
        var inspector = Create(o => o.MaxCollectionItems = 2);
        var root = inspector.Describe("numbers", new List<int> { 1, 2, 3, 4 });
        Assert.Equal(NodeKind.Collection, root.Kind);
        Assert.Equal(4, root.Count);
        var children = inspector.Children(new List<int> { 1, 2, 3, 4 }, "");
        Assert.Equal(3, children.Count); // 2 items + "…"
        Assert.Equal("[1]", children[1].Name);

        var dict = inspector.Children(new Node(), "Map");
        Assert.Single(dict);
        Assert.Equal("a", dict[0].Name);
        Assert.Equal("Map[a]", dict[0].Path);
    }

    [Fact]
    public void Special_values_are_summarized_not_traversed()
    {
        var inspector = Create();
        var children = inspector.Children(new Node(), "");
        var callback = children.Single(c => c.Name == "Callback");
        Assert.Equal(NodeKind.Special, callback.Kind);
        Assert.StartsWith("delegate", callback.Display);
        Assert.False(callback.IsExpandable);
    }

    [Fact]
    public void Long_strings_are_truncated()
    {
        var inspector = Create(o => o.MaxStringLength = 20);
        var node = inspector.Describe("s", new string('x', 100));
        Assert.Contains("+80 chars", node.Display);
    }

    [Fact]
    public void Flatten_and_diff_report_changed_added_and_removed_paths()
    {
        var inspector = Create();
        var before = new Node { Name = "one" };
        var flatBefore = inspector.Flatten(before);
        before.Name = "two";
        before.Numbers.Add(4);
        var flatAfter = inspector.Flatten(before);

        var diff = ObjectInspector.Diff(flatBefore, flatAfter);
        Assert.Contains(diff, d => d.Path == "Name" && d.Before == "\"one\"" && d.After == "\"two\"");
        Assert.Contains(diff, d => d.Path == "Numbers[3]" && d.Kind == "added");
        Assert.DoesNotContain(diff, d => d.Path == "Numbers"); // parent collapsed in favour of the specific child change
        Assert.Equal(Redactor.RedactedValue, flatAfter["Password"]);
    }

    [Fact]
    public void Max_depth_is_enforced()
    {
        var inspector = Create(o => o.MaxInspectionDepth = 2);
        var chain = new Node { Next = new Node { Next = new Node { Next = new Node() } } };
        var deep = inspector.Children(chain, "Next.Next");
        Assert.Equal(NodeKind.Special, deep.Single(c => c.Name == "Next").Kind);
    }
}
