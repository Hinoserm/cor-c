namespace Corsac.Projects;

public static class ProjectGraph
{
    public static IReadOnlyList<EvaluatedProject> Evaluate(string root, string configuration, string? framework)
    {
        Dictionary<string, EvaluatedProject> complete = new(StringComparer.Ordinal);
        HashSet<string> active = new(StringComparer.Ordinal);
        List<EvaluatedProject> order = new();
        void Visit(string path)
        {
            path = Path.GetFullPath(path);
            if (complete.ContainsKey(path)) return;
            if (!active.Add(path)) throw new InvalidDataException("ProjectReference cycle: " + path);
            EvaluatedProject project = ProjectEvaluator.Evaluate(path, configuration, framework);
            foreach (string reference in project.References) Visit(reference);
            active.Remove(path); complete.Add(path, project); order.Add(project);
        }
        Visit(root);
        return order;
    }
}
