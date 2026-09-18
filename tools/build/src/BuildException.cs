namespace Corsac.Build;

public sealed class BuildException : Exception
{
    public BuildException(string message) : base(message) { }
}
