using System.Windows.Automation;

// A first-miss observation only. These records never select or activate controls.
internal sealed class ShellTrayObservation
{
    internal const int MaximumNodes = 128, MaximumDepth = 16, MaximumIdentityLength = 64;
    public string Timing => "first-miss";
    public bool Completed
    {
        get; private set;
    }
    public bool Truncated
    {
        get; private set;
    }
    public string? ErrorType
    {
        get; private set;
    }
    public Node[] Nodes { get; private set; } = [];
    internal sealed record Node(int Parent, int Depth, int ControlType, string ClassName, string AutomationId,
        bool IdentityTruncated, bool IsOffscreen, string NameClass);

    internal void Capture(IEnumerable<AutomationElement> roots, CancellationToken ct)
    {
        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Visit(root, -1, 0))
                    break;
            }
            ct.ThrowIfCancellationRequested();
            Completed = !Truncated;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ErrorType = error.GetType().Name;
            if (error is OperationCanceledException)
                throw;
        }

        bool Visit(AutomationElement element, int parent, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (Nodes.Length == MaximumNodes)
            {
                Truncated = true;
                return false;
            }
            var current = element.Current;
            string className = current.ClassName, automationId = current.AutomationId;
            string nameClass = current.Name switch
            {
                "" => "empty",
                "TypeLay" => "product-exact",
                string name when name.StartsWith("TypeLay · ", StringComparison.Ordinal) => "product-prefix",
                string name when name.Contains("TypeLay", StringComparison.Ordinal) => "product-contained",
                "Show hidden icons" or "显示隐藏的图标" => "known-overflow",
                _ => "other"
            };
            int index = Nodes.Length;
            Nodes = [.. Nodes, new(parent, depth, current.ControlType.Id, Bounded(className), Bounded(automationId),
                className.Length > MaximumIdentityLength || automationId.Length > MaximumIdentityLength,
                current.IsOffscreen, nameClass)];
            ct.ThrowIfCancellationRequested();
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            if (child is not null && depth == MaximumDepth)
            {
                Truncated = true;
                return true;
            }
            while (child is not null)
            {
                if (!Visit(child, index, depth + 1))
                    return false;
                ct.ThrowIfCancellationRequested();
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            return true;
        }
    }

    private static string Bounded(string value) => value[..Math.Min(value.Length, MaximumIdentityLength)];
}
