namespace Corsac.Build;

public sealed class BuildGraph
{
    public List<BuildTarget> Ordered { get; } = new();

    public BuildGraph(BuildTarget root)
    {
        // First collect selected nodes, then add sequence edges. This exposes
        // cycles caused by a later step being a prerequisite of an earlier one.
        HashSet<BuildTarget> selected = new();
        void Collect(BuildTarget target)
        {
            if (!selected.Add(target)) return;
            foreach (BuildTarget child in target.Dependencies.Concat(target.Steps)) Collect(child);
        }
        Collect(root);
        foreach (BuildTarget target in selected)
        {
            for (int i = 0; i < target.Steps.Count; i++)
            {
                target.Steps[i].OrderAfter.AddRange(target.Dependencies);
                if (i > 0) target.Steps[i].OrderAfter.Add(target.Steps[i - 1]);
            }
        }
        Dictionary<BuildTarget, int> state = new();
        List<string> path = new();
        void Visit(BuildTarget target)
        {
            if (state.TryGetValue(target, out int status))
            {
                if (status == 1) throw new BuildException("Target cycle: " + string.Join(" -> ", path.Append(target.Path)));
                return;
            }
            state[target] = 1;
            path.Add(target.Path);
            foreach (BuildTarget dependency in target.Edges) Visit(dependency);
            path.RemoveAt(path.Count - 1);
            state[target] = 2;
            Ordered.Add(target);
        }
        Visit(root);
    }
}
